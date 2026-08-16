namespace SyntaxCircus.Blazor.Auth.Tests;

public class AuthOptionsTests
{
    [Fact]
    public void Defaults_MatchExpectedValues()
    {
        var options = new AuthOptions();

        options.Authority.ShouldBe(string.Empty);
        options.ClientId.ShouldBe(string.Empty);
        options.ClientSecret.ShouldBe(string.Empty);
        options.Scopes.ShouldBe(["openid", "profile", "email", "offline_access"]);
        options.TokenCache.RefreshSkewSeconds.ShouldBe(60);
        options.TokenCache.FallbackAccessTokenLifetimeSeconds.ShouldBe(300);
        options.TokenCache.Redis.Enabled.ShouldBeFalse();
        options.TokenCache.Redis.ConnectionString.ShouldBe(string.Empty);
        options.TokenCache.Redis.InstanceName.ShouldBe("SyntaxCircus:OidcTokenCache:");
        options.TokenCache.Redis.Protection.Enabled.ShouldBeFalse();
        options.TokenCache.Redis.Protection.Purpose.ShouldBe("SyntaxCircus.Blazor.Auth.RedisServerTokenCache");
    }

    [Fact]
    public void SectionName_IsAuthenticationOidc()
        => AuthOptions.SectionName.ShouldBe("Authentication:Oidc");
}
