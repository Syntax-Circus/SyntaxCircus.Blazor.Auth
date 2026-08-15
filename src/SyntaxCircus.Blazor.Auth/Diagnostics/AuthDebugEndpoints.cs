using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace SyntaxCircus.Blazor.Auth.Diagnostics;

/// <summary>
/// Optional, dev-only diagnostic endpoints for inspecting the current principal's claims and
/// cached OIDC tokens. Never wired automatically — call <see cref="MapAuthDebugEndpoints"/>
/// explicitly, and only when <c>IHostEnvironment.IsDevelopment()</c> is true.
/// </summary>
public static class AuthDebugEndpoints
{
    /// <summary>
    /// Maps <c>{basePath}/claims</c> (the current principal's claims) and <c>{basePath}/token</c>
    /// (a redacted summary plus decoded JWT header/payload of the cached tokens). Both require an
    /// authenticated user.
    /// </summary>
    public static IEndpointRouteBuilder MapAuthDebugEndpoints(this IEndpointRouteBuilder endpoints, string basePath = "/debug")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(basePath).RequireAuthorization();

        group.MapGet("/claims", (HttpContext context) =>
        {
            var user = context.User;
            return Results.Ok(new
            {
                authenticated = user.Identity?.IsAuthenticated ?? false,
                name = user.Identity?.Name,
                authenticationType = user.Identity?.AuthenticationType,
                claims = user.Claims.Select(claim => new { type = claim.Type, value = claim.Value }).ToArray(),
            });
        });

        group.MapGet("/token", async (HttpContext context, bool includeRaw) =>
        {
            var accessToken = await context.GetTokenAsync("access_token").ConfigureAwait(false);
            var idToken = await context.GetTokenAsync("id_token").ConfigureAwait(false);
            var refreshToken = await context.GetTokenAsync("refresh_token").ConfigureAwait(false);
            var expiresAt = await context.GetTokenAsync("expires_at").ConfigureAwait(false);

            return Results.Ok(new
            {
                hasAccessToken = !string.IsNullOrEmpty(accessToken),
                accessToken = DescribeToken(accessToken, includeRaw),
                hasIdToken = !string.IsNullOrEmpty(idToken),
                idToken = DescribeToken(idToken, includeRaw),
                hasRefreshToken = !string.IsNullOrEmpty(refreshToken),
                refreshToken = includeRaw ? refreshToken : Preview(refreshToken),
                expiresAt,
            });
        });

        return endpoints;
    }

    private static string? Preview(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return token.Length <= 20
            ? $"{token[..Math.Min(6, token.Length)]}..."
            : $"{token[..10]}...{token[^6..]}";
    }

    private static object? DescribeToken(string? token, bool includeRaw)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var header = TryDecodeJwtPart(token, 0);
        var payload = TryDecodeJwtPart(token, 1);

        return new
        {
            preview = Preview(token),
            raw = includeRaw ? token : null,
            issuer = GetString(payload, "iss"),
            audiences = GetAudiences(payload),
            subject = GetString(payload, "sub"),
            authorizedParty = GetString(payload, "azp"),
            clientId = GetString(payload, "client_id"),
            scope = GetString(payload, "scope"),
            expiresAtUnix = GetLong(payload, "exp"),
            notBeforeUnix = GetLong(payload, "nbf"),
            issuedAtUnix = GetLong(payload, "iat"),
            header,
            payload,
        };
    }

    private static JsonElement? TryDecodeJwtPart(string token, int partIndex)
    {
        var parts = token.Split('.');
        if (parts.Length <= partIndex || string.IsNullOrWhiteSpace(parts[partIndex]))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(DecodeBase64Url(parts[partIndex]));
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        return Convert.FromBase64String(base64);
    }

    private static string? GetString(JsonElement? payload, string propertyName)
    {
        if (payload is not { } element || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => value.GetRawText(),
        };
    }

    private static long? GetLong(JsonElement? payload, string propertyName)
    {
        if (payload is not { } element || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static IReadOnlyList<string> GetAudiences(JsonElement? payload)
    {
        if (payload is not { } element || !element.TryGetProperty("aud", out var audience))
        {
            return [];
        }

        return audience.ValueKind switch
        {
            JsonValueKind.String => [audience.GetString() ?? string.Empty],
            JsonValueKind.Array => [.. audience.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))],
            _ => [],
        };
    }
}
