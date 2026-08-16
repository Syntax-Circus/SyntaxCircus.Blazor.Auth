using System.Text;

namespace SyntaxCircus.Blazor.Auth.Tests;

public class OidcTokenExpiryTests
{
    private static string EncodeBase64Url(string json)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string BuildJwt(long expUnixSeconds)
        => $"{EncodeBase64Url("{\"alg\":\"none\"}")}.{EncodeBase64Url($"{{\"exp\":{expUnixSeconds}}}")}.signature";

    [Fact]
    public void TryParse_ValidIsoDate_ReturnsTrue()
    {
        var result = OidcTokenExpiry.TryParse("2026-03-15T12:30:00Z", out var expiry);

        result.ShouldBeTrue();
        expiry.ShouldBe(new DateTimeOffset(2026, 3, 15, 12, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryParse_NullOrInvalid_ReturnsFalse()
    {
        OidcTokenExpiry.TryParse(null, out _).ShouldBeFalse();
        OidcTokenExpiry.TryParse("not-a-date", out _).ShouldBeFalse();
    }

    [Fact]
    public void Resolve_ExplicitExpiresAtProvided_UsesIt()
    {
        var now = DateTimeOffset.UtcNow;

        var resolved = OidcTokenExpiry.Resolve("2026-03-15T12:30:00Z", "9999", "irrelevant-token", now, TimeSpan.FromMinutes(5));

        resolved.ShouldBe(new DateTimeOffset(2026, 3, 15, 12, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Resolve_NoExpiresAt_UsesExpiresInSeconds()
    {
        var now = DateTimeOffset.UtcNow;

        var resolved = OidcTokenExpiry.Resolve(null, "3600", null, now, TimeSpan.FromMinutes(5));

        resolved.ShouldBe(now.AddSeconds(3600));
    }

    [Fact]
    public void Resolve_ExpiresInZeroOrNegative_FallsThroughToNextSource()
    {
        var now = DateTimeOffset.UtcNow;
        var jwt = BuildJwt(now.AddHours(2).ToUnixTimeSeconds());

        var resolved = OidcTokenExpiry.Resolve(null, "0", jwt, now, TimeSpan.FromMinutes(5));

        resolved.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(now.AddHours(2).ToUnixTimeSeconds()));
    }

    [Fact]
    public void Resolve_NoExpiresAtOrExpiresIn_UsesJwtExpClaim()
    {
        var now = DateTimeOffset.UtcNow;
        var expUnix = now.AddHours(1).ToUnixTimeSeconds();
        var jwt = BuildJwt(expUnix);

        var resolved = OidcTokenExpiry.Resolve(null, null, jwt, now, TimeSpan.FromMinutes(5));

        resolved.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(expUnix));
    }

    [Fact]
    public void Resolve_TokenIsNotAJwt_FallsBackToFallbackLifetime()
    {
        var now = DateTimeOffset.UtcNow;

        var resolved = OidcTokenExpiry.Resolve(null, null, "not-a-jwt-token", now, TimeSpan.FromMinutes(10));

        resolved.ShouldBe(now.AddMinutes(10));
    }

    [Fact]
    public void Resolve_JwtPayloadIsMalformedBase64_FallsBackToFallbackLifetime()
    {
        var now = DateTimeOffset.UtcNow;
        var malformedJwt = "header.not!valid!base64.signature";

        var resolved = OidcTokenExpiry.Resolve(null, null, malformedJwt, now, TimeSpan.FromMinutes(10));

        resolved.ShouldBe(now.AddMinutes(10));
    }

    [Fact]
    public void Resolve_JwtPayloadMissingExpClaim_FallsBackToFallbackLifetime()
    {
        var now = DateTimeOffset.UtcNow;
        var jwt = $"{EncodeBase64Url("{\"alg\":\"none\"}")}.{EncodeBase64Url("{\"sub\":\"user1\"}")}.signature";

        var resolved = OidcTokenExpiry.Resolve(null, null, jwt, now, TimeSpan.FromMinutes(10));

        resolved.ShouldBe(now.AddMinutes(10));
    }

    [Fact]
    public void Resolve_NoSourcesAvailable_UsesFiveMinuteDefaultWhenFallbackIsZero()
    {
        var now = DateTimeOffset.UtcNow;

        var resolved = OidcTokenExpiry.Resolve(null, null, null, now, TimeSpan.Zero);

        resolved.ShouldBe(now.AddMinutes(5));
    }

    [Fact]
    public void Resolve_AllArgumentsNull_UsesFallbackLifetime()
    {
        var now = DateTimeOffset.UtcNow;

        var resolved = OidcTokenExpiry.Resolve(null, null, null, now, TimeSpan.FromMinutes(15));

        resolved.ShouldBe(now.AddMinutes(15));
    }
}
