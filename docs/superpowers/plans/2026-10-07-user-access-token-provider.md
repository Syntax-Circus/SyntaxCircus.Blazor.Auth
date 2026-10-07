# User Access Token Provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a public `IUserAccessTokenProvider` to SyntaxCircus.Blazor.Auth that returns the signed-in user's current access token (refreshed when needed) for callers that cannot use an `HttpClient` pipeline, such as a SignalR `HubConnection`, sharing one cache-then-refresh core with `ApiAuthHandler`.

**Architecture:** Extract the circuit-phase cache-then-refresh-under-lock logic out of `ApiAuthHandler` into an internal singleton `CachedUserTokenResolver`, fixing two latent defects on the way (refresh token re-read inside the lock; `RefreshSkewSeconds` honored on the circuit path through `TimeProvider`). `ApiAuthHandler` and a new internal scoped `UserAccessTokenProvider` both call it. The provider is registered inside the existing `AddBlazorTokenForwarding`; the only new public type is the interface.

**Tech Stack:** .NET 10, ASP.NET Core / Blazor Server, xunit.v3 3.2.2 + Shouldly + NSubstitute, Microsoft.Testing.Platform, GitVersion.MsBuild 6 (TrunkBased), NuGet Trusted Publishing from CI.

**Spec:** `C:\tmp\claude\D--dev-SyntaxCircus-techstrap\42551360-63f8-4931-99a2-3f692676cb7a\scratchpad\blazorauth-plan\design.md` (binding design brief) and `...\facts.md` beside it. The repo's own `AGENTS.md` and `README.md` are binding conventions.

**Working repo:** `D:\dev\SyntaxCircus\SyntaxCircus.Blazor.Auth` (called `<repo>` below). Branch `feat/user-access-token-provider`, cut from `main` at `cb13df2` (v0.1.7). All paths below are relative to `<repo>`. Every step in this plan was run, in order, in a scratch clone; the output shown is real.

## Global Constraints

- **Public surface is exactly one new type:** `public interface IUserAccessTokenProvider { ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default); }`. `UserAccessTokenProvider` and `CachedUserTokenResolver` are `internal sealed`. No new extension method. Registration goes inside the existing `AddBlazorTokenForwarding` (AGENTS.md: "no piecemeal per-feature registration extension methods").
- **Provider lifetime scoped; resolver singleton.** The singleton resolver may only take singleton-safe dependencies. The typed-`HttpClient` `OidcTokenRefreshService` and the expiry publisher are passed per call.
- **`null` from the provider** means anonymous, lapsed session, or failed refresh. Never a client-credentials token.
- **Both public `ApiAuthHandler` constructors stay source-compatible** (the 7-arg production one and the 9-arg compatibility one). Only an internal overload is added.
- **Build must be clean:** `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, CA1305/CA1707/CA1848 suppressed repo-wide, any other analyzer warning is a build break.
- **File-scoped namespaces and `ConfigureAwait(false)`** in library code. Tests use Shouldly (`.ShouldBe`), NSubstitute, `TestContext.Current.CancellationToken`.
- **Tests mirror `src/` 1:1 by filename.** AGENTS.md:62 requires both request-mode and circuit-mode test variants.
- **Central Package Management:** new package versions go in `Directory.Packages.props` only. **xunit.v3 stays pinned at 3.2.2.**
- **Do not hand-edit any version.** GitVersion drives it. The README must be updated with the public surface (AGENTS.md:5, :44).
- **Release safety:** a push to `main` publishes to nuget.org automatically and NuGet versions are immutable. The last step of this plan is "owner merges", never "publish".
- **Commit trailers**, exactly these two lines at the end of every commit message:

  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01ReiWu2p7mSuArnHAMeBiMi
  ```

- **Test command:** `dotnet test --solution SyntaxCircus.Blazor.Auth.slnx --configuration Release`. Baseline before any change: 164 passed.

## Review Focus

1. **No behaviour regression in `ApiAuthHandler` on any path.** Pinned by the whole existing `ApiAuthHandlerTests` suite staying green, plus `SendAsync_NoHttpContextNearExpiryTokenWithoutRefreshToken_StillForwardsTheStillValidToken` (a token inside the skew window with nothing to refresh from is still forwarded until it really expires, not dropped early) and `ResolveAsync_RefreshFailsInsideSkewWindow_ReturnsStillValidTokenWithoutPublishing` (a failed refresh inside the window does not publish expiry while the token is still valid). The one intended behaviour change is the skew window on the circuit path (documented in the README).
2. **Single-flight refresh under concurrency, with no stale refresh token.** `ResolveAsync_ConcurrentCallersWithExpiredToken_MakeExactlyOneRefreshCall`, `GetAccessTokenAsync_NoHttpContextConcurrentCallers_MakeOneTokenEndpointCall`, and `SendAsync_NoHttpContextRefreshTokenRotatedWhileWaitingForLock_RefreshesWithTheRotatedToken`. Residual, not testable here and documented: the lock is process-local, so with Redis and several instances two instances can still refresh concurrently.
3. **Scope correctness.** `Registered_CannotBeResolvedFromTheRootProvider`, `Registered_ResolvesPerScopeUnderScopeValidation...` (built with `validateScopes: true`), and `GetAccessTokenAsync_OutsideCircuitScope_ReturnsNullInsteadOfThrowing`. A stale ambient `HttpContext` in a long-lived circuit is `GetAccessTokenAsync_StaleHttpContextWithExpiredCookieToken_RefreshesInsteadOfReturningStaleToken`.
4. **No client-credentials fallback in the user provider.** `Registered_AnonymousUserNeverFallsBackToClientCredentials` registers a configured M2M provider and asserts it is never called.
5. **The public surface is exactly one new interface.** `PublicSurface_ExposesOnlyTheProviderInterface`.
6. **Known, accepted, documented:** if `RefreshSkewSeconds` is greater than or equal to the IdP's access-token lifetime, every call refreshes (the HTTP-request path already behaves this way). The README says to keep the skew well below the token lifetime. The circuit refresh does not update the auth cookie (unchanged; the existing rotated-refresh-token handling in `ServerRequestOidcTokenResolver` covers the next request).

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `src/SyntaxCircus.Blazor.Auth/CachedUserTokenResolver.cs` | create | the one cache-then-refresh-under-lock implementation (internal, singleton) |
| `src/SyntaxCircus.Blazor.Auth/ApiAuthHandler.cs` | modify | delegate circuit paths A and B to the core; add an internal constructor; public constructors unchanged |
| `src/SyntaxCircus.Blazor.Auth/Tokens/IUserAccessTokenProvider.cs` | create | the public interface and its XML docs |
| `src/SyntaxCircus.Blazor.Auth/Tokens/UserAccessTokenProvider.cs` | create | internal scoped implementation (HttpContext path via the resolver; circuit path via the core) |
| `src/SyntaxCircus.Blazor.Auth/BlazorTokenForwardingExtensions.cs` | modify | register the core (singleton) and the provider (scoped); update the handler factory |
| `src/SyntaxCircus.Blazor.Auth/ServerRequestOidcTokenResolver.cs` | modify | one XML-doc `cref` that pointed at the removed private method |
| `tests/.../CachedUserTokenResolverTests.cs` | create | core behaviour, including the FakeTimeProvider skew test |
| `tests/.../UserAccessTokenProviderTests.cs` | create | provider behaviour in circuit and request mode, DI, public surface |
| `tests/.../ApiAuthHandlerTests.cs` | modify | handler skew, no-refresh-token, refresh-token re-read, DI skew tests |
| `Directory.Packages.props`, `tests/.../SyntaxCircus.Blazor.Auth.Tests.csproj` | modify | `Microsoft.Extensions.TimeProvider.Testing` 10.0.0 (test-only) |
| `README.md`, `AGENTS.md` | modify | entry-point list, how it works, SignalR snippet, behavioural notes, repo map |

