namespace SyntaxCircus.Blazor.Auth.Tests;

public class BlazorTokenForwardingExtensionsTests
{
    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void AddBlazorTokenForwarding_NullServices_ThrowsArgumentNullException()
        => Should.Throw<ArgumentNullException>(() =>
            BlazorTokenForwardingExtensions.AddBlazorTokenForwarding(null!, BuildConfiguration([])));

    [Fact]
    public void AddBlazorTokenForwarding_NullConfiguration_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        Should.Throw<ArgumentNullException>(() => services.AddBlazorTokenForwarding(null!));
    }

    [Fact]
    public void AddBlazorTokenForwarding_RedisNotConfigured_RegistersInProcessTokenCache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<AuthenticationStateProvider>());
        services.AddBlazorTokenForwarding(BuildConfiguration([]));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IServerTokenCache>().ShouldBeOfType<ServerTokenCache>();
    }

    [Fact]
    public void AddBlazorTokenForwarding_RedisEnabledWithConnectionString_RegistersRedisTokenCache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<AuthenticationStateProvider>());
        services.AddBlazorTokenForwarding(BuildConfiguration(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:TokenCache:Redis:Enabled"] = "true",
            ["Authentication:Oidc:TokenCache:Redis:ConnectionString"] = "localhost:6379",
        }));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IServerTokenCache>().ShouldBeOfType<RedisServerTokenCache>();
    }

    [Fact]
    public void AddBlazorTokenForwarding_RedisEnabledButNoConnectionString_FallsBackToInProcessCache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<AuthenticationStateProvider>());
        services.AddBlazorTokenForwarding(BuildConfiguration(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:TokenCache:Redis:Enabled"] = "true",
        }));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IServerTokenCache>().ShouldBeOfType<ServerTokenCache>();
    }

    [Fact]
    public void AddBlazorTokenForwarding_BindsAuthOptionsFromConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<AuthenticationStateProvider>());
        services.AddBlazorTokenForwarding(BuildConfiguration(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:ClientId"] = "client1",
        }));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthOptions>>().Value;

        options.ClientId.ShouldBe("client1");
    }

    [Fact]
    public void AddBlazorTokenForwarding_RegistersCoreServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<AuthenticationStateProvider>());
        services.AddBlazorTokenForwarding(BuildConfiguration([]));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<SessionStateService>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IUserTokenCacheKeyProvider>().ShouldBeOfType<UserTokenCacheKeyProvider>();
        scope.ServiceProvider.GetRequiredService<ServerRequestOidcTokenResolver>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<ApiAuthHandler>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IApiClientCredentialsTokenProvider>().ShouldBeOfType<ApiClientCredentialsTokenProvider>();
    }

    [Fact]
    public void UseBlazorTokenCache_NullApp_ThrowsArgumentNullException()
        => Should.Throw<ArgumentNullException>(() => BlazorTokenForwardingExtensions.UseBlazorTokenCache(null!));
}
