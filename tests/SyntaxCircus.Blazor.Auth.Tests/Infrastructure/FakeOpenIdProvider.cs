using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SyntaxCircus.Blazor.Auth.Tests.Infrastructure;

/// <summary>
/// Just enough of an OpenID provider to drive the <b>real</b> <c>OpenIdConnectHandler</c> through a code
/// flow on a <see cref="CookieSessionHost"/>: challenge, <c>state</c> round trip, code redemption,
/// signed id_token validation and the handler's own cookie sign-in.
/// </summary>
internal sealed class FakeOpenIdProvider : IDisposable
{
    private const string Issuer = "https://auth.example.com";
    private const string ClientId = "client1";

    private readonly RSA rsa = RSA.Create(2048);

    /// <summary>The <c>AuthenticationProperties</c> items the handler had when it validated each id_token.</summary>
    public List<IReadOnlyDictionary<string, string?>> ValidatedProperties { get; } = [];

    /// <summary>Registers the OpenID Connect handler. <paramref name="onTokenValidated"/> stands for the host's own event.</summary>
    public void Register(IServiceCollection services, Func<Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext, Task>? onTokenValidated = null)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        services.AddAuthentication().AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = Issuer,
                AuthorizationEndpoint = $"{Issuer}/authorize",
                TokenEndpoint = $"{Issuer}/token",
            };
            configuration.SigningKeys.Add(key);

            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.ClientId = ClientId;
            options.Configuration = configuration;
            options.ResponseType = "code";
            options.SaveTokens = true;
            options.MapInboundClaims = false;
            options.BackchannelHttpHandler = new StubHttpMessageHandler(request => RedeemCode(request, key));
            options.Events.OnTokenValidated = async context =>
            {
                ValidatedProperties.Add(new Dictionary<string, string?>(context.Properties!.Items));
                if (onTokenValidated is not null)
                {
                    await onTokenValidated(context);
                }
            };
        });
    }

    /// <summary>
    /// Completes the sign-in the challenge at <paramref name="challengePath"/> starts, as
    /// <paramref name="subject"/>. The tokens issued are <c>access-{tokenSuffix}</c> and
    /// <c>refresh-{tokenSuffix}</c>.
    /// </summary>
    public static async Task SignInAsync(CookieSessionHost.Browser browser, string subject, string tokenSuffix, string challengePath = "/challenge")
    {
        using var challenge = new HttpRequestMessage(HttpMethod.Get, challengePath);
        using var redirect = await browser.SendAsync(challenge);
        redirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var query = QueryHelpers.ParseQuery(redirect.Headers.Location!.Query);

        using var callback = new HttpRequestMessage(HttpMethod.Post, "/signin-oidc")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                // The code carries what the token endpoint needs to answer, so the provider holds no state.
                ["code"] = $"{subject}|{query["nonce"]}|{tokenSuffix}",
                ["state"] = query["state"]!,
            }),
        };
        using var signedIn = await browser.SendAsync(callback);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.Redirect, await signedIn.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    public void Dispose() => rsa.Dispose();

    private static HttpResponseMessage RedeemCode(HttpRequestMessage request, SecurityKey key)
    {
        var form = QueryHelpers.ParseQuery(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var parts = form["code"].ToString().Split('|');
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = parts[0], ["nonce"] = parts[1] },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id_token = idToken,
                access_token = $"access-{parts[2]}",
                refresh_token = $"refresh-{parts[2]}",
                token_type = "Bearer",
                expires_in = 3600,
            }),
        };
    }
}