---

### Task 1: Extract the shared core, with fixes (a) and (b)

**Files:**
- Create: `src/SyntaxCircus.Blazor.Auth/CachedUserTokenResolver.cs`
- Create: `tests/SyntaxCircus.Blazor.Auth.Tests/CachedUserTokenResolverTests.cs`
- Modify: `src/SyntaxCircus.Blazor.Auth/ApiAuthHandler.cs`
- Modify: `src/SyntaxCircus.Blazor.Auth/BlazorTokenForwardingExtensions.cs`
- Modify: `src/SyntaxCircus.Blazor.Auth/ServerRequestOidcTokenResolver.cs` (one doc comment)
- Modify: `tests/SyntaxCircus.Blazor.Auth.Tests/ApiAuthHandlerTests.cs`
- Modify: `Directory.Packages.props`, `tests/SyntaxCircus.Blazor.Auth.Tests/SyntaxCircus.Blazor.Auth.Tests.csproj`

**Interfaces:**
- Consumes (existing): `IServerTokenCache` (`GetAsync`, `GetRefreshTokenAsync`, `SetAsync`, `WithRefreshLockAsync`), `ServerTokenCacheEntry.IsUsable(DateTimeOffset nowUtc, TimeSpan refreshSkew)`, `OidcTokenRefreshService.RefreshAsync(string, CancellationToken)`, `AuthOptions.TokenCache.RefreshSkewSeconds`.
- Produces:
  - `internal sealed class CachedUserTokenResolver(IServerTokenCache tokenCache, IOptions<AuthOptions> options, TimeProvider? timeProvider = null)` in namespace `SyntaxCircus.Blazor.Auth`.
  - `Task<string?> CachedUserTokenResolver.ResolveAsync(string cacheKey, OidcTokenRefreshService refreshService, Action<string> onExpired, CancellationToken cancellationToken)`. Contract: returns a token usable now (cache entry outside the skew window, or freshly refreshed); inside the skew window with no refresh token, or with a failed refresh, returns the still-unexpired token without calling `onExpired`; calls `onExpired(cacheKey)` only when the refresh fails and no unexpired token remains; returns `null` when nothing is cached and there is no refresh token. Registered `AddSingleton<CachedUserTokenResolver>()`.
  - `internal ApiAuthHandler(IHttpContextAccessor, IServerTokenCache, ServerRequestOidcTokenResolver, OidcTokenRefreshService, IApiClientCredentialsTokenProvider, ILogger<ApiAuthHandler>, SessionExpiryBroker, CachedUserTokenResolver)`. The two public constructors keep their exact signatures.
  - Test-project dependency `Microsoft.Extensions.TimeProvider.Testing` (gives `FakeTimeProvider`).

- [ ] **Step 1: Create the feature branch and confirm the baseline**

```bash
cd D:/dev/SyntaxCircus/SyntaxCircus.Blazor.Auth
git switch main && git pull --ff-only && git switch -c feat/user-access-token-provider
git log --oneline -1
dotnet test --solution SyntaxCircus.Blazor.Auth.slnx --configuration Release
```

Expected: `cb13df2 Fix long-lived Blazor Server circuits ...`, then `total: 164`, `failed: 0`, `succeeded: 164`. (If `main` has moved, the count may differ; it must be all green.)

- [ ] **Step 2: Add the FakeTimeProvider test dependency**

In `Directory.Packages.props`, add this line immediately above the `Microsoft.NET.Test.Sdk` line:

```xml
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.0.0" />
```

In `tests/SyntaxCircus.Blazor.Auth.Tests/SyntaxCircus.Blazor.Auth.Tests.csproj`, add this line immediately below the `Microsoft.NET.Test.Sdk` `PackageReference`:

