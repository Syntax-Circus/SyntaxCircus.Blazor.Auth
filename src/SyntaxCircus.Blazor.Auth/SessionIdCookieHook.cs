using Microsoft.AspNetCore.Authentication.Cookies;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Wraps a cookie scheme's events so every new sign-in gets its own session id, and a principal the
/// host rebuilds for the same session keeps it. The host's handlers run first.
/// </summary>
internal static class SessionIdCookieHook
{
    /// <summary>
    /// Wraps <see cref="CookieAuthenticationEvents.OnSigningIn"/> and
    /// <see cref="CookieAuthenticationEvents.OnValidatePrincipal"/> unless already wrapped. An events
    /// instance can be post-configured more than once (shared between schemes, or reused when options
    /// are rebuilt), and the wrappers must not stack.
    /// </summary>
    internal static void Attach(CookieAuthenticationEvents events)
    {
        if (events.OnSigningIn.Target is not SigningInWrapper)
        {
            events.OnSigningIn = new SigningInWrapper(events.OnSigningIn).InvokeAsync;
        }

        if (events.OnValidatePrincipal.Target is not ValidatePrincipalWrapper)
        {
            events.OnValidatePrincipal = new ValidatePrincipalWrapper(events.OnValidatePrincipal).InvokeAsync;
        }
    }

    private static void Assign(CookieSigningInContext context, string? incoming)
    {
        if (context.Principal is null)
        {
            return;
        }

        var sessionId = SessionId.Read(context.Principal) ?? incoming;
        if (TokenRefreshSignIn.Find(context) is { } refresh)
        {
            // This package re-signing a cookie to store refreshed tokens is not a new sign-in, so it
            // never creates an id. A cookie without one (issued before the upgrade) must stay that way:
            // a circuit that already holds its principal keeps using the per-user key, and moving only
            // the cookie to a new key would leave the two refreshing the same rotating refresh token
            // from different cache entries.
            refresh.HookRan = true;
            if (sessionId is null)
            {
                context.Properties.Items.TryGetValue(SessionId.PropertiesKey, out sessionId);
            }
        }
        else if (sessionId is null)
        {
            // The host continuing a cookie's session keeps what that cookie had, including no id, for
            // the same reason. Anything else is a new session.
            sessionId = CookieRead.ContinuedBy(context) is { } read ? read.SessionId : SessionId.Create();
        }

        // Also with no id: a claim of this type that is not ours never stays in the cookie.
        context.Principal = SessionId.Apply(context.Principal, sessionId);
        if (sessionId is not null)
        {
            context.Properties.Items[SessionId.PropertiesKey] = sessionId;
        }
    }

    private sealed class SigningInWrapper(Func<CookieSigningInContext, Task> next)
    {
        public async Task InvokeAsync(CookieSigningInContext context)
        {
            // Read before the host's handler runs, in case it rebuilds the principal on a re-sign.
            var incoming = SessionId.Read(context.Principal);
            await next(context).ConfigureAwait(false);
            Assign(context, incoming);
        }
    }

    private sealed class ValidatePrincipalWrapper(Func<CookieValidatePrincipalContext, Task> next)
    {
        public async Task InvokeAsync(CookieValidatePrincipalContext context)
        {
            var incoming = SessionId.Read(context.Principal);
            // Before the host's handler runs: it may replace the principal, or sign the cookie in again.
            CookieRead.Remember(context, incoming);
            await next(context).ConfigureAwait(false);

            // A replaced principal (ASP.NET Core Identity's security-stamp check, for one) is renewed
            // without SigningIn, so it is carried over here. A rejected one is null and stays that way.
            if (incoming is not null && context.Principal is not null && SessionId.Read(context.Principal) is null)
            {
                context.ReplacePrincipal(SessionId.Apply(context.Principal, incoming));
            }
        }
    }
}
