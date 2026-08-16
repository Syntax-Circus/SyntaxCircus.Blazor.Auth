namespace SyntaxCircus.Blazor.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Authentication:Oidc";

    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string[] Scopes { get; set; } = ["openid", "profile", "email", "offline_access"];

    public TokenCacheOptions TokenCache { get; set; } = new();

    public sealed class TokenCacheOptions
    {
        public int RefreshSkewSeconds { get; set; } = 60;

        public int FallbackAccessTokenLifetimeSeconds { get; set; } = 300;

        public RedisTokenCacheOptions Redis { get; set; } = new();
    }

    public sealed class RedisTokenCacheOptions
    {
        public bool Enabled { get; set; }

        public string ConnectionString { get; set; } = string.Empty;

        public string InstanceName { get; set; } = "SyntaxCircus:OidcTokenCache:";

        public RedisTokenCacheProtectionOptions Protection { get; set; } = new();
    }

    public sealed class RedisTokenCacheProtectionOptions
    {
        public bool Enabled { get; set; }

        public string Purpose { get; set; } = "SyntaxCircus.Blazor.Auth.RedisServerTokenCache";
    }
}
