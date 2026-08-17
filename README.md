# SyntaxCircus.Blazor.Auth

[![Build](https://github.com/Syntax-Circus/SyntaxCircus.Blazor.Auth/actions/workflows/build.yml/badge.svg)](https://github.com/Syntax-Circus/SyntaxCircus.Blazor.Auth/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/SyntaxCircus.Blazor.Auth.svg)](https://www.nuget.org/packages/SyntaxCircus.Blazor.Auth)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)

Server-side OIDC token forwarding, refresh, and resilience for interactive Blazor Server applications. The tokens issued at cookie/OIDC sign-in are cached, refreshed, and attached to outgoing calls to your backend API — on the initial HTTP request and later inside the SignalR circuit, where there's no `HttpContext` to read the auth cookie from.

> **No support guaranteed.** Published as-is and maintained on a best-effort basis. Issues and PRs are welcome, but there's no SLA — fork it or vendor what you need if that's not enough.

## Install

```
dotnet add package SyntaxCircus.Blazor.Auth
```

Targets `net10.0`. Brings in `Microsoft.AspNetCore.Authentication.OpenIdConnect` and `Microsoft.Extensions.Caching.StackExchangeRedis` as dependencies — Redis is only ever connected to if you turn it on via configuration (see [Configuration](#configuration)); it isn't a hard runtime requirement.

## Quick start

Configure normal cookie + OIDC authentication yourself first, with **`SaveTokens = true`** and the **`offline_access`** scope — this package only handles what happens to the tokens *after* sign-in, it doesn't set up authentication for you.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBlazorTokenForwarding(builder.Configuration);
builder.Services.AddHttpClient<IMyApiClient, MyApiClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"]!))
    .AddHttpMessageHandler<ApiAuthHandler>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseBlazorTokenCache(); // after UseAuthorization(), before antiforgery
app.UseAntiforgery();
```

What each call does:
- **`AddBlazorTokenForwarding(configuration)`** — registers the token cache (in-process or Redis-backed, see below), the refresh/resolver services, the client-credentials (M2M) fallback provider, and `ApiAuthHandler`.
- **`UseBlazorTokenCache()`** — middleware that eagerly resolves (and refreshes) the current request's token while response headers are still writable, so a refreshed cookie can actually be persisted. See [How it works](#how-it-works) for why the pipeline position matters.
- **`.AddHttpMessageHandler<ApiAuthHandler>()`** — attach to any typed `HttpClient` you want the bearer token forwarded to.

## How it works

Tokens are resolved differently depending on where the outgoing call happens:

- **Normal HTTP request** — `HttpContext` is available, so the resolver reads the token straight from the authentication cookie (refreshing and re-signing the cookie in place if it's near expiry).
- **SignalR circuit** (interactive Blazor Server, after the initial page load) — there's no `HttpContext`. The handler falls back to the server-side `IServerTokenCache`, refreshing in place using the cached refresh token if needed.

`UseBlazorTokenCache()` must run **after `UseAuthorization()` and before `UseAntiforgery()`**: this is the last point in the pipeline where response headers are still writable, which is what lets a refreshed cookie actually get persisted back to the browser. Moving it later means a mid-request refresh has nowhere to write the new cookie.

If the current circuit/request is anonymous and `Api:ClientCredentials` is configured (see below), `ApiAuthHandler` falls back to a client-credentials (M2M) token instead of sending the request unauthenticated.

## Session-expiry UX

`SessionStateService` is a scoped service exposing `IsSessionExpired` and an `OnSessionChanged` event. Inject it into a layout or component and subscribe to `OnSessionChanged` to show a "please sign in again" prompt when the current user's session can no longer be refreshed. It's set automatically by `ApiAuthHandler` — see the 401-handling note below for exactly when.

## Configuration

| Section | Key | Default | Notes |
|---|---|---|---|
| `Authentication:Oidc` | `Authority` | `""` | required |
| `Authentication:Oidc` | `ClientId` | `""` | required |
| `Authentication:Oidc` | `ClientSecret` | `""` | required |
| `Authentication:Oidc` | `Scopes` | `["openid","profile","email","offline_access"]` | keep `offline_access` or refresh tokens won't be issued |
| `Authentication:Oidc:TokenCache` | `RefreshSkewSeconds` | `60` | how early (before actual expiry) a token is treated as due for refresh |
| `Authentication:Oidc:TokenCache` | `FallbackAccessTokenLifetimeSeconds` | `300` | used only when no expiry can be resolved from the token response or JWT — see the expiry fallback chain below |
| `Authentication:Oidc:TokenCache:Redis` | `Enabled` | `false` | set `true` to back the token cache with Redis for multi-instance deployments; in-process otherwise |
| `Authentication:Oidc:TokenCache:Redis` | `ConnectionString` | `""` | required if `Enabled = true` |
| `Authentication:Oidc:TokenCache:Redis` | `InstanceName` | `"SyntaxCircus:OidcTokenCache:"` | Redis key prefix |
| `Authentication:Oidc:TokenCache:Redis:Protection` | `Enabled` | `false` | set `true` to encrypt cache payloads at rest with a Redis-backed `IDataProtectionProvider` — see callout below |
| `Authentication:Oidc:TokenCache:Redis:Protection` | `Purpose` | `"SyntaxCircus.Blazor.Auth.RedisServerTokenCache"` | data-protection purpose string |
| `Api` | `BaseUrl` | `""` | not read internally — see callout below |
| `Api` | `TimeoutSeconds` | `30` | not read internally — see callout below |
| `Api:ClientCredentials` | `TokenEndpoint` | `""` | optional; all three of `TokenEndpoint`/`ClientId`/`ClientSecret` must be set to activate the M2M fallback |
| `Api:ClientCredentials` | `ClientId` | `""` | |
| `Api:ClientCredentials` | `ClientSecret` | `""` | |
| `Api:ClientCredentials` | `Audience` | `""` | optional, included in the token request only if set |
| `Api:ClientCredentials` | `Scope` | `""` | optional, included in the token request only if set |

- `ApiOptions` (`Api:BaseUrl`, `Api:TimeoutSeconds`) is bound via `IOptions<ApiOptions>` for consistency with the other options classes, but nothing in this package currently reads it — the quick-start snippet above builds the typed client's `BaseAddress` directly from `IConfiguration`. If you want to consume `ApiOptions` yourself, you're responsible for reading it.
- `AuthOptions` is read twice at startup: once through the normal `IOptions<AuthOptions>` binding, and once eagerly (`configuration.GetSection(...).Get<AuthOptions>()`) purely to decide whether to register the Redis- or in-process-backed cache before the DI container is built.
- **Redis payload protection** (`Authentication:Oidc:TokenCache:Redis:Protection:Enabled`) is off by default — existing behavior is unchanged unless you turn it on. When enabled, cache payloads are encrypted with a **dedicated** `IDataProtectionProvider` whose key ring is persisted to the same Redis instance as the token cache (`{InstanceName}DataProtection-Keys`). This key ring is isolated from your application's own `AddDataProtection()` setup — it won't affect cookies, antiforgery, or anything else your app already protects — and works correctly across instances in a multi-instance deployment without any extra configuration on your part.

## Behavioral notes

- **Redis: corrupt payloads are a cache miss, not an error.** If a stored payload fails to deserialize — or fails to decrypt, when protection is enabled — the entry is evicted and treated as if it were never there.
- **Redis: TTL has a 30-day floor.** Even an already-expired entry gets at least a 30-day TTL when written, so refresh tokens aren't lost to premature Redis eviction.
- **Refresh is single-flight.** Concurrent callers for the same cache key don't trigger duplicate refresh/token-endpoint calls — this applies to both cache implementations and to the client-credentials provider.
- **401 handling is asymmetric.** A 401 response evicts the cache and marks the session expired (via `SessionStateService`) only when the token came from user OIDC. A 401 on a client-credentials (M2M) token does **not** mark the session expired.
- **A circuit-path refresh's rotated refresh token is honored on the next HTTP request, even with a stale cookie.** If a SignalR-circuit-path refresh (no `HttpContext`) rotates the refresh token in the server-side cache, the next full HTTP request detects that the cache's refresh token has diverged from the (stale) cookie's and prefers the cache — reusing its access token directly if still valid, or refreshing with the cache's rotated refresh token instead of retrying the cookie's already-invalidated one — and re-signs the cookie to catch it up.
- **Token expiry resolution fallback chain:** explicit expiry value → `expires_in` from the token response → the JWT `exp` claim → `FallbackAccessTokenLifetimeSeconds`.
- **Client-credentials failures degrade gracefully.** If the M2M token provider throws, the request goes out unauthenticated (with a logged warning) rather than failing outright.

## Extras

- `SyntaxCircus.Blazor.Auth.Diagnostics.AuthDebugEndpoints.MapAuthDebugEndpoints()` — opt-in `/debug/claims` and `/debug/token` endpoints for inspecting the current principal and cached tokens. Never wired automatically; call it yourself, gated behind `IsDevelopment()`. Raw token values are redacted by default (only a preview is returned) — pass `?includeRaw=true` to see them. Don't expose this outside development.
- `SyntaxCircus.Blazor.Auth.Components.Errors.GlobalErrorBoundary` / `GlobalErrorView` / `LoggingErrorBoundary` — a Bootstrap-styled error boundary that logs unhandled exceptions and auto-recovers on navigation.
- `SyntaxCircus.Blazor.Auth.Components.Layout.ReconnectModal` — the standard Blazor Server reconnect-UI markup (rejoin animation, pause/resume states).

## Contributing

Issues and pull requests are welcome:
- Keep changes focused, with a clear description of the behavior change.
- Match the existing code style (see `.editorconfig`).
- Call out any breaking changes to the public API in your PR description.

See [`AGENTS.md`](AGENTS.md) for repo structure, conventions, and safe extension points — useful whether you're a human or an AI coding agent.

## License

MIT — see [LICENSE.txt](LICENSE.txt).
