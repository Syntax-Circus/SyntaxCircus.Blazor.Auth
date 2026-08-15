namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Options for the machine-to-machine OAuth client-credentials grant used to attach a bearer
/// token to outgoing API calls when the current Blazor circuit has no authenticated user.
/// </summary>
public sealed class ApiClientCredentialsOptions
{
    public const string SectionName = "Api:ClientCredentials";

    public string TokenEndpoint { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TokenEndpoint)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
