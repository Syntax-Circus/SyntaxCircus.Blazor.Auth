namespace SyntaxCircus.Blazor.Auth.Tests;

/// <summary>
/// ApiAuthHandler depends on three sealed, non-interface classes (ServerRequestOidcTokenResolver,
/// OidcTokenRefreshService, SessionStateService) with no extraction seam, so — per the accepted
/// scope for this pass — these tests build the real object graph (real resolver, real refresh
/// service backed by a stub HTTP handler, real in-process token cache, real session state service)
/// and only fake the outer boundaries: the downstream HttpMessageHandler, the current HttpContext,
/// and the circuit's AuthenticationStateProvider/client-credentials provider.
/// </summary>
public class ApiAuthHandlerTests
{
    private static readonly HttpRequestOptionsKey<string> CircuitCacheKey = new("SyntaxCircus.Blazor.Auth.CacheKey");

    private static OidcTokenRefreshService CreateNeverCalledRefreshService()
        => RefreshServiceFactory.Create(_ => throw new InvalidOperationException("Refresh should not have been called.")).Service;

    private static ServerRequestOidcTokenResolver CreateResolver(IServerTokenCache tokenCache, OidcTokenRefreshService refreshService)
        => new(
            tokenCache,
            refreshService,
            new UserTokenCacheKeyProvider(),
            Options.Create(new AuthOptions()),
            NullLogger<ServerRequestOidcTokenResolver>.Instance);

