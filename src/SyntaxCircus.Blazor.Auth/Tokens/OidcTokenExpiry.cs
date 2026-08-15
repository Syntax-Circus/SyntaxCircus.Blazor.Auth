using System.Globalization;
using System.Text.Json;

namespace SyntaxCircus.Blazor.Auth;

public static class OidcTokenExpiry
{
    public static bool TryParse(string? expiresAt, out DateTimeOffset expiry)
        => DateTimeOffset.TryParse(
            expiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out expiry);

    /// <summary>
    /// Resolves a token's expiry from, in order: an explicit <paramref name="expiresAt"/> value,
    /// an <paramref name="expiresIn"/> seconds-from-now value, the access token's own JWT
    /// <c>exp</c> claim (if it looks like a JWT), or finally <paramref name="fallbackLifetime"/>
    /// from now.
    /// </summary>
    public static DateTimeOffset Resolve(
        string? expiresAt,
        string? expiresIn,
        string? accessToken,
        DateTimeOffset nowUtc,
        TimeSpan fallbackLifetime)
    {
        if (TryParse(expiresAt, out var parsed))
        {
            return parsed;
        }

        if (!string.IsNullOrWhiteSpace(expiresIn)
            && long.TryParse(expiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            return nowUtc.AddSeconds(seconds);
        }

        if (TryReadJwtExpiryClaim(accessToken, out var jwtExpiry))
        {
            return jwtExpiry;
        }

        return nowUtc.Add(fallbackLifetime > TimeSpan.Zero ? fallbackLifetime : TimeSpan.FromMinutes(5));
    }

    private static bool TryReadJwtExpiryClaim(string? token, out DateTimeOffset expiry)
    {
        expiry = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.TryGetProperty("exp", out var value) && value.TryGetInt64(out var unixSeconds))
            {
                expiry = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }

        return false;
    }
}
