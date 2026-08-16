namespace SyntaxCircus.Blazor.Auth.Tests;

public class ServerTokenCacheEntryTests
{
    [Fact]
    public void IsUsable_AccessTokenPresentAndNotNearExpiry_ReturnsTrue()
    {
        var entry = new ServerTokenCacheEntry("token", null, null, DateTimeOffset.UtcNow.AddMinutes(10));

        entry.IsUsable(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60)).ShouldBeTrue();
    }

    [Fact]
    public void IsUsable_WithinRefreshSkew_ReturnsFalse()
    {
        var entry = new ServerTokenCacheEntry("token", null, null, DateTimeOffset.UtcNow.AddSeconds(30));

        entry.IsUsable(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60)).ShouldBeFalse();
    }

    [Fact]
    public void IsUsable_AlreadyExpired_ReturnsFalse()
    {
        var entry = new ServerTokenCacheEntry("token", null, null, DateTimeOffset.UtcNow.AddMinutes(-1));

        entry.IsUsable(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60)).ShouldBeFalse();
    }

    [Fact]
    public void IsUsable_EmptyAccessToken_ReturnsFalse()
    {
        var entry = new ServerTokenCacheEntry(string.Empty, null, null, DateTimeOffset.UtcNow.AddMinutes(10));

        entry.IsUsable(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60)).ShouldBeFalse();
    }
}
