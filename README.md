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

For an interactive Blazor circuit, create the named client from the scoped
`IBlazorCircuitHttpClientFactory` rather than directly from `IHttpClientFactory`. It preserves the
named pipeline and supplies the circuit's server-side token-cache key to `ApiAuthHandler`; it never
places an access token or cache key in a request header or browser-visible state. Ordinary HTTP
request-only clients may continue using `IHttpClientFactory`.

What each call does:
- **`AddBlazorTokenForwarding(configuration)`** — registers the token cache (in-process or Redis-backed, see below), the refresh/resolver services, the client-credentials (M2M) fallback provider, and `ApiAuthHandler`.
- **`UseBlazorTokenCache()`** — middleware that eagerly resolves (and refreshes) the current request's token while response headers are still writable, so a refreshed cookie can actually be persisted. See [How it works](#how-it-works) for why the pipeline position matters.
- **`.AddHttpMessageHandler<ApiAuthHandler>()`** — attach to any typed `HttpClient` you want the bearer token forwarded to.
- **`IUserAccessTokenProvider`** — inject it (scoped) wherever you need the signed-in user's current access token as a string, for example a SignalR `HubConnection`. It is registered by `AddBlazorTokenForwarding` (no extra call); see [Using the token outside `HttpClient`](#using-the-token-outside-httpclient).

If your application already keeps its OIDC options under a different configuration section, pass
that section name as the optional third argument: `services.AddBlazorTokenForwarding(configuration,
"MyApplication:Oidc")`. The selected section supplies `AuthOptions`; `Api` and
`Api:ClientCredentials` continue to use their documented sections.

## How it works

Tokens are resolved differently depending on where the outgoing call happens:

