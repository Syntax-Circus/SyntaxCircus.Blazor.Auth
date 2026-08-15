using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>Caches a client-credentials OAuth access token in-process. Safe to share as a singleton.</summary>
public sealed class ApiClientCredentialsTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ApiClientCredentialsOptions> optionsAccessor) : IApiClientCredentialsTokenProvider, IDisposable
{
    public const string HttpClientName = "SyntaxCircus.Blazor.Auth.ClientCredentials";

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public bool IsConfigured => optionsAccessor.CurrentValue.IsConfigured;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _accessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _accessToken;
            }

            var options = optionsAccessor.CurrentValue;
            if (!options.IsConfigured)
            {
                throw new InvalidOperationException(
                    $"{ApiClientCredentialsOptions.SectionName} is not fully configured (TokenEndpoint, ClientId, ClientSecret are required).");
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
            };

            if (!string.IsNullOrWhiteSpace(options.Audience))
            {
                form["audience"] = options.Audience;
            }

            if (!string.IsNullOrWhiteSpace(options.Scope))
            {
                form["scope"] = options.Scope;
            }

            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(
                options.TokenEndpoint,
                new FormUrlEncodedContent(form),
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var token = await response.Content
                .ReadFromJsonAsync<ClientCredentialsTokenResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token?.AccessToken))
            {
                throw new InvalidOperationException("The client-credentials token response did not include an access token.");
            }

            _accessToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(token.ExpiresIn - 60, 60));
            return _accessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose() => _refreshLock.Dispose();

    private sealed class ClientCredentialsTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; } = 300;
    }
}
