namespace SyntaxCircus.Blazor.Auth.Tests;

public class UserTokenCacheKeyProviderTests
{
    private readonly UserTokenCacheKeyProvider _provider = new();

    private static ClaimsPrincipal CreateAuthenticatedPrincipal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "TestAuth"));

    [Fact]
    public void GetCacheKey_Principal_AnonymousUser_ReturnsNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        _provider.GetCacheKey(principal).ShouldBeNull();
    }

    [Fact]
    public void GetCacheKey_Principal_Null_ReturnsNull()
        => _provider.GetCacheKey((ClaimsPrincipal?)null).ShouldBeNull();

    [Fact]
    public void GetCacheKey_Principal_WithSubClaim_UsesSubClaim()
    {
        var principal = CreateAuthenticatedPrincipal(new Claim("sub", "user-123"));

        _provider.GetCacheKey(principal).ShouldBe("user:user-123");
    }

    [Fact]
    public void GetCacheKey_Principal_NoSubClaim_FallsBackToNameIdentifier()
    {
        var principal = CreateAuthenticatedPrincipal(new Claim(ClaimTypes.NameIdentifier, "user-456"));

        _provider.GetCacheKey(principal).ShouldBe("user:user-456");
    }

    [Fact]
    public void GetCacheKey_Principal_SubClaimPreferredOverNameIdentifier()
    {
        var principal = CreateAuthenticatedPrincipal(
            new Claim("sub", "sub-user"),
            new Claim(ClaimTypes.NameIdentifier, "nameid-user"));

        _provider.GetCacheKey(principal).ShouldBe("user:sub-user");
    }

    [Fact]
    public void GetCacheKey_Principal_AuthenticatedButNoSubjectClaim_ReturnsNull()
    {
        var principal = CreateAuthenticatedPrincipal(new Claim("other", "value"));

        _provider.GetCacheKey(principal).ShouldBeNull();
    }

    [Fact]
    public void GetCacheKey_Subject_Null_ReturnsNull()
        => _provider.GetCacheKey((string?)null).ShouldBeNull();

    [Fact]
    public void GetCacheKey_Subject_Whitespace_ReturnsNull()
        => _provider.GetCacheKey("   ").ShouldBeNull();

    [Fact]
    public void GetCacheKey_Subject_TrimsAndPrefixes()
        => _provider.GetCacheKey("  user-789  ").ShouldBe("user:user-789");
}
