namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Eagerly resolves (and, if needed, refreshes) the current request's OIDC token while headers
/// are still writable, populating <see cref="IServerTokenCache"/> so it's available later during
/// the Blazor Server interactive SignalR phase, where HttpContext is null. Register after
/// authentication/authorization and before antiforgery.
/// </summary>
public sealed class BlazorTokenCacheMiddleware(RequestDelegate next, ILogger<BlazorTokenCacheMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext httpContext, ServerRequestOidcTokenResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(resolver);

        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                await resolver.ResolveAsync(httpContext, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to resolve OIDC token for the server-side request token cache.");
            }
        }

        await next(httpContext).ConfigureAwait(false);
    }
}
