using System.Globalization;

namespace SyntaxCircus.Blazor.Auth;

public sealed record OidcTokenRefreshResult(
    string AccessToken,
    string RefreshToken,
    string? IdToken,
    DateTimeOffset ExpiresAt)
{
    public string ExpiresAtTokenValue => ExpiresAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
}
