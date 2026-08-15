using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace SyntaxCircus.Blazor.Auth;

public sealed class OidcTokenRefreshService(
    HttpClient httpClient,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    ILogger<OidcTokenRefreshService> logger)
{
    public async Task<OidcTokenRefreshResult?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var options = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var tokenEndpoint = await ResolveTokenEndpointAsync(options, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            logger.LogWarning("OIDC token endpoint could not be resolved; cannot refresh access token.");
            return null;
        }

        var formValues = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = options.ClientId ?? string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            formValues["client_secret"] = options.ClientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formValues),
        };

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("OIDC refresh token grant failed with status {StatusCode}.", response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenPayload>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload?.AccessToken))
        {
            logger.LogWarning("OIDC refresh token grant did not return an access token.");
            return null;
        }

        var expiresAt = OidcTokenExpiry.Resolve(
            payload.ExpiresAt,
            payload.ExpiresIn?.ToString(CultureInfo.InvariantCulture),
            payload.AccessToken,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));

        return new OidcTokenRefreshResult(
            payload.AccessToken,
            string.IsNullOrWhiteSpace(payload.RefreshToken) ? refreshToken : payload.RefreshToken,
            payload.IdToken,
            expiresAt);
    }

    private static async Task<string?> ResolveTokenEndpointAsync(OpenIdConnectOptions options, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.Configuration?.TokenEndpoint))
        {
            return options.Configuration.TokenEndpoint;
        }

        if (options.ConfigurationManager is null)
        {
            return null;
        }

        var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return configuration.TokenEndpoint;
    }

    private sealed class TokenPayload
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("id_token")]
        public string? IdToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; init; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; init; }
    }
}
