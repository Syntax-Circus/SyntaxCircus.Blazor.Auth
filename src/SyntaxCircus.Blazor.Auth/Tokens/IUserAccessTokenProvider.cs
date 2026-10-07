namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// Gives code that cannot go through an <see cref="HttpClient"/> pipeline (notably a SignalR
/// <c>HubConnection</c>) the signed-in user's current OIDC access token, refreshed when it is
/// missing, expired or inside the configured refresh-skew window. It shares its cache-then-refresh
/// logic with <see cref="ApiAuthHandler"/>, so both see the same token and a single refresh.
/// </summary>
/// <remarks>
/// <para>
/// <b>Null result.</b> <see langword="null"/> means the user is anonymous, the session has lapsed
/// (no usable token and nothing to refresh from), or the identity provider rejected the refresh. It is never a
/// client-credentials (machine-to-machine) token: this provider acts only as the signed-in user, so
/// callers must treat <see langword="null"/> as "not authenticated" and not as "send anonymously".
/// </para>
/// <para>
/// <b>Transient refresh failures.</b> If a refresh fails transiently (network error, timeout, a bad
/// identity-provider response or a discovery failure) while the current token is still valid, that
/// still-valid token is returned. If the token has already expired, or the caller cancels, the call
/// throws instead of returning <see langword="null"/>. A SignalR <c>AccessTokenProvider</c> that
/// throws makes the connection start or reconnect fail, so callers may want to catch.
/// </para>
/// <para>
/// <b>Scope.</b> Registered as a scoped service by <c>AddBlazorTokenForwarding</c>. Resolve it from
/// the Blazor circuit scope, for example by injecting it into a component or a scoped service, and
/// never capture it in a singleton. Outside a circuit scope, where no authentication state is
/// available, it returns <see langword="null"/> rather than throwing.
/// </para>
/// <para>
/// <b>SignalR usage.</b>
/// </para>
/// <code>
/// // In a component or scoped service: [Inject] IUserAccessTokenProvider TokenProvider
/// var connection = new HubConnectionBuilder()
///     .WithUrl(hubUrl, options =>
///         options.AccessTokenProvider = () => TokenProvider.GetAccessTokenAsync().AsTask())
///     .WithAutomaticReconnect()
///     .Build();
/// </code>
/// <para>
/// SignalR invokes the delegate on every (re)connect, so a long-lived connection picks up a
/// refreshed token each time it reconnects.
/// </para>
/// </remarks>
public interface IUserAccessTokenProvider
{
    /// <summary>Returns the current user's access token, or <see langword="null"/> (see remarks on the interface).</summary>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
