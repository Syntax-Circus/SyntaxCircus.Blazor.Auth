using Microsoft.Extensions.Time.Testing;

namespace SyntaxCircus.Blazor.Auth.Tests;

/// <summary>
/// Request-mode (non-null HttpContext) and circuit-mode (null HttpContext) variants of every
/// behavior, per AGENTS.md. Builds the real object graph and fakes only the outer boundaries.
/// </summary>
public class UserAccessTokenProviderTests
{
    private const string Key = "user:user-1";

    private static ClaimsPrincipal Authenticated(string subject = "user-1")
        => new(new ClaimsIdentity([new Claim("sub", subject)], "TestAuth"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static HttpResponseMessage Refreshed(string accessToken = "refreshed-access")
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = accessToken, refresh_token = "refresh-next", expires_in = 3600 }),
        };

    private static AuthenticationStateProvider StateProvider(ClaimsPrincipal principal)
    {
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(new AuthenticationState(principal));
        return provider;
    }

    private sealed record Harness(UserAccessTokenProvider Provider, ServerTokenCache Cache, StubHttpMessageHandler RefreshHandler, SessionStateService SessionState);

    private static Harness Create(
        HttpContext? httpContext,
        ClaimsPrincipal principal,
        Func<HttpRequestMessage, HttpResponseMessage>? refreshResponder = null,
        TimeProvider? clock = null,
        AuthenticationStateProvider? stateProvider = null,
        IUserTokenCacheKeyProvider? keyProviderOverride = null,
        ILogger<ServerRequestOidcTokenResolver>? resolverLogger = null)
    {
        var cache = new ServerTokenCache();
        var (refresh, refreshHandler) = RefreshServiceFactory.Create(refreshResponder ?? (_ => Refreshed()));
        var authOptions = Options.Create(new AuthOptions());
        var keyProvider = new UserTokenCacheKeyProvider();
        var providerKeys = keyProviderOverride ?? keyProvider;
        var broker = new SessionExpiryBroker();
        var state = new SessionStateService(broker);
        state.Observe(Key);
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        var provider = new UserAccessTokenProvider(
            accessor,
            new ServerRequestOidcTokenResolver(cache, refresh, keyProvider, authOptions, resolverLogger ?? NullLogger<ServerRequestOidcTokenResolver>.Instance, clock),
            stateProvider ?? StateProvider(principal),
            providerKeys,
            new CachedUserTokenResolver(cache, authOptions, clock),
            refresh,
            broker,
            NullLogger<UserAccessTokenProvider>.Instance);
        return new Harness(provider, cache, refreshHandler, state);
    }

    private sealed class CapturingLogger : ILogger<ServerRequestOidcTokenResolver>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }

    private static Task Seed(ServerTokenCache cache, string access, string? refresh, DateTimeOffset expiresAt)
        => cache.SetAsync(Key, new ServerTokenCacheEntry(access, refresh, null, expiresAt), TestContext.Current.CancellationToken);

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    // ---- circuit mode (no HttpContext) ----

    [Fact]
    public async Task GetAccessTokenAsync_NoHttpContextCachedToken_ReturnsItWithoutRefreshing()
    {
        var h = Create(null, Authenticated());
        await Seed(h.Cache, "cached-access", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        var token = await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("cached-access");
        h.RefreshHandler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoHttpContextExpiredEntryWithRefreshToken_RefreshesOnce()
    {
        var h = Create(null, Authenticated());
        await Seed(h.Cache, "expired-access", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-10));

        var token = await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("refreshed-access");
        h.RefreshHandler.CallCount.ShouldBe(1);
        (await h.Cache.GetAsync(Key, TestContext.Current.CancellationToken))!.AccessToken.ShouldBe("refreshed-access");
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoHttpContextConcurrentCallers_MakeOneTokenEndpointCall()
    {
        var h = Create(null, Authenticated());
        await Seed(h.Cache, "expired-access", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-10));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(async () => await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        tokens.ShouldAllBe(t => t == "refreshed-access");
        h.RefreshHandler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoHttpContextRefreshFails_ReturnsNullAndPublishesExpiryToSubscribedSession()
    {
        var h = Create(null, Authenticated(), _ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        await Seed(h.Cache, "expired-access", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-10));

        var token = await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        h.SessionState.IsSessionExpired.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoHttpContextNothingCached_ReturnsNullWithoutRefreshing()
    {
        var h = Create(null, Authenticated());

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBeNull();

        h.RefreshHandler.CallCount.ShouldBe(0);
        h.SessionState.IsSessionExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoHttpContextUnauthenticatedUserWithSubjectClaim_ReturnsNullEvenWithCachedTokenForTheKey()
    {
        var h = Create(null, new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")])));
        await Seed(h.Cache, "someone-elses-access", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBeNull();

        h.RefreshHandler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_OutsideCircuitScope_ReturnsNullInsteadOfThrowing()
    {
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Throws(new InvalidOperationException("Not inside a Blazor circuit."));
        var h = Create(null, Authenticated(), stateProvider: provider);

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoHttpContextClockAdvancesIntoSkewWindow_Refreshes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = Create(null, Authenticated(), clock: clock);
        await Seed(h.Cache, "cached-access", "refresh-1", clock.GetUtcNow().AddMinutes(10));

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe("cached-access");
        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(30));
        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe("refreshed-access");

        h.RefreshHandler.CallCount.ShouldBe(1);
    }

    // ---- request mode (HttpContext present, including the stale ambient context of a long-lived circuit) ----

    [Fact]
    public async Task GetAccessTokenAsync_HttpContextWithValidCookieToken_ReturnsCookieToken()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "cookie-access",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = Iso(DateTimeOffset.UtcNow.AddHours(1)),
        });
        var h = Create(context, Authenticated());

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe("cookie-access");

        h.RefreshHandler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_StaleHttpContextWithExpiredCookieToken_RefreshesInsteadOfReturningStaleToken()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "stale-cookie-access",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = Iso(DateTimeOffset.UtcNow.AddHours(-3)),
        });
        var h = Create(context, Authenticated());

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe("refreshed-access");

        h.RefreshHandler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_HttpContextExpiredCookieTokenWithoutRefreshToken_ReturnsNullAndPublishesExpiry()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "expired-cookie-access",
            ["expires_at"] = Iso(DateTimeOffset.UtcNow.AddHours(-3)),
        });
        var h = Create(context, Authenticated());

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBeNull();

        h.SessionState.IsSessionExpired.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAccessTokenAsync_HttpContextAnonymous_ReturnsNull()
    {
        var (context, _) = FakeAuthenticationContext.CreateUnauthenticated();
        var h = Create(context, Anonymous());

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoHttpContextExpiredEntryRefreshThrowsInvalidOperation_Rethrows()
    {
        var h = Create(null, Authenticated(), _ => throw new InvalidOperationException("discovery failed"));
        await Seed(h.Cache, "expired-access", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-10));

        await Should.ThrowAsync<InvalidOperationException>(async () => await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoHttpContextUnauthenticatedWithPermissiveKeyProvider_ReturnsNull()
    {
        var keys = Substitute.For<IUserTokenCacheKeyProvider>();
        keys.GetCacheKey(Arg.Any<ClaimsPrincipal?>()).Returns(Key);
        var h = Create(null, Anonymous(), keyProviderOverride: keys);
        await Seed(h.Cache, "someone-elses-access", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task GetAccessTokenAsync_HttpContextTransientRefreshFailureInsideSkewWindow_ReturnsStillValidTokenWithoutExpiry()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "near-expiry-access",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = Iso(DateTimeOffset.UtcNow.AddSeconds(30)),
        });
        var log = new CapturingLogger();
        var h = Create(context, Authenticated(), _ => throw new HttpRequestException("idp down"), resolverLogger: log);

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe("near-expiry-access");
        log.Levels.ShouldContain(LogLevel.Warning);

        h.RefreshHandler.CallCount.ShouldBe(1);
        h.SessionState.IsSessionExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAccessTokenAsync_HttpContextExpiredTokenRefreshThrows_Rethrows()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "expired-access",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = Iso(DateTimeOffset.UtcNow.AddMinutes(-5)),
        });
        var h = Create(context, Authenticated(), _ => throw new HttpRequestException("idp down"));

        await Should.ThrowAsync<HttpRequestException>(async () => await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessTokenAsync_HttpContextCallerCancelsDuringRefreshInsideSkewWindow_Rethrows()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "near-expiry-access",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = Iso(DateTimeOffset.UtcNow.AddSeconds(30)),
        });
        using var cts = new CancellationTokenSource();
        var log = new CapturingLogger();
        var h = Create(context, Authenticated(), _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }, resolverLogger: log);

        await Should.ThrowAsync<OperationCanceledException>(async () => await h.Provider.GetAccessTokenAsync(cts.Token));
        log.Levels.ShouldNotContain(LogLevel.Warning);
    }

    [Fact]
    public async Task GetAccessTokenAsync_HttpContextUnexpectedExceptionInsideSkewWindow_Rethrows()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "near-expiry-access",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = Iso(DateTimeOffset.UtcNow.AddSeconds(30)),
        });
        var h = Create(context, Authenticated(), _ => throw new ArgumentException("programming error"));

        await Should.ThrowAsync<ArgumentException>(async () => await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessTokenAsync_HttpContextRefreshTimesOutInsideSkewWindow_ReturnsStillValidToken()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "near-expiry-access",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = Iso(DateTimeOffset.UtcNow.AddSeconds(30)),
        });
        var h = Create(context, Authenticated(), _ => throw new TaskCanceledException("timeout"));

        (await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe("near-expiry-access");
        h.SessionState.IsSessionExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoHttpContextUnexpectedExceptionInsideSkewWindow_Rethrows()
    {
        var h = Create(null, Authenticated(), _ => throw new ArgumentException("programming error"));
        await Seed(h.Cache, "near-expiry-access", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30));

        await Should.ThrowAsync<ArgumentException>(async () => await h.Provider.GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    // ---- registration ----

    private static ServiceProvider BuildRegistered(ClaimsPrincipal principal, IApiClientCredentialsTokenProvider? clientCredentials = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            options.Configuration = new OpenIdConnectConfiguration { TokenEndpoint = "https://identity.example.com/token" });
        services.AddScoped<AuthenticationStateProvider>(_ => StateProvider(principal));
        services.AddBlazorTokenForwarding(new ConfigurationBuilder().Build());
        if (clientCredentials is not null)
        {
            services.AddSingleton(clientCredentials);
        }

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task Registered_ResolvesPerScopeUnderScopeValidationAndSharesTheCacheWithTheHandler()
    {
        using var provider = BuildRegistered(Authenticated());
        await provider.GetRequiredService<IServerTokenCache>().SetAsync(
            Key,
            new ServerTokenCacheEntry("cached-access", null, null, DateTimeOffset.UtcNow.AddHours(1)),
            TestContext.Current.CancellationToken);

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();
        var a = scopeA.ServiceProvider.GetRequiredService<IUserAccessTokenProvider>();

        a.ShouldBeSameAs(scopeA.ServiceProvider.GetRequiredService<IUserAccessTokenProvider>());
        a.ShouldNotBeSameAs(scopeB.ServiceProvider.GetRequiredService<IUserAccessTokenProvider>());
        (await a.GetAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe("cached-access");
        scopeA.ServiceProvider.GetRequiredService<ApiAuthHandler>().ShouldNotBeNull();
    }

    [Fact]
    public void Registered_CannotBeResolvedFromTheRootProvider()
    {
        using var provider = BuildRegistered(Authenticated());

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IUserAccessTokenProvider>());
    }

    [Fact]
    public async Task Registered_AnonymousUserNeverFallsBackToClientCredentials()
    {
        var clientCredentials = Substitute.For<IApiClientCredentialsTokenProvider>();
        clientCredentials.IsConfigured.Returns(true);
        clientCredentials.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("m2m-token");
        using var provider = BuildRegistered(Anonymous(), clientCredentials);
        using var scope = provider.CreateScope();

        var token = await scope.ServiceProvider.GetRequiredService<IUserAccessTokenProvider>().GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        await clientCredentials.DidNotReceive().GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Registered_CachedUserTokenResolverGetsARealLogger()
    {
        using var provider = BuildRegistered(Authenticated());
        var resolver = provider.GetRequiredService<CachedUserTokenResolver>();

        var field = typeof(CachedUserTokenResolver).GetField("logger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field.ShouldNotBeNull();
        field.GetValue(resolver).ShouldNotBeOfType<NullLogger>();
    }

    [Fact]
    public void PublicSurface_ExposesOnlyTheProviderInterface()
    {
        var exported = typeof(IUserAccessTokenProvider).Assembly.GetExportedTypes().Select(t => t.Name).ToList();

        exported.ShouldContain(nameof(IUserAccessTokenProvider));
        exported.ShouldNotContain("UserAccessTokenProvider");
        exported.ShouldNotContain("CachedUserTokenResolver");
    }
}
