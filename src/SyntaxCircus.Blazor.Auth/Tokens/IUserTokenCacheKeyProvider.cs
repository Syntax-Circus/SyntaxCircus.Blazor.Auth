using System.Security.Claims;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>Chooses the <see cref="IServerTokenCache"/> key for a signed-in user.</summary>
public interface IUserTokenCacheKeyProvider
{
    /// <summary>
    /// The cache key for this principal's sign-in session, or <see langword="null"/> when it has none.
    /// The package resolves every token through this overload, on the HTTP-request and circuit paths
    /// alike. Use it for anything that must match the package's key, such as
    /// <see cref="SessionStateService.Observe"/> or a direct <see cref="IServerTokenCache"/> call.
    /// </summary>
    string? GetCacheKey(ClaimsPrincipal? principal);

    /// <summary>
    /// The per-user key for <paramref name="subject"/>. A subject alone cannot identify a sign-in
    /// session, so for a signed-in user this is <b>not</b> the key the package uses: the default
    /// provider's per-user key holds tokens only for sessions without a session id (cookies issued
    /// before version 0.3.0, or schemes whose sign-in the package could not hook). The package itself
    /// does not call this overload. It remains for source compatibility.
    /// </summary>
    string? GetCacheKey(string? subject);
}
