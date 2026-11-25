using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SyntaxCircus.Blazor.Auth.Tests.Infrastructure;

/// <summary>
/// A <see cref="TestServer"/> with the <b>real</b> cookie handler, <c>AddBlazorTokenForwarding</c> and
/// <c>UseBlazorTokenCache()</c> wired as a host app would, plus a fake identity provider that answers
/// refresh grants with tokens derived from the refresh token it was sent. Unlike
/// <see cref="FakeAuthenticationContext"/>, sign-in goes through the framework, so cookie events,
/// ticket serialization and re-signing behave as in production.
/// </summary>
internal sealed class CookieSessionHost : IDisposable
{
    private readonly WebApplication app;
    private readonly TestServer server;

    private CookieSessionHost(WebApplication app, FakeIdentityProvider identityProvider, CapturingLoggerProvider logs)
    {
        this.app = app;
        server = (TestServer)app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        IdentityProvider = identityProvider;
        Logs = logs;
    }

    public FakeIdentityProvider IdentityProvider { get; }

    public CapturingLoggerProvider Logs { get; }

    public IServiceProvider Services => app.Services;

    /// <summary>
    /// Builds the host. <paramref name="configureBefore"/> runs before
    /// <c>AddBlazorTokenForwarding</c> and <paramref name="configureAfter"/> after it. When
    /// <paramref name="addDefaultCookie"/> is false the caller must register the cookie scheme itself,
    /// which is how the registration-order tests work.
    /// </summary>
    public static CookieSessionHost Create(
        Action<IServiceCollection>? configureBefore = null,
        Action<IServiceCollection>? configureAfter = null,
        bool addDefaultCookie = true)
    {
        var identityProvider = new FakeIdentityProvider();
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);

        var services = builder.Services;
        services.AddRouting();
        if (addDefaultCookie)
        {
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        }