- **Normal HTTP request** — `HttpContext` is available, so the resolver reads the token straight from the authentication cookie (refreshing and re-signing the cookie in place if it's near expiry).
- **SignalR circuit** (interactive Blazor Server, after the initial page load) — there's no `HttpContext`. The handler falls back to the server-side `IServerTokenCache`, refreshing in place using the cached refresh token if needed.

An interactive circuit must obtain that client through `IBlazorCircuitHttpClientFactory`. The scoped
factory wraps the existing named `IHttpClientFactory` pipeline and puts only the caller's exact token
cache key in an internal `HttpRequestMessage.Options` entry. This keeps separately rendered circuits
isolated while retaining handler pooling and named-client configuration.

`UseBlazorTokenCache()` must run **after `UseAuthorization()` and before `UseAntiforgery()`**: this is the last point in the pipeline where response headers are still writable, which is what lets a refreshed cookie actually get persisted back to the browser. Moving it later means a mid-request refresh has nowhere to write the new cookie.

If the current circuit/request is anonymous and `Api:ClientCredentials` is configured (see below), `ApiAuthHandler` falls back to a client-credentials (M2M) token instead of sending the request unauthenticated.

`ApiAuthHandler` and `IUserAccessTokenProvider` share one cache-then-refresh implementation for the circuit case, so a token refreshed by one is immediately visible to the other, and concurrent callers still make a single token-endpoint call. The provider has **no** client-credentials fallback: it only ever returns the signed-in user's token.

## Per-sign-in token isolation

Since 0.3.0, cached tokens belong to one sign-in session, not to the user. Two browsers signed in as the same user have separate cache entries, refresh locks and session-expiry signals, and one never receives the other's tokens. There is nothing to configure: `AddBlazorTokenForwarding` adds this to your cookie authentication.

- **The session id.** When a cookie scheme signs in, the principal gets a random 256-bit id in a `urn:syntaxcircus:blazor-auth:session-id` claim. It is a claim because an interactive circuit has only the `ClaimsPrincipal`. Only a claim with the issuer `SyntaxCircus.Blazor.Auth` counts, so a claim of that type from your identity provider is replaced (or, for a session that stays without an id, removed) at sign-in and cannot choose the id.
- **Cache key.** With an id: `user-session:{id}:{subject}`. Without one: `user:{subject}`, as before. A session with an id never reads the per-user entry or another session's entry, even when its own is empty. The id appears wherever cache keys are logged; it is not a credential.
- **What keeps the id:** token refreshes, sliding-expiration renewal, signing the current principal in again, replacing the principal in `OnValidatePrincipal` or a `ValidatePrincipal` override (as ASP.NET Core Identity's security-stamp check does), and re-signing a rebuilt principal as described below.
- **What starts a new session:** every new sign-in with a new principal and new `AuthenticationProperties`, such as each OpenID Connect sign-in. That includes a challenge you start with an old cookie's properties (a step-up, say): they pass through the OpenID Connect `state`, and the old id is not taken from them.
- **When you re-sign a principal you built yourself** (a profile update, say), pass the cookie's own properties: those `AuthenticateAsync` returned **in the same request**, to the same scheme, for the same subject. The cookie then keeps what it had, so circuits already open stay with it: its id, or, for a cookie issued before the upgrade, no id and the per-user key.

  ```csharp
  var current = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
  await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, rebuiltPrincipal, current.Properties);
  ```

  With new or cloned properties (`Clone()`, or a copy of `Items`), the cookie gets a new id unless the principal still carries the existing `Claim` object (`principal.Clone()` does; a claim rebuilt from its type and value loses the issuer and does not count). A circuit already open then keeps the old id while the cookie has the new one. Both refresh from the same refresh token, so a provider that rotates refresh tokens rejects the second use and the session is lost: the user has to sign in again.
- **A new sign-in must not reuse the old session's objects.** If your own code signs a user in again (a new password login, say), use new `AuthenticationProperties`: signing the same subject in with the current cookie's own properties continues that session, so copies of the old cookie would receive the new tokens (and a cookie from before the upgrade would stay on the per-user key). Likewise don't copy every claim of an already signed-in principal into a new sign-in, which carries the old id across.

### Cookie configuration

It works whether `AddCookie` is called before or after `AddBlazorTokenForwarding`, with your own `OnSigningIn` and `OnValidatePrincipal` handlers (they run first), with an assigned `options.Events` instance or events subclass, with `EventsType`, with a server-side `SessionStore`, and with any cookie scheme name. The request path still reads tokens only from the default `Cookies` scheme.

In two cases the package forwards your events through its own instance, so that it still runs:

| Your configuration | What changes |
|---|---|
| An events subclass that overrides `SigningIn` or `ValidatePrincipal` | `options.Events` becomes a plain `CookieAuthenticationEvents` that calls every method on your instance. Code that casts `options.Events` back to your type no longer finds it. |
| `options.EventsType` | `options.EventsType` becomes a package type that resolves your type from request services and calls it. |

It cannot protect a session in these cases:

| Cause | What happens | Warning logged |
|---|---|---|
| A `PostConfigure<CookieAuthenticationOptions>` registered **after** `AddBlazorTokenForwarding` that replaces `Events`, `EventsType` or `OnSigningIn` without calling the previous handler | New sign-ins get no id, so the user's sessions share the per-user entry | When the package next re-signs a cookie after a token refresh ("sign-in hook did not run") |
| The same, replacing only `OnValidatePrincipal` | A principal replaced during validation loses the id and falls back to the per-user entry. A rebuilt principal re-signed with the cookie's own properties gets a new id (see above) | Only for the lost id ("is missing the session id"); **none** for the re-sign |
| An `IClaimsTransformation` (or middleware) that rebuilds `HttpContext.User` without the claim | The affected requests use the per-user entry | Once per application instance ("is missing the session id") |

There is **no** warning if your own `AuthenticationStateProvider` gives circuits a rebuilt principal without the claim (or with the claim's issuer lost): the circuit then looks under the per-user key.

**Custom `IUserTokenCacheKeyProvider`.** It still replaces the default (register it after `AddBlazorTokenForwarding`), but isolation is then up to your implementation: a key derived from the subject alone shares one entry between all of a user's sessions, with no warning. To keep isolation, build on `UserTokenCacheKeyProvider.GetCacheKey(principal)`.

**Within one session**, a copy of the cookie is that session: it can be handed the session's current, refreshed tokens. Signing out does not remove the session's cache entry; call `IServerTokenCache.RemoveAsync(keys.GetCacheKey(context.User)!)` in your sign-out endpoint if you want it gone at once.

### Upgrading from 0.2.x

- **Cookies issued before the upgrade keep working, and keep the per-user key**, including through token refreshes and your own re-sign with the cookie's own properties (moving only the cookie to a new key would strand circuits already open on the old one, and replay rotated refresh tokens). So pre-upgrade sessions still share `user:{subject}` **with each other** until they sign in again. They never receive tokens from a sign-in made after the upgrade. If you need all sharing to stop at once, make everyone sign in again, for example by renaming the auth cookie.
- **Your own `SignInAsync` of a pre-upgrade cookie keeps it that way** when it continues the cookie's session: the same principal or a rebuilt one, with the properties `AuthenticateAsync` returned in the same request, to the same scheme, for the same subject. With new or cloned properties, or for another scheme or subject, it is a new session and gets an id, and nothing is logged. If it replaces a pre-upgrade cookie whose circuit is still in use (in another tab, say), that circuit keeps the per-user key while the cookie moves on. If both then refresh from the same rotating refresh token, the second use is rejected (some providers then revoke the whole grant) and the user has to sign in again. So to re-sign an existing session, pass its own properties.
- **`GetCacheKey(subject)` no longer returns the key the package uses** for a signed-in session. It returns the per-user key. Use `GetCacheKey(principal)` for `SessionStateService.Observe`, `SessionExpiryBroker.Subscribe` and direct `IServerTokenCache` calls. Calls made with a bare subject or a hand-built `user:{subject}` key compile but silently find nothing for new sessions.
- Code that acted on all of a user's sessions through that one entry (reading the user's token in a background job, evicting it to force a refresh or sign-out) no longer reaches new sessions, and there is no API that lists a user's sessions.
- The request path now calls `GetCacheKey(principal)` instead of `GetCacheKey(subject)`. This matters only to a custom provider whose two overloads disagree.
- Session expiry is now signalled to the affected session only, not to every circuit of the user.
- There is now one cache entry per sign-in, each kept until 30 days after its access token expires, in memory or in Redis. Size the cache for your sign-in volume.
- The auth cookie (or server-side ticket) grows by a few hundred bytes.
- With several instances sharing Redis, upgrade them together. An instance still on 0.2.x uses the per-user key for every cookie.
- If you serialize all claims to a WebAssembly client, the session id claim is included.

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

- `GetAccessTokenAsync` returns `null` when the user is anonymous, the session has lapsed, or the identity provider rejected the refresh and the current token has expired. It is **never** a client-credentials token, so treat `null` as "not signed in" rather than sending the request anonymously. When the refresh is rejected and the token has expired, the session is also marked expired (see below), exactly as `ApiAuthHandler` does.
- It **throws** if a refresh fails transiently (network error, timeout, malformed IdP response, discovery failure) and the token has already expired, and when the caller cancels. In a SignalR `AccessTokenProvider`, an exception makes the connection start or reconnect fail, so catch it if you want to handle that yourself. If the token is still valid when a transient failure happens, it is returned and a warning is logged.
- It is scoped. Resolve it from the circuit scope and never capture it in a singleton. Outside a circuit scope, where no authentication state exists, it returns `null` instead of throwing.
- SignalR calls the delegate on every (re)connect, so a long-lived connection picks up a refreshed token each time it reconnects.

## Session-expiry UX

`SessionStateService` is a scoped service exposing `IsSessionExpired` and an `OnSessionChanged` event. Inject it into a layout or component and subscribe to `OnSessionChanged` to show a "please sign in again" prompt when the current user's session can no longer be refreshed. It's set automatically by `ApiAuthHandler` — see the 401-handling note below for exactly when. For a circuit to be notified, call `Observe` with the key from `IUserTokenCacheKeyProvider.GetCacheKey(principal)` (the principal overload, see [Per-sign-in token isolation](#per-sign-in-token-isolation)).

## Configuration

| Section | Key | Default | Notes |
|---|---|---|---|
| `Authentication:Oidc` | `Authority` | `""` | required |
| `Authentication:Oidc` | `ClientId` | `""` | required |
| `Authentication:Oidc` | `ClientSecret` | `""` | required |
| `Authentication:Oidc` | `Scopes` | `["openid","profile","email","offline_access"]` | keep `offline_access` or refresh tokens won't be issued |
| `Authentication:Oidc:TokenCache` | `RefreshSkewSeconds` | `60` | how early (before actual expiry) a token is treated as due for refresh, on both the HTTP-request and circuit paths |
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
- **The in-process cache has the same retention.** An entry is removed after 30 days past its access token's expiry (or 30 days after it was written, if that is later), by a sweep that runs during a write at most once an hour. An entry whose refresh lock is in use is not removed. In both caches a refresh lock is released from memory once nobody holds or waits for it.
- **Refresh is single-flight.** Concurrent callers for the same cache key don't trigger duplicate refresh/token-endpoint calls — this applies to both cache implementations and to the client-credentials provider. The lock is per process, so separate instances sharing a Redis-backed cache can still refresh at the same time.
- **401 handling is asymmetric.** A 401 response evicts the cache and marks the session expired (via `SessionStateService`) only when the token came from user OIDC. A 401 on a client-credentials (M2M) token does **not** mark the session expired.
- **A long-lived Blazor Server circuit re-checks its token instead of reusing the first resolution forever.** `ServerRequestOidcTokenResolver` caches its resolution on `HttpContext.Items` (valid for as long as that resolution's own token is fresh) so a normal HTTP request only resolves once — but for an interactive circuit, `HttpContext` (and therefore `Items`) is ambient for the whole SignalR connection, not one interaction. Once the cached resolution's validity window elapses, the next call re-resolves (and refreshes) rather than replaying the same, now-stale, resolution for the rest of the circuit's lifetime.
- **The circuit path honors `RefreshSkewSeconds`.** Before this was fixed, a token cached for a SignalR circuit was forwarded until its exact expiry, so a call made just before expiry could reach the API with a token that expired in flight. Now a cached token within `RefreshSkewSeconds` of expiry is refreshed first, the same as on the HTTP-request path. Keep `RefreshSkewSeconds` comfortably below your access-token lifetime, or every call refreshes. This applies to `ApiAuthHandler` and `IUserAccessTokenProvider` alike when registered through DI. An `ApiAuthHandler` you construct directly (without DI) uses a skew of 0, the v0.1.x behaviour.
- **Transient refresh failures keep a still-valid token.** If a refresh fails with a network error, a timeout, a malformed IdP response or a discovery failure while the current token has not yet expired, that token is forwarded and a warning is logged. This applies on both the circuit path and the HTTP-request path. An expired token, or cancellation by the caller, still throws. A refresh the IdP actively rejects (any non-success status, including a 5xx, yields no new token) is not transient: once the token has expired, the session is marked expired. Validity is re-checked after the refresh returns, so a slow IdP failure that outlives the token throws (or expires the session) rather than forwarding the expired token.
- **Custom `IServerTokenCache` implementations.** The circuit path now treats an entry that is expired or inside the skew window as needing a refresh, so a custom cache whose `GetAsync` returns expired entries no longer has them forwarded as-is. `IUserAccessTokenProvider` holds one `OidcTokenRefreshService` for the circuit's lifetime, the same as the existing resolver.
- **While the IdP is down, each call inside the skew window retries the refresh.** Retries are one at a time under the refresh lock, until the token actually expires. This is accepted: the cost is one failing token-endpoint call per request in that window. Each call waits for the failing refresh (up to the `HttpClient` timeout, 100 seconds by default) and calls for the same cache key (sign-in session) queue behind the lock.
- **Known limitation (HTTP-request path).** The "raced" and "diverged" cache checks in `ServerRequestOidcTokenResolver` use `GetAsync`, which ignores the skew. So while the token cache holds an unexpired entry, the request path does not refresh early; it refreshes only when that token actually expires. The circuit path is not affected.
- **The refresh token is re-read after the refresh lock is acquired.** A caller waiting for the lock uses the refresh token as of the moment it gets the lock, so it never replays a refresh token another caller has just rotated.
- **A circuit-path refresh's rotated refresh token is honored on the next HTTP request, even with a stale cookie.** If a SignalR-circuit-path refresh (no `HttpContext`) rotates the refresh token in the server-side cache, the next full HTTP request detects that the cache's refresh token has diverged from the (stale) cookie's and prefers the cache — reusing its access token directly if still valid, or refreshing with the cache's rotated refresh token instead of retrying the cookie's already-invalidated one — and re-signs the cookie to catch it up.
- **Token expiry resolution fallback chain:** explicit expiry value → `expires_in` from the token response → the JWT `exp` claim → `FallbackAccessTokenLifetimeSeconds`.
- **Client-credentials failures degrade gracefully.** If the M2M token provider throws, the request goes out unauthenticated (with a logged warning) rather than failing outright.

## Extras

- `SyntaxCircus.Blazor.Auth.Diagnostics.AuthDebugEndpoints.MapAuthDebugEndpoints()` — opt-in `/debug/claims` and `/debug/token` endpoints for inspecting the current principal and cached tokens. Never wired automatically; call it yourself, gated behind `IsDevelopment()`. Raw token values are redacted by default (only a preview is returned) — pass `?includeRaw=true` to see them. Don't expose this outside development.

## Contributing

Issues and pull requests are welcome:
- Keep changes focused, with a clear description of the behavior change.
- Match the existing code style (see `.editorconfig`).
- Call out any breaking changes to the public API in your PR description.

See [`AGENTS.md`](AGENTS.md) for repo structure, conventions, and safe extension points — useful whether you're a human or an AI coding agent.

## License

MIT — see [LICENSE.txt](LICENSE.txt).
