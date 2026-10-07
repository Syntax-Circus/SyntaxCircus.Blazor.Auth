# AGENTS.md

Guidance for AI coding agents (and contributors generally) working in this repo. Read [`README.md`](README.md) first for what the package does and how consumers use it — this file is about working *on* the package itself.

This is a small NuGet library, not an application. "Done" means: it builds clean under `TreatWarningsAsErrors`, the test suite is green, and the README's configuration table / entry-point list stays accurate if you changed public surface or config.

## Repo map

```
src/SyntaxCircus.Blazor.Auth/
  BlazorTokenForwardingExtensions.cs   — the two DI/pipeline entry points (AddBlazorTokenForwarding, UseBlazorTokenCache)
  ApiAuthHandler.cs                    — DelegatingHandler; dual-mode (HttpContext request vs. SignalR circuit)
  CachedUserTokenResolver.cs           — internal singleton; the one cache-then-refresh-under-lock implementation for the circuit path, shared by ApiAuthHandler and UserAccessTokenProvider
  TransientRefreshFailure.cs           — internal filter (network/timeout/malformed/discovery) shared by the circuit and request paths
  BlazorTokenCacheMiddleware.cs        — eager per-request token resolution
  ServerRequestOidcTokenResolver.cs
  ServerRequestOidcTokenResolution.cs
  SessionStateService.cs               — scoped UI signal (IsSessionExpired / OnSessionChanged)
  Caching/                             — IServerTokenCache + ServerTokenCache (in-process) + RedisServerTokenCache
  Tokens/                              — OidcTokenRefreshService, OidcTokenExpiry, IApiClientCredentialsTokenProvider,
                                          ApiClientCredentialsTokenProvider, IUserTokenCacheKeyProvider, UserTokenCacheKeyProvider,
                                          IUserAccessTokenProvider (public) + UserAccessTokenProvider (internal, scoped; user token only, no M2M fallback)
  Options/                             — AuthOptions, ApiOptions, ApiClientCredentialsOptions
  Diagnostics/                         — namespace SyntaxCircus.Blazor.Auth.Diagnostics; AuthDebugEndpoints (opt-in)

tests/SyntaxCircus.Blazor.Auth.Tests/
  mirrors src/ 1:1 by filename, e.g. ApiAuthHandlerTests.cs ↔ ApiAuthHandler.cs
  Infrastructure/                      — shared test doubles (see Testing below)
```

`Diagnostics/` and `Components/*` are deliberately in their own namespaces, separate from the root `SyntaxCircus.Blazor.Auth` namespace, so host apps can opt into diagnostics/UI extras without a blanket `using` pulling them in. Preserve this when adding new optional pieces — don't put a new opt-in extra in the root namespace.

## Conventions

- **DI registration.** Everything funnels through `AddBlazorTokenForwarding` in one call — no piecemeal per-feature registration extension methods. New core services go inside it, following the existing order: accessors/scoped state → options binding → conditional cache registration last (it depends on options already being read).
- **Options pattern.** Every options class has `public const string SectionName`, is registered via `services.Configure<T>(configuration.GetSection(T.SectionName))`, uses non-nullable string defaults (`= string.Empty`), and nests option groups as `sealed` nested classes (see `AuthOptions.TokenCacheOptions.RedisTokenCacheOptions`). Follow this exactly for new options. Don't introduce `IOptionsSnapshot`/`IOptionsMonitor` unless there's a specific reload requirement — everything today is plain `IOptions<T>`.
- **Analyzers.** `Directory.Build.props` sets `Nullable`/`ImplicitUsings` enabled, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, with CA1305/CA1707/CA1848 (culture-info, underscore naming, LoggerMessage-delegate) suppressed repo-wide. Any *other* new analyzer warning in code you touch is a build break, not a suggestion.
- **Central Package Management.** Add new package dependencies in `Directory.Packages.props`, not inline `Version=` attributes in `.csproj` files. Transitive pinning is enabled — a bumped transitive dependency may need its own explicit `PackageVersion` entry.
- **xunit.v3 is pinned to 3.2.2** (below 4.0.0). From the props comment: v4.0.0 (released 2026-08-14) obsoletes the `FrontControllerRunSettings` constructor that NCrunch's xunit.v3 adapter still invokes via reflection, breaking every test under NCrunch. **Do not bump past 3.2.2** without confirming NCrunch ships compatible support.
- **GitVersion.MsBuild** drives package versioning at build time (private asset, not a consumer dependency) — don't hand-edit version numbers. It's disabled under NCrunch specifically (`Directory.Build.props`: `Condition="'$(NCrunch)' == '1'"` → `DisableGitVersionTask=true`).

## Safe extension points

- **New `IServerTokenCache` implementation** — implement it in `Caching/`, register it alongside the existing Redis/in-process conditional in `AddBlazorTokenForwarding`. Preserve the single-flight `WithRefreshLockAsync` contract, and if the store is externally-backed, preserve the "malformed payload is a cache miss, not an exception" contract — mirror `RedisServerTokenCacheTests` for the shape of tests to write.
- **New token source** (e.g. a different M2M grant type) — mirror `IApiClientCredentialsTokenProvider`/`ApiClientCredentialsTokenProvider`: an options class with the `IsConfigured` computed-property pattern, singleton registration, a named `HttpClient`. Wire the fallback into `ApiAuthHandler.ResolveAccessTokenAsync`, and give the new source its own case in the private `TokenSource` enum (`None` / `UserOidc` / `ClientCredentials`) — that enum is what gates the "only a user-OIDC 401 marks the session expired" contract, so don't silently reuse an existing value for a new source.
- **New opt-in diagnostic/UI extra** — its own namespace under `Diagnostics/` or `Components/...`, never auto-registered from `AddBlazorTokenForwarding`. Static extension method, explicit call required by the host app, gated by the *caller's* `IsDevelopment()` check (see `MapAuthDebugEndpoints()`), not the library's.
- **New config option** — add it to the relevant `Options` class with a sane non-null default. If it's consumer-visible, add a row to the README configuration table in the same change. Don't let the README and the `Options` classes drift — that drift is exactly the gap this repo's docs were rewritten to close.

