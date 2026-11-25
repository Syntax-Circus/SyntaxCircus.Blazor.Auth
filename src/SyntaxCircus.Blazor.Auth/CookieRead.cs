using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SyntaxCircus.Blazor.Auth;

/// <summary>
/// What the current request found in an auth cookie it read. It is how a sign-in that continues that
/// cookie's session (the host re-signing a principal it rebuilt, with the cookie's own
/// <c>AuthenticationProperties</c>) is told apart from a new sign-in.
/// </summary>
/// <remarks>
/// The id recorded in <c>AuthenticationProperties.Items</c> cannot decide this: items travel in the
/// OpenID Connect <c>state</c>, so a new identity-provider sign-in started with an old cookie's
/// properties arrives carrying the old record. Nothing here leaves the request. Only a sign-in using
/// the very <c>Items</c> dictionary the cookie handler read in this request matches, and a dictionary
/// restored from <c>state</c>, or copied, is a different one.
/// </remarks>
internal sealed record CookieRead(string Scheme, IDictionary<string, string?> Items, string? Subject, string? SessionId)
{
    private static readonly object ItemKey = new();

    public static void Remember(CookieValidatePrincipalContext context, string? sessionId)
    {
        if (context.HttpContext.Items.TryGetValue(ItemKey, out var value) && value is List<CookieRead> reads)
        {
            reads.Add(Of(context, sessionId));
        }
        else
        {
            context.HttpContext.Items[ItemKey] = new List<CookieRead> { Of(context, sessionId) };
        }
    }

    /// <summary>
    /// The read whose session <paramref name="context"/>'s sign-in continues, or <see langword="null"/>
    /// when it is a new session. Besides the properties, the scheme and the subject must both be the
    /// ones read: a session never moves to another user. A read without a <see cref="SessionId"/> is a
    /// cookie issued before the upgrade, and the sign-in continuing it gets none either.
    /// </summary>
    public static CookieRead? ContinuedBy(CookieSigningInContext context)
    {
        if (!context.HttpContext.Items.TryGetValue(ItemKey, out var value) || value is not List<CookieRead> reads)
        {
            return null;
        }

        var subject = SubjectOf(context.Principal);
        return subject is null
            ? null
            : reads.LastOrDefault(read => ReferenceEquals(read.Items, context.Properties.Items)
                && string.Equals(read.Scheme, context.Scheme.Name, StringComparison.Ordinal)
                && string.Equals(read.Subject, subject, StringComparison.Ordinal));
    }

    private static CookieRead Of(CookieValidatePrincipalContext context, string? sessionId)
        => new(context.Scheme.Name, context.Properties.Items, SubjectOf(context.Principal), sessionId);

    private static string? SubjectOf(ClaimsPrincipal? principal)
        => principal is null ? null : UserTokenCacheKeyProvider.ResolveSubject(principal);
}