        configureBefore?.Invoke(services);
        services.AddBlazorTokenForwarding(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Oidc:TokenCache:RefreshSkewSeconds"] = "60",
            })
            .Build());
        configureAfter?.Invoke(services);

        services.AddScoped(_ => Substitute.For<AuthenticationStateProvider>());
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            // Only what a test that registers the real OpenID Connect handler has not already set.
            options.ClientId ??= "client1";
            options.Configuration ??= new OpenIdConnectConfiguration { TokenEndpoint = "https://auth.example.com/token" };
        });
        services.AddHttpClient<OidcTokenRefreshService>().ConfigurePrimaryHttpMessageHandler(() => identityProvider.CreateHandler());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseBlazorTokenCache();

        // Signs in the way the OIDC handler does: a brand-new principal and brand-new properties.
        app.MapGet("/login", async (HttpContext context, string sub, string access, string refresh, int expiresIn, string? forgedSessionId) =>
        {
            var claims = new List<Claim> { new("sub", sub) };
            if (forgedSessionId is not null)
            {
                claims.Add(new Claim(SessionIdClaimType, forgedSessionId, ClaimValueTypes.String, "https://auth.example.com"));
            }

            var properties = new AuthenticationProperties();
            properties.StoreTokens(Tokens(access, refresh, expiresIn));

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc")),
                properties);
        });

        // Issues the cookie a host running an earlier package version issued: the real ticket format,
        // written without going through SigningIn, so there is no session id.
        app.MapGet("/pre-upgrade-login", (HttpContext context, IOptionsMonitor<CookieAuthenticationOptions> cookieOptions, string sub, string access, string refresh, int expiresIn) =>
        {
            var options = cookieOptions.Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var properties = new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14),
            };
            properties.StoreTokens(Tokens(access, refresh, expiresIn));
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub)], "oidc")),
                properties,
                CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Cookies.Append(options.Cookie.Name!, options.TicketDataFormat.Protect(ticket));
        });

        // Re-signs the current principal the way an app does after e.g. a profile update.
        app.MapGet("/resign", async (HttpContext context) =>
        {
            var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, result.Principal!, result.Properties);
        });

        // Re-signs with a principal built from scratch (claim types and values only) but the cookie's own
        // properties, as an app does after reloading the user. With copyProperties, into a new
        // AuthenticationProperties holding a copy of the items.
        app.MapGet("/resign-rebuilt", async (HttpContext context, string? sub, bool? copyProperties) =>
        {
            var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var claims = result.Principal!.Claims
                .Where(claim => claim.Type != SessionIdClaimType)
                .Select(claim => new Claim(claim.Type, claim.Type == "sub" && sub is not null ? sub : claim.Value));
            var properties = copyProperties == true
                ? new AuthenticationProperties(new Dictionary<string, string?>(result.Properties!.Items))
                : result.Properties!;
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, "rebuilt")),
                properties);
        });

        // The step-up pattern: a new identity-provider sign-in started with the current cookie's properties.
        app.MapGet("/challenge-with-cookie-properties", (Delegate)(async (HttpContext context) =>
        {
            var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            // The handler adds this with Items.Add, so it throws unless the last sign-in's value is removed.
            result.Properties!.Items.Remove(OpenIdConnectDefaults.RedirectUriForCodePropertiesKey);
            result.Properties.RedirectUri = "/capture";
            await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, result.Properties);
        }));

        app.MapGet("/challenge", (Delegate)((HttpContext context) => context.ChallengeAsync(
            OpenIdConnectDefaults.AuthenticationScheme,
            new AuthenticationProperties { RedirectUri = "/capture" })));

        app.MapGet("/me", async (HttpContext context, ServerRequestOidcTokenResolver resolver, IUserTokenCacheKeyProvider keys, IServerTokenCache cache) =>
        {
            var resolution = await resolver.ResolveAsync(context, context.RequestAborted);
            return Results.Json(new SessionView(
                resolution.Token,
                resolution.CacheKey,
                keys.GetCacheKey(context.User),
                resolution.CacheKey is null ? null : await cache.GetRefreshTokenAsync(resolution.CacheKey, context.RequestAborted),
                [.. context.User.FindAll(SessionIdClaimType).Select(claim => claim.Value)],
                context.User.Claims.Select(claim => claim.Type).Distinct().ToArray()));
        });

        // What the cookie itself holds, independent of the token cache.
        app.MapGet("/cookie-tokens", (Delegate)(async (HttpContext context) => Results.Json(new CookieTokens(
            await context.GetTokenAsync(CookieAuthenticationDefaults.AuthenticationScheme, "access_token"),
            await context.GetTokenAsync(CookieAuthenticationDefaults.AuthenticationScheme, "refresh_token")))));

        // Hands the request's principal to the test, standing in for the principal a circuit gets.
        ClaimsPrincipal? captured = null;
        app.MapGet("/capture", (HttpContext context) => { captured = context.User; });

        app.StartAsync().GetAwaiter().GetResult();
        var host = new CookieSessionHost(app, identityProvider, logs);
        host.capturedPrincipal = () => captured;
        return host;
    }

    private static AuthenticationToken[] Tokens(string access, string refresh, int expiresIn) =>
    [
        new() { Name = "access_token", Value = access },
        new() { Name = "refresh_token", Value = refresh },
        new() { Name = "expires_at", Value = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToString("o", CultureInfo.InvariantCulture) },
    ];

    private Func<ClaimsPrincipal?> capturedPrincipal = () => null;

    /// <summary>The claim type, repeated as a literal so the tests pin the wire value.</summary>
    public const string SessionIdClaimType = "urn:syntaxcircus:blazor-auth:session-id";

    public Browser NewBrowser() => new(server.CreateClient(), this);

    public void Dispose()
    {
        server.Dispose();
        ((IDisposable)app).Dispose();
    }

    /// <summary>One cookie jar: a browser, or a copy of a cookie someone else took.</summary>
    internal sealed class Browser(HttpClient client, CookieSessionHost host)
    {
        public Dictionary<string, string> Cookies { get; private set; } = new(StringComparer.Ordinal);

        public Browser Copy() => new(host.server.CreateClient(), host) { Cookies = new(Cookies, StringComparer.Ordinal) };

        public async Task<HttpResponseMessage> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            var response = await SendAsync(request);
            response.EnsureSuccessStatusCode();
            return response;
        }

        /// <summary>Sends with this jar's cookies and keeps what comes back. Redirects are not followed.</summary>
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            if (Cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(pair => $"{pair.Key}={pair.Value}")));
            }

            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    var pair = setCookie.Split(';', 2)[0].Split('=', 2);
                    if (pair[1].Length == 0)
                    {
                        Cookies.Remove(pair[0]);
                    }
                    else
                    {
                        Cookies[pair[0]] = pair[1];
                    }
                }
            }

            return response;
        }

        public Task LoginAsync(string sub, string tokenSuffix, int expiresIn, string? forgedSessionId = null)
            => GetAsync($"/login?sub={Uri.EscapeDataString(sub)}&access=access-{tokenSuffix}&refresh=refresh-{tokenSuffix}&expiresIn={expiresIn}"
                + (forgedSessionId is null ? string.Empty : $"&forgedSessionId={Uri.EscapeDataString(forgedSessionId)}"));

        public Task PreUpgradeLoginAsync(string sub, string tokenSuffix, int expiresIn)
            => GetAsync($"/pre-upgrade-login?sub={Uri.EscapeDataString(sub)}&access=access-{tokenSuffix}&refresh=refresh-{tokenSuffix}&expiresIn={expiresIn}");

        public async Task<SessionView> MeAsync()
        {
            var response = await GetAsync("/me");
            return (await response.Content.ReadFromJsonAsync<SessionView>(TestContext.Current.CancellationToken))!;
        }

        public async Task<CookieTokens> CookieTokensAsync()
        {
            var response = await GetAsync("/cookie-tokens");
            return (await response.Content.ReadFromJsonAsync<CookieTokens>(TestContext.Current.CancellationToken))!;
        }

        public async Task<ClaimsPrincipal> PrincipalAsync()
        {
            await GetAsync("/capture");
            return host.capturedPrincipal()!;
        }
    }
}

