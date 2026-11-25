using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// The per-sign-in session id. It travels as a claim because the Blazor circuit path has only the
/// <see cref="ClaimsPrincipal"/>. A claim counts as ours only when it carries <see cref="Issuer"/>:
/// claims built from an identity provider's tokens carry the provider's (or the scheme's) issuer, so
/// the provider cannot choose the id. The cookie ticket preserves the issuer.
/// </summary>
internal static class SessionId
{
    internal const string ClaimType = "urn:syntaxcircus:blazor-auth:session-id";
    internal const string Issuer = "SyntaxCircus.Blazor.Auth";

    /// <summary>The cookie's own record of its id, in the ticket's <c>AuthenticationProperties</c>.</summary>
    internal const string PropertiesKey = ".SyntaxCircus.Blazor.Auth.SessionId";

    private const int ByteLength = 32;
    private static readonly int TextLength = Base64Url.GetEncodedLength(ByteLength);

    internal static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength));

    internal static string? Read(ClaimsPrincipal? principal)
        => principal?.Claims.FirstOrDefault(IsOurs)?.Value;

    /// <summary>
    /// Returns <paramref name="principal"/> carrying <paramref name="sessionId"/> as its only session-id
    /// claim, or no such claim when it is <see langword="null"/>. A principal that needs changing is
    /// cloned first, so an instance the caller holds on to (and might sign in again for another
    /// session) is never mutated.
    /// </summary>
    internal static ClaimsPrincipal Apply(ClaimsPrincipal principal, string? sessionId)
    {
        var existing = principal.FindAll(ClaimType).ToList();
        var alreadyApplied = sessionId is null
            ? existing.Count == 0
            : existing is [var only] && IsOurs(only) && string.Equals(only.Value, sessionId, StringComparison.Ordinal);
        if (alreadyApplied)
        {
            return principal;
        }

        var copy = new ClaimsPrincipal(principal.Identities.Select(identity => identity.Clone()));
        foreach (var identity in copy.Identities)
        {
            foreach (var claim in identity.FindAll(ClaimType).ToList())
            {
                identity.TryRemoveClaim(claim);
            }
        }

        if (sessionId is not null)
        {
            var target = copy.Identities.FirstOrDefault(identity => identity.IsAuthenticated) ?? copy.Identities.FirstOrDefault();
            target?.AddClaim(new Claim(ClaimType, sessionId, ClaimValueTypes.String, Issuer));
        }

        return copy;
    }

    // The format check keeps the value safe to embed in a cache key: it cannot contain the separator.
    private static bool IsOurs(Claim claim)
        => string.Equals(claim.Type, ClaimType, StringComparison.Ordinal)
            && string.Equals(claim.Issuer, Issuer, StringComparison.Ordinal)
            && claim.Value.Length == TextLength
            && Base64Url.IsValid(claim.Value);
}