```xml
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

Run: `dotnet build tests/SyntaxCircus.Blazor.Auth.Tests -c Release`
Expected: `Build succeeded.` `0 Warning(s)` `0 Error(s)`.

- [ ] **Step 3: Write the failing handler tests (behavioural RED against the unchanged handler)**

Open `tests/SyntaxCircus.Blazor.Auth.Tests/ApiAuthHandlerTests.cs`. Delete its last line (the closing `}` of the class) and append exactly the following, which re-closes the class:

```csharp

    [Fact]
    public async Task SendAsync_NoHttpContextCachedTokenInsideRefreshSkewWindow_RefreshesInsteadOfForwardingNearExpiryToken()
    {
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("near-expiry-access", "refresh-1", null, DateTimeOffset.UtcNow.AddSeconds(30)), TestContext.Current.CancellationToken);
        var (refreshService, refreshHandler) = RefreshServiceFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "refreshed-access", refresh_token = "refresh-2", expires_in = 3600 }),
        });
        var harness = CreateHandler(null, AuthenticatedPrincipal("user-1"), _ => new HttpResponseMessage(HttpStatusCode.OK), tokenCache, refreshService);

        await Send(harness.Handler);

        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer refreshed-access");
        refreshHandler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_NoHttpContextNearExpiryTokenWithoutRefreshToken_StillForwardsTheStillValidToken()
    {
        var tokenCache = new ServerTokenCache();
        await tokenCache.SetAsync("user:user-1", new ServerTokenCacheEntry("near-expiry-access", null, null, DateTimeOffset.UtcNow.AddSeconds(30)), TestContext.Current.CancellationToken);
        var harness = CreateHandler(null, AuthenticatedPrincipal("user-1"), _ => new HttpResponseMessage(HttpStatusCode.OK), tokenCache);

        await Send(harness.Handler);

        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer near-expiry-access");
        harness.SessionState.IsSessionExpired.ShouldBeFalse();
    }

    [Fact]
    public async Task SendAsync_NoHttpContextRefreshTokenRotatedWhileWaitingForLock_RefreshesWithTheRotatedToken()
    {
        var inner = new ServerTokenCache();
        await inner.SetAsync("user:user-1", new ServerTokenCacheEntry("expired-access", "refresh-1", null, DateTimeOffset.UtcNow.AddMinutes(-10)), TestContext.Current.CancellationToken);
        // Simulates another caller that held the lock first: it rotated the refresh token, but its
        // access token is already expired again by the time this caller gets the lock.
        var tokenCache = new LockEntryHookTokenCache(inner, () => inner.SetAsync(
            "user:user-1",
            new ServerTokenCacheEntry("rotated-expired-access", "refresh-2", null, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var (refreshService, refreshHandler) = RefreshServiceFactory.Create(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("refresh_token=refresh-2", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { access_token = "refreshed-access", refresh_token = "refresh-3", expires_in = 3600 }),
                }
                : new HttpResponseMessage(HttpStatusCode.BadRequest);
        });
        var harness = CreateHandler(null, AuthenticatedPrincipal("user-1"), _ => new HttpResponseMessage(HttpStatusCode.OK), tokenCache, refreshService);

        await Send(harness.Handler);

        harness.InnerHandler.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer refreshed-access");
        refreshHandler.CallCount.ShouldBe(1);
        harness.SessionState.IsSessionExpired.ShouldBeFalse();
    }

    /// <summary>Runs a hook as the refresh lock is entered, to simulate another caller changing the cache first.</summary>
    private sealed class LockEntryHookTokenCache(IServerTokenCache inner, Func<Task> onLockEntered) : IServerTokenCache
    {
        public Task<ServerTokenCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken = default) => inner.GetAsync(cacheKey, cancellationToken);

        public Task<string?> GetRefreshTokenAsync(string cacheKey, CancellationToken cancellationToken = default) => inner.GetRefreshTokenAsync(cacheKey, cancellationToken);

        public Task SetAsync(string cacheKey, ServerTokenCacheEntry entry, CancellationToken cancellationToken = default) => inner.SetAsync(cacheKey, entry, cancellationToken);

        public Task RemoveAsync(string cacheKey, CancellationToken cancellationToken = default) => inner.RemoveAsync(cacheKey, cancellationToken);

        public Task<T> WithRefreshLockAsync<T>(string cacheKey, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
            => inner.WithRefreshLockAsync(
                cacheKey,
                async ct =>
                {
                    await onLockEntered().ConfigureAwait(false);
                    return await action(ct).ConfigureAwait(false);
                },
                cancellationToken);
    }
}
```

- [ ] **Step 4: Run to verify RED**

Run: `dotnet test --solution SyntaxCircus.Blazor.Auth.slnx --configuration Release`

Expected (real output):

```
failed SyntaxCircus.Blazor.Auth.Tests.ApiAuthHandlerTests.SendAsync_NoHttpContextCachedTokenInsideRefreshSkewWindow_RefreshesInsteadOfForwardingNearExpiryToken
failed SyntaxCircus.Blazor.Auth.Tests.ApiAuthHandlerTests.SendAsync_NoHttpContextRefreshTokenRotatedWhileWaitingForLock_RefreshesWithTheRotatedToken
Test run summary: Failed!
  total: 167
  failed: 2
  succeeded: 165
```

The third new test (`...NearExpiryTokenWithoutRefreshToken_StillForwardsTheStillValidToken`) passes now and must keep passing: it guards against fix (b) dropping a still-valid token.

- [ ] **Step 5: Write the core's tests (compile RED)**

Create `tests/SyntaxCircus.Blazor.Auth.Tests/CachedUserTokenResolverTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;

namespace SyntaxCircus.Blazor.Auth.Tests;

public class CachedUserTokenResolverTests
{
    private const string Key = "user:user-1";