    private static IHttpContextAccessor CreateAccessor(HttpContext? context)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return accessor;
    }

    private static AuthenticationStateProvider CreateAuthStateProvider(ClaimsPrincipal principal)
    {
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(new AuthenticationState(principal));
        return provider;
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(string subject)
        => new(new ClaimsIdentity([new Claim("sub", subject)], "TestAuth"));

    private static ClaimsPrincipal AnonymousPrincipal() => new(new ClaimsIdentity());

    private sealed record Harness(ApiAuthHandler Handler, StubHttpMessageHandler InnerHandler, IServerTokenCache TokenCache, SessionStateService SessionState, SessionExpiryBroker Broker, IApiClientCredentialsTokenProvider ClientCredentialsProvider);

    private static Harness CreateHandler(
        HttpContext? httpContext,
        ClaimsPrincipal circuitPrincipal,
        Func<HttpRequestMessage, HttpResponseMessage> innerResponder,
        IServerTokenCache? tokenCache = null,
        OidcTokenRefreshService? refreshService = null,
        bool clientCredentialsConfigured = false,
        string? clientCredentialsToken = "m2m-token")
    {
        tokenCache ??= new ServerTokenCache();
        refreshService ??= CreateNeverCalledRefreshService();
        var resolver = CreateResolver(tokenCache, refreshService);
        var broker = new SessionExpiryBroker();
        var sessionState = new SessionStateService(broker);
        var clientCredentialsProvider = Substitute.For<IApiClientCredentialsTokenProvider>();
        clientCredentialsProvider.IsConfigured.Returns(clientCredentialsConfigured);
        if (clientCredentialsConfigured)
        {
            clientCredentialsProvider.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(clientCredentialsToken!);
        }

        var handler = new ApiAuthHandler(
            CreateAccessor(httpContext),
            tokenCache,
            resolver,
            refreshService,
            CreateAuthStateProvider(circuitPrincipal),
            new UserTokenCacheKeyProvider(),
            sessionState,
            clientCredentialsProvider,
            NullLogger<ApiAuthHandler>.Instance);

        var innerHandler = new StubHttpMessageHandler(innerResponder);
        handler.InnerHandler = innerHandler;

        return new Harness(handler, innerHandler, tokenCache, sessionState, broker, clientCredentialsProvider);
    }

    private static Task<HttpResponseMessage> Send(ApiAuthHandler handler, string url = "https://api.example.com/things", string? cacheKey = null)
    {
        using var invoker = new HttpMessageInvoker(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            request.Options.Set(CircuitCacheKey, cacheKey);
        }

        return invoker.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static ServiceProvider BuildRegisteredProvider(ClaimsPrincipal principal, HttpMessageHandler? refreshHandler = null, IApiClientCredentialsTokenProvider? clientCredentials = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            options.Configuration = new OpenIdConnectConfiguration { TokenEndpoint = "https://identity.example.com/token" });
        services.AddScoped<AuthenticationStateProvider>(_ => CreateAuthStateProvider(principal));
        services.AddBlazorTokenForwarding(BuildConfiguration([]));
        if (clientCredentials is not null)
        {
            services.AddSingleton(clientCredentials);
        }

        if (refreshHandler is not null)
        {
            services.AddHttpClient<OidcTokenRefreshService>()
                .ConfigurePrimaryHttpMessageHandler(() => refreshHandler);
        }

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public async Task RegisteredHandler_UserOidc401_EvictsAndPublishesExactKeyToSameUserCircuitOnly()
    {
        using var provider = BuildRegisteredProvider(AuthenticatedPrincipal("user-1"));
        using var sameUserCircuit = provider.CreateScope();
        using var otherUserCircuit = provider.CreateScope();
        var sameUserState = sameUserCircuit.ServiceProvider.GetRequiredService<SessionStateService>();
        var otherUserState = otherUserCircuit.ServiceProvider.GetRequiredService<SessionStateService>();
        sameUserState.Observe("user:user-1");
        otherUserState.Observe("user:user-2");
        var cache = provider.GetRequiredService<IServerTokenCache>();
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "cookie-access",
            ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
        });

        using var handlerScope = provider.CreateScope();
        handlerScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var handler = handlerScope.ServiceProvider.GetRequiredService<ApiAuthHandler>();
        handler.InnerHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Send(handler, cacheKey: "user:user-1");

        (await cache.GetAsync("user:user-1", TestContext.Current.CancellationToken)).ShouldBeNull();
        sameUserState.IsSessionExpired.ShouldBeTrue();
        otherUserState.IsSessionExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task RegisteredHandler_RequestPathExpiredResolution_PublishesExactKeyBeforeDownstreamUnauthorized()
    {
        using var provider = BuildRegisteredProvider(AuthenticatedPrincipal("user-1"));
        using var circuit = provider.CreateScope();
        var circuitState = circuit.ServiceProvider.GetRequiredService<SessionStateService>();
        circuitState.Observe("user:user-1");
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "expired-access",
            ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(-10).UtcDateTime.ToString("O"),
        });

        using var handlerScope = provider.CreateScope();
        handlerScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var handler = handlerScope.ServiceProvider.GetRequiredService<ApiAuthHandler>();
        handler.InnerHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var response = await Send(handler);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        circuitState.IsSessionExpired.ShouldBeTrue();
    }

    [Fact]
    public async Task RegisteredHandler_CircuitRefreshFailure_PublishesExactKeyToCircuit()
    {
        using var provider = BuildRegisteredProvider(
            AuthenticatedPrincipal("user-1"),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)));
        using var circuit = provider.CreateScope();
        var circuitState = circuit.ServiceProvider.GetRequiredService<SessionStateService>();
        circuitState.Observe("user:user-1");
        var cache = provider.GetRequiredService<IServerTokenCache>();
        await cache.SetAsync(
            "user:user-1",
            new ServerTokenCacheEntry("expired-access", "refresh-token", null, DateTimeOffset.UtcNow.AddMinutes(-10)),
            TestContext.Current.CancellationToken);

        using var handlerScope = provider.CreateScope();
        handlerScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var handler = handlerScope.ServiceProvider.GetRequiredService<ApiAuthHandler>();
        handler.InnerHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Send(handler, cacheKey: "user:user-1");

        circuitState.IsSessionExpired.ShouldBeTrue();
    }

    [Fact]
    public void RegisteredApiAuthHandler_RequiresSessionExpiryBroker()
    {
        using var provider = BuildRegisteredProvider(AuthenticatedPrincipal("user-1"));
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ApiAuthHandler>().ShouldNotBeNull();
        typeof(ApiAuthHandler).GetConstructors()
            .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(SessionExpiryBroker)))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task RegisteredHandler_MissingCircuitOption_UsesClientCredentialsAndDoesNotPublishUserExpiryOn401()
    {
        var m2m = Substitute.For<IApiClientCredentialsTokenProvider>();
        m2m.IsConfigured.Returns(true);
        m2m.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("m2m-token");
        using var provider = BuildRegisteredProvider(AnonymousPrincipal(), clientCredentials: m2m);
        using var circuit = provider.CreateScope();
        var state = circuit.ServiceProvider.GetRequiredService<SessionStateService>();
        state.Observe("user:one");
        var cache = provider.GetRequiredService<IServerTokenCache>();
        await cache.SetAsync("user:one", new ServerTokenCacheEntry("user-token", "refresh", null, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        using var handlerScope = provider.CreateScope();
        handlerScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var handler = handlerScope.ServiceProvider.GetRequiredService<ApiAuthHandler>();
        handler.InnerHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Send(handler);

        handler.InnerHandler.ShouldBeOfType<StubHttpMessageHandler>().LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer m2m-token");
        state.IsSessionExpired.ShouldBeFalse();
        (await cache.GetAsync("user:one", TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task SendAsync_NoHttpContextAnonymousCircuitNoClientCredentials_SendsWithoutAuthorizationHeader()
    {
        var harness = CreateHandler(null, AnonymousPrincipal(), _ => new HttpResponseMessage(HttpStatusCode.OK));

        await Send(harness.Handler);

        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_NoHttpContextAuthenticatedCircuitWithCachedToken_AttachesCachedBearerToken()
    {
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("cached-access", "cached-refresh", null, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        var harness = CreateHandler(null, AuthenticatedPrincipal("user-1"), _ => new HttpResponseMessage(HttpStatusCode.OK), tokenCache);

        await Send(harness.Handler);

        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer cached-access");
    }

    [Fact]
    public async Task SendAsync_NoHttpContextAuthenticatedCircuitNoCacheNoRefreshToken_SendsWithoutAuthorizationHeader()
    {
        var harness = CreateHandler(null, AuthenticatedPrincipal("user-1"), _ => new HttpResponseMessage(HttpStatusCode.OK));

        await Send(harness.Handler);

        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_NoHttpContextAuthenticatedCircuitWithRefreshTokenOnly_RefreshesAndAttachesNewToken()
    {
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("expired-access", "refresh-1", null, DateTimeOffset.UtcNow.AddMinutes(-10)), TestContext.Current.CancellationToken);
        var (refreshService, _) = RefreshServiceFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "refreshed-access", refresh_token = "refresh-2", expires_in = 3600 }),
        });
        var harness = CreateHandler(null, AuthenticatedPrincipal("user-1"), _ => new HttpResponseMessage(HttpStatusCode.OK), tokenCache, refreshService);

        await Send(harness.Handler);

        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer refreshed-access");
    }

    [Fact]
    public async Task SendAsync_HttpContextPresentWithValidCookieToken_AttachesResolvedBearerToken()
    {
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "cookie-access",
            ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
        });
        var harness = CreateHandler(context, AnonymousPrincipal(), _ => new HttpResponseMessage(HttpStatusCode.OK));

        await Send(harness.Handler);

        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer cookie-access");
    }

    [Fact]
    public async Task SendAsync_HttpContextPresent_IgnoresConflictingCircuitCacheKey()
    {
        var cache = new ServerTokenCache();
        await cache.SetAsync("user:conflict", new ServerTokenCacheEntry("conflict-access", "refresh", null, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("cookie-user", new Dictionary<string, string>
        {
            ["access_token"] = "cookie-access",
            ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
        });
        var harness = CreateHandler(context, AnonymousPrincipal(), _ => new HttpResponseMessage(HttpStatusCode.OK), cache);

        await Send(harness.Handler, cacheKey: "user:conflict");

        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer cookie-access");
        (await cache.GetAsync("user:conflict", TestContext.Current.CancellationToken))!.AccessToken.ShouldBe("conflict-access");
    }

    [Fact]
    public async Task SendAsync_HttpContextPresentButAnonymous_ClientCredentialsConfigured_FallsBackToM2MToken()
    {
        var (context, _) = FakeAuthenticationContext.CreateUnauthenticated();
        var harness = CreateHandler(context, AnonymousPrincipal(), _ => new HttpResponseMessage(HttpStatusCode.OK), clientCredentialsConfigured: true);

        await Send(harness.Handler);

        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer m2m-token");
    }

    [Fact]
    public async Task SendAsync_NoUserTokenAndClientCredentialsNotConfigured_SendsUnauthenticated()
    {
        var (context, _) = FakeAuthenticationContext.CreateUnauthenticated();
        var harness = CreateHandler(context, AnonymousPrincipal(), _ => new HttpResponseMessage(HttpStatusCode.OK), clientCredentialsConfigured: false);

        var response = await Send(harness.Handler);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_ClientCredentialsProviderThrows_SendsUnauthenticatedInsteadOfThrowing()
    {
        var (context, _) = FakeAuthenticationContext.CreateUnauthenticated();
        var harness = CreateHandler(context, AnonymousPrincipal(), _ => new HttpResponseMessage(HttpStatusCode.OK), clientCredentialsConfigured: true);
        harness.ClientCredentialsProvider.GetAccessTokenAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("not configured"));

        var response = await Send(harness.Handler);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_UnauthorizedResponseFromUserOidcToken_EvictsCacheAndMarksSessionExpired()
    {
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("cached-access", "cached-refresh", null, DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        var harness = CreateHandler(null, AuthenticatedPrincipal("user-1"), _ => new HttpResponseMessage(HttpStatusCode.Unauthorized), tokenCache);
        harness.SessionState.Observe("user:user-1");

        await Send(harness.Handler);

        harness.SessionState.IsSessionExpired.ShouldBeTrue();
        (await tokenCache.GetAsync("user:user-1", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_UnauthorizedResponseFromClientCredentialsToken_DoesNotMarkSessionExpired()
    {
        var (context, _) = FakeAuthenticationContext.CreateUnauthenticated();
        var harness = CreateHandler(context, AnonymousPrincipal(), _ => new HttpResponseMessage(HttpStatusCode.Unauthorized), clientCredentialsConfigured: true);

        await Send(harness.Handler);

        harness.SessionState.IsSessionExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task CircuitPathRefreshThenHttpPathResolutionWithStaleCookie_HttpPathUsesCachesRotatedRefreshTokenInsteadOfFailingOnStaleCookie()
    {
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("circuit-access-1", "refresh-1", null, DateTimeOffset.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);

        var refreshTokenAlreadyUsed = new HashSet<string>(StringComparer.Ordinal);
        var (rotatingRefreshService, _) = RefreshServiceFactory.Create(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var sentRefreshToken = body.Split('&').Select(p => p.Split('=', 2)).First(p => p[0] == "refresh_token")[1];

            if (!refreshTokenAlreadyUsed.Add(sentRefreshToken))
            {
                // A rotation-enforcing IdP rejects a replayed/already-used refresh token.
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            return sentRefreshToken switch
            {
                "refresh-1" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { access_token = "circuit-access-2", refresh_token = "refresh-2", expires_in = 3600 }),
                },
                "refresh-2" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { access_token = "http-path-refreshed-access", refresh_token = "refresh-3", expires_in = 3600 }),
                },
                _ => new HttpResponseMessage(HttpStatusCode.BadRequest),
            };
        });

        var circuitHarness = CreateHandler(null, AuthenticatedPrincipal("user-1"), _ => new HttpResponseMessage(HttpStatusCode.OK), tokenCache, rotatingRefreshService);

        // Circuit-path refresh: rotates refresh-1 -> refresh-2 in the shared cache, with no HttpContext
        // available to persist the rotation back to the (stale) auth cookie.
        await Send(circuitHarness.Handler);
        circuitHarness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer circuit-access-2");

        // Simulate time passing: the circuit's own cached access token has since also expired (e.g. the
        // tab went idle long enough that no further outgoing call kept it silently renewed), while its
        // rotated refresh token is still valid at the IdP.
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("circuit-access-2", "refresh-2", null, DateTimeOffset.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);

        // A subsequent full HTTP request still carries the ORIGINAL, now-stale cookie (access-1/refresh-1)
        // — as it would if the cookie were never re-signed by the circuit path.
        var (context, _) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(-5).UtcDateTime.ToString("O"),
        });
        var httpPathResolver = CreateResolver(tokenCache, rotatingRefreshService);

        var resolution = await httpPathResolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        resolution.Token.ShouldBe("http-path-refreshed-access");
        resolution.IsExpired.ShouldBeFalse();
    }
}
