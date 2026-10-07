using System.Text.Json;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// The failures of a token refresh that are treated as transient: a network error, a timeout (not
/// caused by the caller's own cancellation, which callers check separately), a malformed IdP
/// response, or a discovery failure. Anything else is a bug and must propagate.
/// </summary>
internal static class TransientRefreshFailure
{
    // InvalidOperationException is included because OIDC discovery (ConfigurationManager) reports an
    // unreachable or unusable metadata endpoint that way (IDX20803). It also matches HttpClient's
    // invalid-request-URI error, so a misconfigured token endpoint is logged as a warning, rather
    // than thrown, while a token is still valid.
    public static bool Is(Exception ex)
        => ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException;
}