    private static HttpResponseMessage Refreshed(string accessToken = "refreshed-access", string refreshToken = "refresh-next")
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = accessToken, refresh_token = refreshToken, expires_in = 3600 }),
        };

    private static CachedUserTokenResolver CreateResolver(ServerTokenCache cache, TimeProvider? clock = null, int skewSeconds = 60)
        => new(cache, Options.Create(new AuthOptions { TokenCache = { RefreshSkewSeconds = skewSeconds } }), clock);

    private static Task Seed(ServerTokenCache cache, string access, string? refresh, DateTimeOffset expiresAt)
        => cache.SetAsync(Key, new ServerTokenCacheEntry(access, refresh, null, expiresAt), TestContext.Current.CancellationToken);

    [Fact]
    public async Task ResolveAsync_CachedTokenOutsideSkewWindow_ReturnsItWithoutRefreshing()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "cached-access", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());

        var token = await CreateResolver(cache).ResolveAsync(Key, refresh, _ => { }, TestContext.Current.CancellationToken);

        token.ShouldBe("cached-access");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_ClockAdvancesIntoSkewWindow_RefreshesThroughTimeProvider()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new ServerTokenCache();
        await Seed(cache, "cached-access", "refresh-1", clock.GetUtcNow().AddMinutes(10));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());
        var resolver = CreateResolver(cache, clock);

        (await resolver.ResolveAsync(Key, refresh, _ => { }, TestContext.Current.CancellationToken)).ShouldBe("cached-access");
        handler.CallCount.ShouldBe(0);

        // The cache's own expiry check uses the wall clock and still sees a live entry, so only the
        // skew check on the injected clock can trigger this refresh.
        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(30));
        (await resolver.ResolveAsync(Key, refresh, _ => { }, TestContext.Current.CancellationToken)).ShouldBe("refreshed-access");

        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_ZeroSkewConfigured_ForwardsTokenUntilItActuallyExpires()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "cached-access", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());

        var token = await CreateResolver(cache, skewSeconds: 0).ResolveAsync(Key, refresh, _ => { }, TestContext.Current.CancellationToken);

        token.ShouldBe("cached-access");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_InsideSkewWindowWithoutRefreshToken_ReturnsStillValidTokenWithoutPublishing()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "near-expiry-access", null, DateTimeOffset.UtcNow.AddSeconds(30));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());
        var published = new List<string>();

        var token = await CreateResolver(cache).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBe("near-expiry-access");
        handler.CallCount.ShouldBe(0);
        published.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_RefreshFailsInsideSkewWindow_ReturnsStillValidTokenWithoutPublishing()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "near-expiry-access", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var published = new List<string>();

        var token = await CreateResolver(cache).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBe("near-expiry-access");
        handler.CallCount.ShouldBe(1);
        published.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ExpiredTokenRefreshFails_ReturnsNullAndPublishesExpiryForKey()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "expired-access", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        var (refresh, _) = RefreshServiceFactory.Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var published = new List<string>();

        var token = await CreateResolver(cache).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        published.ShouldBe([Key]);
    }

    [Fact]
    public async Task ResolveAsync_NothingCached_ReturnsNullWithoutRefreshingOrPublishing()
    {
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());
        var published = new List<string>();

        var token = await CreateResolver(new ServerTokenCache()).ResolveAsync(Key, refresh, published.Add, TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        handler.CallCount.ShouldBe(0);
        published.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentCallersWithExpiredToken_MakeExactlyOneRefreshCall()
    {
        var cache = new ServerTokenCache();
        await Seed(cache, "expired-access", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-10));
        var (refresh, handler) = RefreshServiceFactory.Create(_ => Refreshed());
        var resolver = CreateResolver(cache);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => resolver.ResolveAsync(Key, refresh, __ => { }, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        tokens.ShouldAllBe(t => t == "refreshed-access");
        handler.CallCount.ShouldBe(1);
    }
}
```

Run: `dotnet build tests/SyntaxCircus.Blazor.Auth.Tests -c Release`
Expected: `error CS0246: The type or namespace name 'CachedUserTokenResolver' could not be found`.

- [ ] **Step 6: Implement the core**

Create `src/SyntaxCircus.Blazor.Auth/CachedUserTokenResolver.cs`:

```csharp
namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// The single cache-then-refresh implementation for the Blazor circuit phase, where no
/// <see cref="HttpContext"/> (and so no auth cookie) is available. Shared by
/// <see cref="ApiAuthHandler"/> and the user access-token provider so the two can never
/// drift apart. It reads <see cref="IServerTokenCache"/>, refreshes under the per-key lock when
/// the cached access token is missing, expired or inside the configured refresh-skew window, and
/// writes the refreshed tokens back.
/// </summary>
/// <remarks>
/// Registered as a singleton, so it only takes singleton-safe dependencies. The refresh service
/// (a typed <see cref="HttpClient"/>, transient) and the expiry publisher (which differs between
/// the production and compatibility handler constructors) are passed per call instead.
/// </remarks>
internal sealed class CachedUserTokenResolver(
    IServerTokenCache tokenCache,
    IOptions<AuthOptions> options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Returns a usable access token for <paramref name="cacheKey"/>, refreshing it if needed, or
    /// <see langword="null"/> when there is none. A failed refresh publishes expiry through
    /// <paramref name="onExpired"/> only when no still-valid token remains to hand back.
    /// </summary>
    public async Task<string?> ResolveAsync(
        string cacheKey,
        OidcTokenRefreshService refreshService,
        Action<string> onExpired,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(refreshService);
        ArgumentNullException.ThrowIfNull(onExpired);

        var refreshSkew = TimeSpan.FromSeconds(Math.Max(0, options.Value.TokenCache.RefreshSkewSeconds));

        var entry = await tokenCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (entry is not null && entry.IsUsable(clock.GetUtcNow(), refreshSkew))
        {
            return entry.AccessToken;
        }

        // Fast exit that avoids taking the lock when nothing could be refreshed anyway. The refresh
        // token actually used is re-read inside the lock below.
        var observedRefreshToken = await tokenCache.GetRefreshTokenAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(observedRefreshToken))
        {
            return StillValidToken(entry, clock.GetUtcNow());
        }

        return await tokenCache.WithRefreshLockAsync(
            cacheKey,
            async lockCt =>
            {
                var cached = await tokenCache.GetAsync(cacheKey, lockCt).ConfigureAwait(false);
                if (cached is not null && cached.IsUsable(clock.GetUtcNow(), refreshSkew))
                {
                    return cached.AccessToken;
                }

                // A caller that held the lock before us may have rotated the refresh token (and
                // IdPs that rotate reject the old one), so use the value as of now, not the one read
                // before waiting for the lock.
                var refreshToken = await tokenCache.GetRefreshTokenAsync(cacheKey, lockCt).ConfigureAwait(false);
                var stillValid = StillValidToken(cached, clock.GetUtcNow());
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    return stillValid;
                }

                var refreshed = await refreshService.RefreshAsync(refreshToken, lockCt).ConfigureAwait(false);
                if (refreshed is null)
                {
                    if (stillValid is null)
                    {
                        onExpired(cacheKey);
                    }

                    return stillValid;
                }

                await tokenCache.SetAsync(
                    cacheKey,
                    new ServerTokenCacheEntry(refreshed.AccessToken, refreshed.RefreshToken, refreshed.IdToken, refreshed.ExpiresAt),
                    lockCt).ConfigureAwait(false);
                return refreshed.AccessToken;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string? StillValidToken(ServerTokenCacheEntry? entry, DateTimeOffset now)
        => entry is not null && !string.IsNullOrWhiteSpace(entry.AccessToken) && entry.ExpiresAtUtc > now
            ? entry.AccessToken
            : null;
}
```

Notes for the implementer: the pre-lock `GetRefreshTokenAsync` read is only a fast exit; the value passed to `RefreshAsync` is the one re-read inside the lock (fix a). Every time comparison goes through the injected `TimeProvider` and `ServerTokenCacheEntry.IsUsable` (fix b). `ServerTokenCache.GetAsync` still filters on the wall clock, which is why a `FakeTimeProvider` test can only observe the skew path.

- [ ] **Step 7: Wire `ApiAuthHandler` to the core**

In `src/SyntaxCircus.Blazor.Auth/ApiAuthHandler.cs`:

(7a) Add a field after `compatibilitySessionStateService`:

```csharp
    private readonly CachedUserTokenResolver userTokenResolver;
```

(7b) Replace the body of the **first public constructor** (the 7-arg one). It currently ends `SessionExpiryBroker sessionExpiryBroker)` followed by a `{ ... }` body assigning fields. Make the public constructor chain to a new internal one, and move the body to the internal one. The result must read:

```csharp
    /// <summary>Creates the handler used by the registered production path.</summary>
    public ApiAuthHandler(
        IHttpContextAccessor httpContextAccessor,
        IServerTokenCache tokenCache,
        ServerRequestOidcTokenResolver resolver,
        OidcTokenRefreshService refreshService,
        IApiClientCredentialsTokenProvider clientCredentialsTokenProvider,
        ILogger<ApiAuthHandler> logger,
        SessionExpiryBroker sessionExpiryBroker)
        : this(
            httpContextAccessor,
            tokenCache,
            resolver,
            refreshService,
            clientCredentialsTokenProvider,
            logger,
            sessionExpiryBroker,
            new CachedUserTokenResolver(tokenCache, Options.Create(new AuthOptions())))
    {
    }

    /// <summary>
    /// Creates the handler with the shared circuit-phase token resolver. Internal because the
    /// resolver is; the DI registration uses this so the host's configured refresh skew applies.
    /// </summary>
    internal ApiAuthHandler(
        IHttpContextAccessor httpContextAccessor,
        IServerTokenCache tokenCache,
        ServerRequestOidcTokenResolver resolver,
        OidcTokenRefreshService refreshService,
        IApiClientCredentialsTokenProvider clientCredentialsTokenProvider,
        ILogger<ApiAuthHandler> logger,
        SessionExpiryBroker sessionExpiryBroker,
        CachedUserTokenResolver userTokenResolver)
    {
        this.userTokenResolver = userTokenResolver;
        this.httpContextAccessor = httpContextAccessor;
        this.tokenCache = tokenCache;
        this.resolver = resolver;
        this.refreshService = refreshService;
        this.clientCredentialsTokenProvider = clientCredentialsTokenProvider;
        this.logger = logger;
        this.sessionExpiryBroker = sessionExpiryBroker;
    }
