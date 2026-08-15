using System.Security.Claims;

namespace SyntaxCircus.Blazor.Auth;

public interface IUserTokenCacheKeyProvider
{
    string? GetCacheKey(ClaimsPrincipal? principal);

    string? GetCacheKey(string? subject);
}
