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

        var subject = ResolveSubject(principal);
        var userKey = GetCacheKey(subject);
        var sessionId = SessionId.Read(principal);
        // The id comes first and cannot contain ':', and the prefix differs from the per-user one, so no
        // (subject, id) pair can produce another pair's key or any subject's per-user key.
        return userKey is null || sessionId is null ? userKey : $"user-session:{sessionId}:{subject!.Trim()}";
    }

    public string? GetCacheKey(string? subject)
        => string.IsNullOrWhiteSpace(subject) ? null : $"user:{subject.Trim()}";

    internal static string? ResolveSubject(ClaimsPrincipal principal)
        => principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
}
