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

    private const string SessionClaimType = "urn:syntaxcircus:blazor-auth:session-id";
    private static readonly string IdA = new string('a', 42) + "A";
    private static readonly string IdB = new string('b', 42) + "A";

    private static Claim SessionClaim(string value, string issuer = "SyntaxCircus.Blazor.Auth")
        => new(SessionClaimType, value, ClaimValueTypes.String, issuer);

    [Fact]
    public void GetCacheKey_Principal_WithSessionId_IsUniquePerSubjectAndSession()
    {
        var keys = new[]
        {
            _provider.GetCacheKey(CreateAuthenticatedPrincipal(new Claim("sub", "u1"), SessionClaim(IdA))),
            _provider.GetCacheKey(CreateAuthenticatedPrincipal(new Claim("sub", "u1"), SessionClaim(IdB))),
            _provider.GetCacheKey(CreateAuthenticatedPrincipal(new Claim("sub", "u2"), SessionClaim(IdA))),
            _provider.GetCacheKey(CreateAuthenticatedPrincipal(new Claim("sub", "u1"))),
        };

        keys.ShouldBeUnique();
        keys[0].ShouldBe($"user-session:{IdA}:u1");
    }

    [Fact]
    public void GetCacheKey_SubjectCraftedToLookLikeASessionKey_CannotCollideWithOne()
    {
        var sessionKey = _provider.GetCacheKey(CreateAuthenticatedPrincipal(new Claim("sub", "victim"), SessionClaim(IdA)))!;

        _provider.GetCacheKey(sessionKey).ShouldNotBe(sessionKey);
        _provider.GetCacheKey(CreateAuthenticatedPrincipal(new Claim("sub", sessionKey))).ShouldNotBe(sessionKey);
        _provider.GetCacheKey(CreateAuthenticatedPrincipal(new Claim("sub", $"{IdA}:victim"))).ShouldNotBe(sessionKey);
    }

    [Theory]
    [InlineData("https://auth.example.com")]
    [InlineData("LOCAL AUTHORITY")]
    [InlineData("OpenIdConnect")]
    public void GetCacheKey_Principal_SessionClaimFromAnotherIssuer_IsIgnored(string issuer)
    {
        var principal = CreateAuthenticatedPrincipal(new Claim("sub", "u1"), SessionClaim(IdA, issuer));

        _provider.GetCacheKey(principal).ShouldBe("user:u1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:a")]
    public void GetCacheKey_Principal_MalformedSessionClaim_IsIgnored(string value)
    {
        var principal = CreateAuthenticatedPrincipal(new Claim("sub", "u1"), SessionClaim(value));

        _provider.GetCacheKey(principal).ShouldBe("user:u1");
    }

    [Fact]
    public void GetCacheKey_Principal_SessionIdButNoSubject_ReturnsNull()
        => _provider.GetCacheKey(CreateAuthenticatedPrincipal(SessionClaim(IdA))).ShouldBeNull();
}
