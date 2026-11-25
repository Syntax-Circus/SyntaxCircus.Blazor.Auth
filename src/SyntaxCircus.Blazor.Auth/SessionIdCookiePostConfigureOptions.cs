using Microsoft.AspNetCore.Authentication.Cookies;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Adds <see cref="SessionIdCookieHook"/> to every cookie scheme. Post-configuration runs after all
/// <c>Configure</c> callbacks, so it does not matter whether <c>AddCookie</c> is called before or
/// after <c>AddBlazorTokenForwarding</c>. Wrapping the <c>OnSigningIn</c> delegate is not enough in
/// two cases, which are forwarded through a separate events instance instead: a host that sets
/// <c>EventsType</c> (the handler then ignores <c>Options.Events</c>), and an events subclass that
/// overrides <c>SigningIn</c> (which need not call the delegate).
/// </summary>
internal sealed class SessionIdCookiePostConfigureOptions : IPostConfigureOptions<CookieAuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.EventsType is { } eventsType)
        {
            // A type that is not cookie events is a misconfiguration the cookie handler itself rejects.
            if (eventsType.IsAssignableTo(typeof(CookieAuthenticationEvents))
                && !(eventsType.IsGenericType && eventsType.GetGenericTypeDefinition() == typeof(SessionIdCookieEvents<>)))
            {
                options.EventsType = typeof(SessionIdCookieEvents<>).MakeGenericType(eventsType);
            }

            return;
        }

        if (OverridesSigningIn(options.Events))
        {
            options.Events = Forward(options.Events);
        }

        SessionIdCookieHook.Attach(options.Events);
    }

    internal static CookieAuthenticationEvents Forward(CookieAuthenticationEvents inner)
    {
        var events = new CookieAuthenticationEvents();
        Forward(inner, events);
        return events;
    }

    /// <summary>Points every delegate of <paramref name="events"/> at <paramref name="inner"/>'s virtual methods.</summary>
    internal static void Forward(CookieAuthenticationEvents inner, CookieAuthenticationEvents events)
    {
        events.OnValidatePrincipal = inner.ValidatePrincipal;
        events.OnCheckSlidingExpiration = inner.CheckSlidingExpiration;
        events.OnSigningIn = inner.SigningIn;
        events.OnSignedIn = inner.SignedIn;
        events.OnSigningOut = inner.SigningOut;
        events.OnRedirectToLogin = inner.RedirectToLogin;
        events.OnRedirectToAccessDenied = inner.RedirectToAccessDenied;
        events.OnRedirectToLogout = inner.RedirectToLogout;
        events.OnRedirectToReturnUrl = inner.RedirectToReturnUrl;
    }

    private static bool OverridesSigningIn(CookieAuthenticationEvents events)
    {
        // A delegate over a virtual method binds to the override the instance actually dispatches to.
        Func<CookieSigningInContext, Task> signingIn = events.SigningIn;
        return signingIn.Method.DeclaringType != typeof(CookieAuthenticationEvents);
    }
}

/// <summary>
/// Stands in for a host's <c>EventsType</c>: resolves that type from the request's services, as the
/// cookie handler would have, and adds the session-id hook around it.
/// </summary>
internal sealed class SessionIdCookieEvents<TEvents> : CookieAuthenticationEvents
    where TEvents : CookieAuthenticationEvents
{
    public SessionIdCookieEvents(TEvents inner)
    {
        SessionIdCookiePostConfigureOptions.Forward(inner, this);
        SessionIdCookieHook.Attach(this);
    }
}