```

The 9-arg compatibility constructor is not touched: it already chains to the 7-arg public one.

(7c) Delete the two private methods `TryResolveCachedTokenAsync` and `TryRefreshInCircuitAsync` (everything from `private async Task<string?> TryResolveCachedTokenAsync(` down to, and including, the closing brace of `TryRefreshInCircuitAsync`; `PublishExpired` stays) and put this single method in their place:

```csharp
    private Task<string?> TryResolveCachedTokenAsync(string cacheKey, CancellationToken cancellationToken)
        => userTokenResolver.ResolveAsync(cacheKey, refreshService, PublishExpired, cancellationToken);
```

Both circuit call sites (path A, the `BlazorTokenRequestOptions.CacheKey` branch, and path B, the compatibility `AuthenticationStateProvider` branch) already call `TryResolveCachedTokenAsync`, so no other handler code changes.

(7d) In `src/SyntaxCircus.Blazor.Auth/ServerRequestOidcTokenResolver.cs`, the doc comment on `ResolveNearExpiryTokenAsync` has a `cref` to the method just removed (it would now be a CS1574 build error). Change:

```csharp
    /// refresh (<see cref="ApiAuthHandler.TryRefreshInCircuitAsync"/>) already rotated the refresh
```

to:

```csharp
    /// refresh (<see cref="CachedUserTokenResolver.ResolveAsync"/>) already rotated the refresh
```

- [ ] **Step 8: Register the core and update the DI factory**

In `src/SyntaxCircus.Blazor.Auth/BlazorTokenForwardingExtensions.cs`, directly after `services.AddScoped<ServerRequestOidcTokenResolver>();` add:

```csharp
        services.AddSingleton<CachedUserTokenResolver>();
```

and extend the `ApiAuthHandler` factory by one argument, so it ends:

```csharp
            sp.GetRequiredService<SessionExpiryBroker>(),
            sp.GetRequiredService<CachedUserTokenResolver>()));
```

- [ ] **Step 9: Run to verify GREEN**

Run: `dotnet test --solution SyntaxCircus.Blazor.Auth.slnx --configuration Release`

Expected (real output): `failed: 0`, `succeeded: 175` (164 existing + 3 handler + 8 core), no warnings.

- [ ] **Step 10: Pin the DI factory change**

In `ApiAuthHandlerTests.cs`, change the signature of `BuildRegisteredProvider` and its `AddBlazorTokenForwarding` call so a test can supply configuration. The signature line becomes:

```csharp
    private static ServiceProvider BuildRegisteredProvider(ClaimsPrincipal principal, HttpMessageHandler? refreshHandler = null, IApiClientCredentialsTokenProvider? clientCredentials = null, Dictionary<string, string?>? configuration = null)
```

and inside it `services.AddBlazorTokenForwarding(BuildConfiguration([]));` becomes `services.AddBlazorTokenForwarding(BuildConfiguration(configuration ?? []));`. Then delete the file's last line (class `}`) again and append:

```csharp

    [Fact]
    public async Task RegisteredHandler_UsesConfiguredRefreshSkew()
    {
        var refreshHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Refresh should not have been called."));
        using var provider = BuildRegisteredProvider(
            AuthenticatedPrincipal("user-1"),
            refreshHandler,
            configuration: new Dictionary<string, string?> { ["Authentication:Oidc:TokenCache:RefreshSkewSeconds"] = "0" });
        await provider.GetRequiredService<IServerTokenCache>().SetAsync(
            "user:user-1",
            new ServerTokenCacheEntry("near-expiry-access", "refresh-1", null, DateTimeOffset.UtcNow.AddSeconds(30)),
            TestContext.Current.CancellationToken);

        using var handlerScope = provider.CreateScope();
        handlerScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var handler = handlerScope.ServiceProvider.GetRequiredService<ApiAuthHandler>();
        var inner = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        handler.InnerHandler = inner;

        await Send(handler, cacheKey: "user:user-1");

        inner.LastRequest!.HeaderValue("Authorization").ShouldBe("Bearer near-expiry-access");
        refreshHandler.CallCount.ShouldBe(0);
    }
}
```

Run the full suite. Expected: `failed: 0`, `succeeded: 176`.

- [ ] **Step 11: Mutation checks**

For each row: back up the file (`cp F F.bak`), apply the change, run the test command, restore (`mv F.bak F`). Confirm the suite is green again afterwards (`succeeded: 176`). `C` = `src/SyntaxCircus.Blazor.Auth/CachedUserTokenResolver.cs`. Results below are real.

| # | Mutation | Expected / observed failing test(s) |
|---|---|---|
| M1 | in `C`, `RefreshAsync(refreshToken, lockCt)` to `RefreshAsync(observedRefreshToken, lockCt)` (use the pre-lock token) | `SendAsync_NoHttpContextRefreshTokenRotatedWhileWaitingForLock_RefreshesWithTheRotatedToken` (1 failed) |
| M2 | in `C`, both `IsUsable(clock.GetUtcNow(), refreshSkew)` to `ExpiresAtUtc > clock.GetUtcNow()` (ignore skew) | the handler skew test, `ResolveAsync_ClockAdvancesIntoSkewWindow_RefreshesThroughTimeProvider`, `ResolveAsync_RefreshFailsInsideSkewWindow_ReturnsStillValidTokenWithoutPublishing` (3 failed) |
| M3 | in `C`, every `clock.GetUtcNow()` to `DateTimeOffset.UtcNow` (bypass `TimeProvider`) | `ResolveAsync_ClockAdvancesIntoSkewWindow_RefreshesThroughTimeProvider` (1 failed) |
| M4 | in `C`, the in-lock re-check `if (cached is not null && cached.IsUsable(...))` to `if (cached is not null && cached.AccessToken == "never")` | `ResolveAsync_ConcurrentCallersWithExpiredToken_MakeExactlyOneRefreshCall` (1 failed) |
| M5 | in `BlazorTokenForwardingExtensions.cs`, last factory argument `sp.GetRequiredService<CachedUserTokenResolver>()` to `new CachedUserTokenResolver(sp.GetRequiredService<IServerTokenCache>(), Options.Create(new AuthOptions()))` | `RegisteredHandler_UsesConfiguredRefreshSkew` (1 failed) |
| M6 | in `C`, `if (stillValid is null)` to `if (true)` (always publish on failure) | `ResolveAsync_RefreshFailsInsideSkewWindow_ReturnsStillValidTokenWithoutPublishing` (1 failed) |

Note on M4: do not write it as `if (false)`; that produces `CS0162: Unreachable code` and a build break, not a test failure.

- [ ] **Step 12: Commit**

```bash
git add Directory.Packages.props src tests
git commit -m "refactor: extract CachedUserTokenResolver; honor refresh skew on circuit path

