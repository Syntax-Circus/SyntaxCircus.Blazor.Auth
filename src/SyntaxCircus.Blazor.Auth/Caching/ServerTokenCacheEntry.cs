namespace SyntaxCircus.Blazor.Auth;

public sealed record ServerTokenCacheEntry(
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsUsable(DateTimeOffset nowUtc, TimeSpan refreshSkew)
        => !string.IsNullOrWhiteSpace(AccessToken) && ExpiresAtUtc - refreshSkew > nowUtc;
}
