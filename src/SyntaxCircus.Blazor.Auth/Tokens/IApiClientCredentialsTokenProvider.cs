namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Provides a machine-to-machine (OAuth2 client-credentials) access token for outbound API calls
/// made from an anonymous Blazor circuit. Opt-in — only consulted when
/// <see cref="IsConfigured"/> is true.
/// </summary>
public interface IApiClientCredentialsTokenProvider
{
    bool IsConfigured { get; }

    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
