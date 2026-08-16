namespace SyntaxCircus.Blazor.Auth.Tests;

public class OidcTokenRefreshResultTests
{
    [Fact]
    public void ExpiresAtTokenValue_FormatsAsRoundTripUtc()
    {
        var expiresAt = new DateTimeOffset(2026, 3, 15, 12, 30, 0, TimeSpan.Zero);
        var result = new OidcTokenRefreshResult("access", "refresh", "id", expiresAt);

        result.ExpiresAtTokenValue.ShouldBe("2026-03-15T12:30:00.0000000Z");
    }

    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var result = new OidcTokenRefreshResult("access", "refresh", "id", expiresAt);

        result.AccessToken.ShouldBe("access");
        result.RefreshToken.ShouldBe("refresh");
        result.IdToken.ShouldBe("id");
        result.ExpiresAt.ShouldBe(expiresAt);
    }
}
