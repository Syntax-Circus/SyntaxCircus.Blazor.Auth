namespace SyntaxCircus.Blazor.Auth.Tests;

public class BlazorTokenCacheMiddlewareTests
{
    private static ServerRequestOidcTokenResolver CreateResolver(OidcTokenRefreshService refreshService)
        => new(
            new ServerTokenCache(),
            refreshService,
            new UserTokenCacheKeyProvider(),
            Options.Create(new AuthOptions()),
            NullLogger<ServerRequestOidcTokenResolver>.Instance);

    private static OidcTokenRefreshService CreateNeverCalledRefreshService()
        => RefreshServiceFactory.Create(_ => throw new InvalidOperationException("Refresh should not have been called.")).Service;

    private static BlazorTokenCacheMiddleware CreateMiddleware(RequestDelegate next)
        => new(next, NullLogger<BlazorTokenCacheMiddleware>.Instance);

    [Fact]
    public async Task InvokeAsync_NullHttpContext_ThrowsArgumentNullException()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);

        await Should.ThrowAsync<ArgumentNullException>(() => middleware.InvokeAsync(null!, CreateResolver(CreateNeverCalledRefreshService())));
    }

    [Fact]
    public async Task InvokeAsync_NullResolver_ThrowsArgumentNullException()
    {
        var (context, _) = FakeAuthenticationContext.CreateUnauthenticated();
        var middleware = CreateMiddleware(_ => Task.CompletedTask);

        await Should.ThrowAsync<ArgumentNullException>(() => middleware.InvokeAsync(context, null!));
    }

    [Fact]
    public async Task InvokeAsync_UnauthenticatedUser_SkipsResolverButCallsNext()
    {
        var (context, authService) = FakeAuthenticationContext.CreateUnauthenticated();
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, CreateResolver(CreateNeverCalledRefreshService()));

        nextCalled.ShouldBeTrue();
        await authService.DidNotReceiveWithAnyArgs().AuthenticateAsync(default!, default);
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedUser_ResolvesTokenThenCallsNext()
    {
        var (context, authService) = FakeAuthenticationContext.CreateAuthenticated("user-1", new Dictionary<string, string>
        {
            ["access_token"] = "access-1",
            ["refresh_token"] = "refresh-1",
            ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
        });
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, CreateResolver(CreateNeverCalledRefreshService()));

        nextCalled.ShouldBeTrue();
        await authService.Received(1).AuthenticateAsync(context, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task InvokeAsync_ResolverThrows_SwallowsExceptionAndStillCallsNext()
    {
        var (context, authService) = FakeAuthenticationContext.CreateAuthenticated("user-1");
        authService.AuthenticateAsync(context, CookieAuthenticationDefaults.AuthenticationScheme)
            .Returns<Task<AuthenticateResult>>(_ => throw new InvalidOperationException("boom"));
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await Should.NotThrowAsync(() => middleware.InvokeAsync(context, CreateResolver(CreateNeverCalledRefreshService())));

        nextCalled.ShouldBeTrue();
    }
}