internal sealed record SessionView(
    string? Token,
    string? CacheKey,
    string? PrincipalCacheKey,
    string? CachedRefreshToken,
    string[] SessionIds,
    string[] ClaimTypes);

internal sealed record CookieTokens(string? AccessToken, string? RefreshToken);

/// <summary>
/// Answers a refresh grant for <c>refresh-X</c> with <c>access-X+</c> / <c>refresh-X+</c>, so every
/// token names the sign-in it descends from. With <see cref="RotatesRefreshTokens"/> it rejects a
/// refresh token it has already redeemed, as a rotating provider does.
/// </summary>
internal sealed class FakeIdentityProvider
{
    private readonly ConcurrentDictionary<string, bool> redeemed = new(StringComparer.Ordinal);

    public bool RotatesRefreshTokens { get; set; }

    /// <summary>Lifetime of the access tokens issued by refresh grants.</summary>
    public int ExpiresInSeconds { get; set; } = 3600;

    public ConcurrentQueue<string> Presented { get; } = new();

    public HttpMessageHandler CreateHandler() => new StubHttpMessageHandler(request =>
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var refreshToken = body.Split('&')
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] == "refresh_token")
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .Single();
        Presented.Enqueue(refreshToken);

        if (RotatesRefreshTokens && !redeemed.TryAdd(refreshToken, true))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var lineage = refreshToken["refresh-".Length..] + "+";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                access_token = $"access-{lineage}",
                refresh_token = $"refresh-{lineage}",
                expires_in = ExpiresInSeconds,
            }),
        };
    });
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public IEnumerable<string> LibraryWarnings => Entries
        .Where(e => e.Level >= LogLevel.Warning && e.Category.StartsWith("SyntaxCircus.", StringComparison.Ordinal))
        .Select(e => e.Message);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
