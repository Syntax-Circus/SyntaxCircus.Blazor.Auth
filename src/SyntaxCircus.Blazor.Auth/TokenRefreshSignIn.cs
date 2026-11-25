using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Marks the cookie re-sign <see cref="ServerRequestOidcTokenResolver"/> performs to persist refreshed
/// tokens, so <see cref="SessionIdCookieHook"/> can tell it from a sign-in and report back that it ran.
/// Matched on the <see cref="AuthenticationProperties"/> instance, so another sign-in made while the
/// re-sign is in progress is not mistaken for it.
/// </summary>
internal sealed class TokenRefreshSignIn(AuthenticationProperties properties)
{
    private static readonly object ItemKey = new();

    private AuthenticationProperties Properties { get; } = properties;

    public bool HookRan { get; set; }

    /// <summary>Runs <paramref name="signIn"/> under the marker and returns whether the hook saw it.</summary>
    public static async Task<bool> RunAsync(HttpContext httpContext, AuthenticationProperties properties, Func<Task> signIn)
    {
        var marker = new TokenRefreshSignIn(properties);
        httpContext.Items[ItemKey] = marker;
        try
        {
            await signIn().ConfigureAwait(false);
        }
        finally
        {
            httpContext.Items.Remove(ItemKey);
        }

        return marker.HookRan;
    }

    public static TokenRefreshSignIn? Find(CookieSigningInContext context)
        => context.HttpContext.Items.TryGetValue(ItemKey, out var value)
            && value is TokenRefreshSignIn marker
            && ReferenceEquals(marker.Properties, context.Properties)
                ? marker
                : null;
}
