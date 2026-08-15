using System.Security.Claims;

namespace SyntaxCircus.Blazor.Auth;

public sealed class UserTokenCacheKeyProvider : IUserTokenCacheKeyProvider
{
    public string? GetCacheKey(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return GetCacheKey(ResolveSubject(principal));
    }

    public string? GetCacheKey(string? subject)
        => string.IsNullOrWhiteSpace(subject) ? null : $"user:{subject.Trim()}";

    internal static string? ResolveSubject(ClaimsPrincipal principal)
        => principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
}