## Testing

- Runner: `Microsoft.Testing.Platform` (set in `global.json`), invoked via `dotnet test` as normal — output format differs slightly from the legacy VSTest runner if you're debugging CI output.
- Stack: xunit.v3, Shouldly for assertions (prefer fluent `.ShouldBe(...)` style to match existing tests over xunit's native `Assert.*`), NSubstitute for mocking, `Microsoft.AspNetCore.Mvc.Testing` for endpoint-level tests.
- Tests mirror `src/` 1:1 by filename — add a matching test file for a new source file rather than folding new tests into an existing one.
- `Infrastructure/` shared test doubles — reuse these rather than re-inventing:
  - `FakeAuthenticationContext.CreateUnauthenticated()` / `CreateAuthenticated(subject, tokens?)` — builds a `DefaultHttpContext` wired to a substituted `IAuthenticationService`, so `HttpContext.AuthenticateAsync`/`GetTokenAsync` behave like a real cookie handler without spinning up a `TestServer`.
  - `RefreshServiceFactory.Create(responder, tokenEndpoint?)` — builds a real `OidcTokenRefreshService` against a stubbed HTTP boundary.
  - `StubHttpMessageHandler(responder)` — fake `HttpMessageHandler`; exposes `LastRequest` (method/URI/headers/body) and `CallCount` for asserting on outgoing calls.
  - `TestAuthHandler` — header-driven auth handler (`X-Test-Authenticated: true`, `X-Test-Token-{name}`) for `TestServer`-backed integration tests.
  - `TestServerFactory.Create(configureServices?, configureApp)` — spins up an in-memory `TestServer` for exercising middleware/endpoints for real.
- NCrunch: `.ncrunchsolution`/`.ncrunchproject` are checked into this branch. See the xunit.v3 pin above for why it's load-bearing — don't bump that package without checking NCrunch compatibility first.

## Gotchas

- **`UseBlazorTokenCache()` ordering is load-bearing.** It must run between `UseAuthorization()` and `UseAntiforgery()` — that's the window where response headers are still writable, needed to persist a refreshed cookie. If you touch `BlazorTokenCacheMiddleware`, don't assume the ordering requirement is arbitrary.
- **`ApiAuthHandler` has two structurally different code paths** gated on `httpContextAccessor.HttpContext is not null` (normal request vs. SignalR circuit). A fix that only touches one path usually needs a mirrored fix in the other — check `ApiAuthHandlerTests` for paired tests (request-mode and circuit-mode variants of the same behavior) before assuming single-path coverage is enough.
- **Circuit-path token logic lives only in `CachedUserTokenResolver`.** Both `ApiAuthHandler` and `UserAccessTokenProvider` call it; a fix to cache-then-refresh, skew or lock behavior goes there, not in either caller. It is a singleton, so it must not take scoped or typed-`HttpClient` dependencies (`OidcTokenRefreshService` and the expiry publisher are passed per call). The two public `ApiAuthHandler` constructors build their own instance with default `AuthOptions` (refresh skew 0, the v0.1.x behaviour); the internal constructor (used by the DI registration) takes the shared one so the configured `RefreshSkewSeconds` applies.
- **Transient refresh failures are filtered by `TransientRefreshFailure.Is`, on both paths.** The circuit path (`CachedUserTokenResolver`) and the request path (`ServerRequestOidcTokenResolver`) keep a still-valid token and log a warning when a refresh fails that way; an expired token or caller cancellation still throws. Change the filter in that one place, not per path.
- **Known limitation, pre-existing:** on the request path the "raced" and "diverged" cache checks use `GetAsync`, which ignores the skew, so the request path does not refresh early while the cache holds an unexpired entry. Don't assume the skew applies there.
- **`IUserAccessTokenProvider` is the only new public type; keep it that way.** It is scoped, never resolve it from the root provider or capture it in a singleton, and it must never fall back to client credentials. It returns `null` for anonymous, lapsed, rejected-and-expired, or outside a circuit scope, and throws on transient failure with an expired token and on cancellation. `PublicSurface_ExposesOnlyTheProviderInterface` guards the accessibility of the implementation types.
- **The `TokenSource` enum in `ApiAuthHandler`** gates the "only user-OIDC 401 marks the session expired" contract — see Safe extension points above before adding a new source.
- **`AuthOptions` is read two different ways** in `AddBlazorTokenForwarding` (`IOptions<AuthOptions>` binding, plus one eager `Get<AuthOptions>()` call used only to decide Redis-vs-in-process cache registration). If you change `AuthOptions`'s shape, keep both call sites consistent.
- **`ApiOptions` is bound but not consumed internally.** This is documented as intentional (host-app convenience) in the README — don't "fix" it as a bug without raising it as its own decision first.
- **`the-button-enhancement-plan.md`** at the repo root is an unrelated, separate in-flight workstream (optional `IDataProtector` payload protection for `RedisServerTokenCache`). It describes a *planned* change, not current behavior — don't treat it as authoritative for how `RedisServerTokenCache` works today, and don't fold it into unrelated changes.
