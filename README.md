# SyntaxCircus.Blazor.Auth

[![Build](https://github.com/Syntax-Circus/SyntaxCircus.Blazor.Auth/actions/workflows/build.yml/badge.svg)](https://github.com/Syntax-Circus/SyntaxCircus.Blazor.Auth/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/SyntaxCircus.Blazor.Auth.svg)](https://www.nuget.org/packages/SyntaxCircus.Blazor.Auth)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)

Server-side OIDC token forwarding, refresh, and resilience for interactive Blazor Server applications. The tokens issued at cookie/OIDC sign-in are cached, refreshed, and attached to outgoing calls to your backend API — on the initial HTTP request and later inside the SignalR circuit, where there's no `HttpContext` to read the auth cookie from.

> **No support guaranteed.** Published as-is and maintained on a best-effort basis. Issues and PRs are welcome, but there's no SLA — fork it or vendor what you need if that's not enough.

## Usage

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

Configure normal cookie + OIDC authentication yourself (with `SaveTokens = true` and the `offline_access` scope) — this package only handles what happens to the tokens *after* sign-in.

## Configuration

| Section | Purpose |
|---|---|
| `Authentication:Oidc` | `Authority`, `ClientId`, `ClientSecret`, `Scopes`, `TokenCache:RefreshSkewSeconds`, `TokenCache:FallbackAccessTokenLifetimeSeconds` |
| `Authentication:Oidc:TokenCache:Redis` | `Enabled`, `ConnectionString`, `InstanceName` — set `Enabled: true` to back the token cache with Redis for multi-instance deployments; in-process otherwise |
| `Api` | `BaseUrl`, `TimeoutSeconds` |
| `Api:ClientCredentials` | Optional. `TokenEndpoint`, `ClientId`, `ClientSecret`, `Audience`, `Scope` — when set, anonymous circuit calls fall back to a client-credentials (M2M) token instead of going out unauthenticated |

## Extras

- `SyntaxCircus.Blazor.Auth.Diagnostics.AuthDebugEndpoints.MapAuthDebugEndpoints()` — opt-in `/debug/claims` and `/debug/token` endpoints for inspecting the current principal and cached tokens. Never wired automatically; call it yourself, gated behind `IsDevelopment()`.
- `SyntaxCircus.Blazor.Auth.Components.Errors.GlobalErrorBoundary` / `GlobalErrorView` / `LoggingErrorBoundary` — a Bootstrap-styled error boundary that logs unhandled exceptions and auto-recovers on navigation.
- `SyntaxCircus.Blazor.Auth.Components.Layout.ReconnectModal` — the standard Blazor Server reconnect-UI markup (rejoin animation, pause/resume states).

## Contributing

Issues and pull requests are welcome:
- Keep changes focused, with a clear description of the behavior change.
- Match the existing code style (see `.editorconfig`).
- Call out any breaking changes to the public API in your PR description.

## License

MIT — see [LICENSE.txt](LICENSE.txt).