Moves the circuit-phase cache-then-refresh logic out of ApiAuthHandler into an
internal singleton so it can be shared. Fixes the refresh token being read before
the lock (a waiting caller could replay a rotated token) and applies
RefreshSkewSeconds on the circuit path through TimeProvider. A token inside the
skew window with no usable refresh is still forwarded until it really expires.
Public ApiAuthHandler constructors are unchanged.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01ReiWu2p7mSuArnHAMeBiMi"
```

---

### Task 2: The public provider, its registration and its tests

**Files:**
- Create: `src/SyntaxCircus.Blazor.Auth/Tokens/IUserAccessTokenProvider.cs`
- Create: `src/SyntaxCircus.Blazor.Auth/Tokens/UserAccessTokenProvider.cs`
- Create: `tests/SyntaxCircus.Blazor.Auth.Tests/UserAccessTokenProviderTests.cs`
- Modify: `src/SyntaxCircus.Blazor.Auth/BlazorTokenForwardingExtensions.cs`

**Interfaces:**
- Consumes (from Task 1): `CachedUserTokenResolver.ResolveAsync(string cacheKey, OidcTokenRefreshService refreshService, Action<string> onExpired, CancellationToken cancellationToken)` and its singleton registration. Existing: `ServerRequestOidcTokenResolver.ResolveAsync(HttpContext, CancellationToken)` returning `ServerRequestOidcTokenResolution(Token, Subject, CacheKey, IsExpired)`, `IUserTokenCacheKeyProvider.GetCacheKey(ClaimsPrincipal?)`, `SessionExpiryBroker.Publish(string)`, `AuthenticationStateProvider`.
- Produces:
  - `public interface IUserAccessTokenProvider { ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default); }` in namespace `SyntaxCircus.Blazor.Auth` (the package's only new public type).
  - `internal sealed class UserAccessTokenProvider(IHttpContextAccessor, ServerRequestOidcTokenResolver, AuthenticationStateProvider, IUserTokenCacheKeyProvider, CachedUserTokenResolver, OidcTokenRefreshService, SessionExpiryBroker, ILogger<UserAccessTokenProvider>) : IUserAccessTokenProvider`.
  - DI: `services.AddScoped<IUserAccessTokenProvider, UserAccessTokenProvider>()` inside `AddBlazorTokenForwarding`.

- [ ] **Step 1: Write the failing tests (compile RED)**

Create `tests/SyntaxCircus.Blazor.Auth.Tests/UserAccessTokenProviderTests.cs`:

```csharp
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
        AuthenticationStateProvider? stateProvider = null)
    {
        var cache = new ServerTokenCache();
        var (refresh, refreshHandler) = RefreshServiceFactory.Create(refreshResponder ?? (_ => Refreshed()));
        var authOptions = Options.Create(new AuthOptions());
        var keyProvider = new UserTokenCacheKeyProvider();
        var broker = new SessionExpiryBroker();
        var state = new SessionStateService(broker);
        state.Observe(Key);
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        var provider = new UserAccessTokenProvider(
            accessor,
            new ServerRequestOidcTokenResolver(cache, refresh, keyProvider, authOptions, NullLogger<ServerRequestOidcTokenResolver>.Instance, clock),
            stateProvider ?? StateProvider(principal),
            keyProvider,
            new CachedUserTokenResolver(cache, authOptions, clock),
            refresh,
            broker,
            NullLogger<UserAccessTokenProvider>.Instance);
        return new Harness(provider, cache, refreshHandler, state);
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
    public void PublicSurface_ExposesOnlyTheProviderInterface()
    {
        var exported = typeof(IUserAccessTokenProvider).Assembly.GetExportedTypes().Select(t => t.Name).ToList();

        exported.ShouldContain(nameof(IUserAccessTokenProvider));
        exported.ShouldNotContain("UserAccessTokenProvider");
        exported.ShouldNotContain("CachedUserTokenResolver");
    }
}
```

Run: `dotnet build tests/SyntaxCircus.Blazor.Auth.Tests -c Release`
Expected: `error CS0246: The type or namespace name 'UserAccessTokenProvider' could not be found` (and the same for `IUserAccessTokenProvider`).

- [ ] **Step 2: Create the public interface with its XML docs**

Create `src/SyntaxCircus.Blazor.Auth/Tokens/IUserAccessTokenProvider.cs`:

```csharp
namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Gives code that cannot go through an <see cref="HttpClient"/> pipeline (notably a SignalR
/// <c>HubConnection</c>) the signed-in user's current OIDC access token, refreshed when it is
/// missing, expired or inside the configured refresh-skew window. It shares its cache-then-refresh
/// logic with <see cref="ApiAuthHandler"/>, so both see the same token and a single refresh.
/// </summary>
/// <remarks>
/// <para>
/// <b>Null result.</b> <see langword="null"/> means the user is anonymous, the session has lapsed
/// (no usable token and nothing to refresh from), or the refresh failed. It is never a
/// client-credentials (machine-to-machine) token: this provider acts only as the signed-in user, so
/// callers must treat <see langword="null"/> as "not authenticated" and not as "send anonymously".
/// </para>
/// <para>
/// <b>Scope.</b> Registered as a scoped service by <c>AddBlazorTokenForwarding</c>. Resolve it from
/// the Blazor circuit scope, for example by injecting it into a component or a scoped service, and
/// never capture it in a singleton. Outside a circuit scope, where no authentication state is
/// available, it returns <see langword="null"/> rather than throwing.
/// </para>
/// <para>
/// <b>SignalR usage.</b>
/// </para>
/// <code>
/// // In a component or scoped service: [Inject] IUserAccessTokenProvider TokenProvider
/// var connection = new HubConnectionBuilder()
///     .WithUrl(hubUrl, options =>
///         options.AccessTokenProvider = () => TokenProvider.GetAccessTokenAsync().AsTask())
///     .WithAutomaticReconnect()
///     .Build();
/// </code>
/// <para>
/// SignalR invokes the delegate on every (re)connect, so a long-lived connection picks up a
/// refreshed token each time it reconnects.
/// </para>
/// </remarks>
public interface IUserAccessTokenProvider
{
    /// <summary>Returns the current user's access token, or <see langword="null"/> (see remarks on the interface).</summary>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Create the implementation**

Create `src/SyntaxCircus.Blazor.Auth/Tokens/UserAccessTokenProvider.cs`:

```csharp
using Microsoft.AspNetCore.Components.Authorization;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Scoped <see cref="IUserAccessTokenProvider"/>. Mirrors the user-token half of
/// <see cref="ApiAuthHandler"/> (HttpContext present: <see cref="ServerRequestOidcTokenResolver"/>;
/// absent: authentication state, then <see cref="CachedUserTokenResolver"/>) and deliberately has no
/// client-credentials fallback.
/// </summary>
internal sealed class UserAccessTokenProvider(
    IHttpContextAccessor httpContextAccessor,
    ServerRequestOidcTokenResolver requestResolver,
    AuthenticationStateProvider authenticationStateProvider,
    IUserTokenCacheKeyProvider cacheKeyProvider,
    CachedUserTokenResolver circuitResolver,
    OidcTokenRefreshService refreshService,
    SessionExpiryBroker sessionExpiryBroker,
    ILogger<UserAccessTokenProvider> logger) : IUserAccessTokenProvider
{
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            return await ResolveForRequestAsync(httpContext, cancellationToken).ConfigureAwait(false);
        }

        AuthenticationState authState;
        try
        {
            authState = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Thrown when resolved outside a circuit scope: there is no user to act as.
            logger.LogWarning(ex, "Failed to resolve authentication state; no user access token is available.");
            return null;
        }

        if (authState.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var cacheKey = cacheKeyProvider.GetCacheKey(authState.User);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return null;
        }

        return await circuitResolver.ResolveAsync(cacheKey, refreshService, sessionExpiryBroker.Publish, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveForRequestAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var resolution = await requestResolver.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolution.Token) && resolution.IsExpired && !string.IsNullOrWhiteSpace(resolution.CacheKey))
        {
            sessionExpiryBroker.Publish(resolution.CacheKey);
        }

        return string.IsNullOrWhiteSpace(resolution.Token) ? null : resolution.Token;
    }
}
```

Notes: only `GetAuthenticationStateAsync` is inside the `try`, so an `InvalidOperationException` raised by a refresh is not swallowed as "outside a circuit". The explicit `IsAuthenticated` check duplicates a guard inside `UserTokenCacheKeyProvider.GetCacheKey(ClaimsPrincipal)`; it is kept deliberately (mirrors the handler, and keeps the provider safe if the key provider is replaced).

- [ ] **Step 4: Register it**

In `BlazorTokenForwardingExtensions.cs`, directly below `services.AddSingleton<CachedUserTokenResolver>();` add:

```csharp
        services.AddScoped<IUserAccessTokenProvider, UserAccessTokenProvider>();
```

- [ ] **Step 5: Run to verify GREEN**

Run: `dotnet test --solution SyntaxCircus.Blazor.Auth.slnx --configuration Release`
Expected (real output): `failed: 0`, `succeeded: 192`, no warnings.

- [ ] **Step 6: Mutation checks**

Same procedure as Task 1 step 11. `U` = `src/SyntaxCircus.Blazor.Auth/Tokens/UserAccessTokenProvider.cs`.

| # | Mutation | Expected / observed failing test(s) |
|---|---|---|
| P1 | in `U`, delete `sessionExpiryBroker.Publish(resolution.CacheKey);` | `GetAccessTokenAsync_HttpContextExpiredCookieTokenWithoutRefreshToken_ReturnsNullAndPublishesExpiry` |
| P2 | in `U`, `catch (InvalidOperationException ex)` to `catch (ArgumentException ex)` | `GetAccessTokenAsync_OutsideCircuitScope_ReturnsNullInsteadOfThrowing` |
| P3 | in `U`, delete the `if (authState.User.Identity?.IsAuthenticated != true) { return null; }` block | **survives (equivalent mutant):** `UserTokenCacheKeyProvider.GetCacheKey(ClaimsPrincipal)` already returns null for an unauthenticated principal, so the provider's check is defence in depth. The test `...UnauthenticatedUserWithSubjectClaim_ReturnsNullEvenWithCachedTokenForTheKey` still pins the observable behaviour. |
| P4 | in `U`, `if (httpContext is not null)` to `if (httpContext is null && false)` (ignore the HttpContext) | the three request-mode tests: `...HttpContextWithValidCookieToken...`, `...StaleHttpContextWithExpiredCookieToken...`, `...HttpContextExpiredCookieTokenWithoutRefreshToken...` |
| P5 | in `BlazorTokenForwardingExtensions.cs`, `AddScoped<IUserAccessTokenProvider` to `AddSingleton<IUserAccessTokenProvider` | `Registered_ResolvesPerScope...`, `Registered_AnonymousUserNeverFallsBackToClientCredentials` (scope validation rejects it) |
| P6 | in `U`, `refreshService, sessionExpiryBroker.Publish` to `refreshService, _ => { }` (drop circuit expiry publishing) | `GetAccessTokenAsync_NoHttpContextRefreshFails_ReturnsNullAndPublishesExpiryToSubscribedSession` |

Restore every file after each run and confirm `succeeded: 192`.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat: add IUserAccessTokenProvider for user-scoped access tokens

Public interface returning the signed-in user's current access token, refreshed
through the same shared core as ApiAuthHandler, for callers that cannot use an
HttpClient pipeline (e.g. SignalR HttpConnectionOptions.AccessTokenProvider).
Scoped, registered by AddBlazorTokenForwarding, returns null for anonymous or
lapsed sessions and never falls back to client credentials.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01ReiWu2p7mSuArnHAMeBiMi"
```

---

### Task 3: Docs, the version hint, and hand-off to the owner

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: the public surface from Task 2 (`IUserAccessTokenProvider`) and the behaviour changes from Task 1 (skew on the circuit path, in-lock refresh-token re-read).
- Produces: documentation only. No new symbols. The commit message carries the `+semver: minor` hint that makes GitVersion produce 0.2.0.

- [ ] **Step 1: README, entry-point list**

In `README.md`, in the "What each call does" list, add this item immediately after the `.AddHttpMessageHandler<ApiAuthHandler>()` item:

```markdown
- **`IUserAccessTokenProvider`** — inject it (scoped) wherever you need the signed-in user's current access token as a string, for example a SignalR `HubConnection`. It is registered by `AddBlazorTokenForwarding` (no extra call); see [Using the token outside `HttpClient`](#using-the-token-outside-httpclient).
```

- [ ] **Step 2: README, "How it works" note and the SignalR section**

Immediately before the `## Session-expiry UX` heading, insert:

````markdown
`ApiAuthHandler` and `IUserAccessTokenProvider` share one cache-then-refresh implementation for the circuit case, so a token refreshed by one is immediately visible to the other, and concurrent callers still make a single token-endpoint call. The provider has **no** client-credentials fallback: it only ever returns the signed-in user's token.

## Using the token outside `HttpClient`

Anything that needs the raw bearer token rather than an `HttpClient` pipeline, such as a SignalR `HubConnection`, can inject the scoped `IUserAccessTokenProvider`:

```csharp
// In a component or scoped service: [Inject] IUserAccessTokenProvider TokenProvider
var connection = new HubConnectionBuilder()
    .WithUrl(hubUrl, options =>
        options.AccessTokenProvider = () => TokenProvider.GetAccessTokenAsync().AsTask())
    .WithAutomaticReconnect()
    .Build();
```

- `GetAccessTokenAsync` returns `null` when the user is anonymous, the session has lapsed, or the refresh failed. It is **never** a client-credentials token, so treat `null` as "not signed in" rather than sending the request anonymously. A failed refresh also marks the session expired (see below), exactly as `ApiAuthHandler` does.
- It is scoped. Resolve it from the circuit scope and never capture it in a singleton. Outside a circuit scope, where no authentication state exists, it returns `null` instead of throwing.
- SignalR calls the delegate on every (re)connect, so a long-lived connection picks up a refreshed token each time it reconnects.
````

- [ ] **Step 3: README, behavioural notes and configuration row**

In the "Behavioral notes" list, insert these two bullets immediately before the bullet that starts `**A circuit-path refresh's rotated refresh token is honored`:

```markdown
- **The circuit path honors `RefreshSkewSeconds`.** Before this was fixed, a token cached for a SignalR circuit was forwarded until its exact expiry, so a call made just before expiry could reach the API with a token that expired in flight. Now a cached token within `RefreshSkewSeconds` of expiry is refreshed first, the same as on the HTTP-request path; if that refresh fails but the token has not actually expired yet, it is still forwarded. Keep `RefreshSkewSeconds` comfortably below your access-token lifetime, or every call refreshes. This applies to `ApiAuthHandler` and `IUserAccessTokenProvider` alike.
- **The refresh token is re-read after the refresh lock is acquired.** A caller waiting for the lock uses the refresh token as of the moment it gets the lock, so it never replays a refresh token another caller has just rotated.
```

In the configuration table, change the `RefreshSkewSeconds` row's Notes cell to:

```markdown
how early (before actual expiry) a token is treated as due for refresh, on both the HTTP-request and circuit paths
```

- [ ] **Step 4: AGENTS.md, repo map and gotchas**

In the repo map, add this line directly under the `ApiAuthHandler.cs` line:

```
  CachedUserTokenResolver.cs           — internal singleton; the one cache-then-refresh-under-lock implementation for the circuit path, shared by ApiAuthHandler and UserAccessTokenProvider
```

and replace the `Tokens/` entry's last line so it reads:

```
  Tokens/                              — OidcTokenRefreshService, OidcTokenExpiry, IApiClientCredentialsTokenProvider,
                                          ApiClientCredentialsTokenProvider, IUserTokenCacheKeyProvider, UserTokenCacheKeyProvider,
                                          IUserAccessTokenProvider (public) + UserAccessTokenProvider (internal, scoped; user token only, no M2M fallback)
```

In "Gotchas", insert these two bullets immediately before the `**The \`TokenSource\` enum in \`ApiAuthHandler\`**` bullet:

```markdown
- **Circuit-path token logic lives only in `CachedUserTokenResolver`.** Both `ApiAuthHandler` and `UserAccessTokenProvider` call it; a fix to cache-then-refresh, skew or lock behavior goes there, not in either caller. It is a singleton, so it must not take scoped or typed-`HttpClient` dependencies (`OidcTokenRefreshService` and the expiry publisher are passed per call). The two public `ApiAuthHandler` constructors build their own instance with default `AuthOptions`; the internal constructor (used by the DI registration) takes the shared one so the configured `RefreshSkewSeconds` applies.
- **`IUserAccessTokenProvider` is the only new public type; keep it that way.** It is scoped, never resolve it from the root provider or capture it in a singleton, and it must never fall back to client credentials. `PublicSurface_ExposesOnlyTheProviderInterface` guards the accessibility of the implementation types.
```

- [ ] **Step 5: Final verification**

```bash
dotnet build SyntaxCircus.Blazor.Auth.slnx --configuration Release
dotnet test --solution SyntaxCircus.Blazor.Auth.slnx --configuration Release
git diff cb13df2 --stat
```

Expected: build succeeds with 0 warnings, `failed: 0`, `succeeded: 192`. The diff touches only the files in the File Structure table; confirm no `*.csproj` other than the test project changed and no version number was edited.

- [ ] **Step 6: Commit with the version hint**

GitVersion 6 in TrunkBased mode with `commit-message-incrementing: Enabled` bumps the patch by default (proven below: v0.1.7 becomes 0.1.8). A public API addition must be a minor bump (0.2.0), so the commit message needs a `+semver: minor` line. Merges here are squash merges, so the hint must also be in the squash commit message (Step 8).

```bash
git add README.md AGENTS.md
git commit -m "docs: document IUserAccessTokenProvider and circuit-path skew

README: entry-point item, how-it-works note, SignalR usage section, behavioural
notes and the RefreshSkewSeconds row. AGENTS.md: repo map and gotchas.

+semver: minor

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01ReiWu2p7mSuArnHAMeBiMi"
```

- [ ] **Step 7: Prove the version (optional but recommended, in a throwaway clone, not the working repo)**

```bash
git clone <repo> /tmp/ver && cd /tmp/ver && git checkout main
git merge --squash origin/feat/user-access-token-provider   # or the local branch
git commit -m "feat: add IUserAccessTokenProvider (#18)"
dotnet pack src/SyntaxCircus.Blazor.Auth -c Release -o ../out && ls ../out   # expect ...0.1.8.nupkg
git commit --amend -m "feat: add IUserAccessTokenProvider (#18)" -m "+semver: minor"
rm -rf ../out && dotnet pack src/SyntaxCircus.Blazor.Auth -c Release -o ../out && ls ../out   # expect ...0.2.0.nupkg
```

Observed in the scratch proof: without the footer `SyntaxCircus.Blazor.Auth.0.1.8.nupkg`; with `+semver: minor` anywhere in the commit message (a body line, or among trailers) `SyntaxCircus.Blazor.Auth.0.2.0.nupkg`. On a branch not named `main` GitVersion adds a prerelease label (for example `0.2.0-main-sim.1`); that is expected and only the stable `main` build is published.

- [ ] **Step 8: Push the branch and open the PR (does not publish)**

CI's publish job runs only for a `push` to `refs/heads/main` (`if: github.ref == 'refs/heads/main' && github.event_name == 'push'`). Pushing a feature branch and opening a PR runs build and test only.

```bash
git push -u origin feat/user-access-token-provider
gh pr create --title "feat: add IUserAccessTokenProvider" --body "Adds the public IUserAccessTokenProvider (scoped, user token only, no client-credentials fallback) sharing one cache-then-refresh core with ApiAuthHandler. Also fixes the circuit path ignoring RefreshSkewSeconds and reading the refresh token before the lock. Public ApiAuthHandler constructors are unchanged.

+semver: minor

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01ReiWu2p7mSuArnHAMeBiMi"
```

Wait for the PR's `build` job to be green.

- [ ] **Step 9: Owner merges (STOP: the executing agent does not merge)**

The owner merges the PR. **Merging to `main` publishes to nuget.org automatically and the version cannot be unpublished.** Before clicking "Squash and merge" the owner must make sure the squash commit message (the editable box GitHub shows) still contains the line `+semver: minor`. If it does not, add it; otherwise the package ships as 0.1.8 instead of 0.2.0 and a 0.2.0 can only be produced by another commit.

- [ ] **Step 10: Verify the release after the merge**

After the owner has merged and the `Build` workflow on `main` finishes (the `publish` job runs in `environment: release` and may need approval):

```bash
git fetch --tags && git tag --list 'v0.2*'      # expect v0.2.0
dotnet package search SyntaxCircus.Blazor.Auth --exact-match --source https://api.nuget.org/v3/index.json --prerelease
# or: curl -s https://api.nuget.org/v3-flatcontainer/syntaxcircus.blazor.auth/index.json
```

Expected: `0.2.0` appears in the nuget.org version list (indexing can lag a few minutes). If the tag is `v0.1.8` instead, the version hint was lost in the squash message: report it, do not retry by hand.

Downstream (outside this plan): TechStrap bumps `Directory.Packages.props:18` from 0.1.7 to 0.2.0 and re-checks `AdminRules` before PHASE-10b.
