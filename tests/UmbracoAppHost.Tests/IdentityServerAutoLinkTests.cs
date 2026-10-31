using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Umbraco.Cms.Api.Management.Security;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Security;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class IdentityServerAutoLinkTests
{
    [Fact]
    public async Task OpenID_preserves_Umbraco_creation_linking_callbacks_and_disabled_account_checks()
    {
        using var identity = new TestIdentityServer();
        var observedClaims = new List<string?>();
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            EnvironmentName = "Production",
            FirstBootGroup = "Settings",
            Settings =
            {
                ["IdentityServer:Name"] = "Native",
                ["IdentityServer:Authority"] = TestIdentityServer.Issuer,
                ["IdentityServer:ClientId"] = TestIdentityServer.ClientId,
                ["EmbeddedIdentityServer:Enabled"] = "false",
                // The old policy would refuse these new accounts before Umbraco could link them.
                ["Accounts:BackOfficeUsersManagedBy"] = "Umbraco",
                ["Accounts:MembersManagedBy"] = "Umbraco",
                ["Accounts:LinkByUserNameOnFirstSignIn"] = "false",
            },
            ConfigureBuilder = builder =>
            {
                foreach (var scheme in new[] { "Umbraco.Native", "UmbracoMembers.Native" })
                    builder.Services.PostConfigure<OpenIdConnectOptions>(scheme, options =>
                    {
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(identity.Metadata);
                        options.Backchannel = new HttpClient(identity, disposeHandler: false);
                    });
                builder.Services.PostConfigure<BackOfficeExternalLoginProviderOptions>("Umbraco.Native", options =>
                    options.AutoLinkOptions.OnAutoLinking = (_, info) => observedClaims.Add(info.Principal.FindFirst("department")?.Value));
                builder.Services.PostConfigure<MemberExternalLoginProviderOptions>("UmbracoMembers.Native", options =>
                    options.AutoLinkOptions.OnAutoLinking = (_, info) => observedClaims.Add(info.Principal.FindFirst("department")?.Value));
            },
            ConfigureApp = app =>
            {
                app.MapGet("/__tests/native-login/{audience}", async (string audience, HttpContext context) =>
                {
                    var scheme = audience == "backoffice" ? "Umbraco.Native" : "UmbracoMembers.Native";
                    var callback = "/__tests/native-callback/" + audience;
                    var properties = audience == "backoffice"
                        ? context.RequestServices.GetRequiredService<IBackOfficeSignInManager>().ConfigureExternalAuthenticationProperties(scheme, callback)
                        : context.RequestServices.GetRequiredService<IMemberSignInManager>().ConfigureExternalAuthenticationProperties(scheme, callback);
                    await context.ChallengeAsync(scheme, properties);
                });
                app.MapGet("/__tests/native-callback/{audience}", async (string audience, HttpContext context) =>
                {
                    Microsoft.AspNetCore.Identity.SignInResult result;
                    if (audience == "backoffice")
                    {
                        var manager = context.RequestServices.GetRequiredService<IBackOfficeSignInManager>();
                        var info = await manager.GetExternalLoginInfoAsync();
                        Assert.NotNull(info);
                        result = await manager.ExternalLoginSignInAsync(info, isPersistent: false);
                    }
                    else
                    {
                        var manager = context.RequestServices.GetRequiredService<IMemberSignInManager>();
                        var info = await manager.GetExternalLoginInfoAsync();
                        Assert.NotNull(info);
                        result = await manager.ExternalLoginSignInAsync(info, isPersistent: false);
                    }
                    return Results.Json(new { result.Succeeded, result.IsNotAllowed, result.IsLockedOut });
                });
            },
        });

        var schemes = await site.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.DoesNotContain(schemes, scheme => scheme.Name is "Umbraco.Windows" or "UmbracoMembers.Windows" or "Negotiate");
        var backofficeOptions = site.Services.GetRequiredService<IOptionsMonitor<BackOfficeExternalLoginProviderOptions>>().Get("Umbraco.Native");
        var memberOptions = site.Services.GetRequiredService<IOptionsMonitor<MemberExternalLoginProviderOptions>>().Get("UmbracoMembers.Native");
        Assert.True(backofficeOptions.AutoLinkOptions.AutoLinkExternalAccount);
        Assert.True(backofficeOptions.AutoLinkOptions.AllowManualLinking);
        Assert.Empty(backofficeOptions.AutoLinkOptions.DefaultUserGroups);
        Assert.True(memberOptions.AutoLinkOptions.AutoLinkExternalAccount);

        foreach (var audience in new[] { "backoffice", "member" })
        {
            var scheme = audience == "backoffice" ? "Umbraco.Native" : "UmbracoMembers.Native";
            var subject = audience == "backoffice" ? "S-1-5-21-100-200-300-9101" : "S-1-5-21-100-200-300-9102";
            var email = audience + "@native.example.invalid";
            var signedIn = await SignIn(site, identity, audience, subject, email);
            Assert.True(signedIn.GetProperty("succeeded").GetBoolean(), signedIn.ToString());
            Assert.Equal("Operations", observedClaims.Last());
            var users = site.Services.GetRequiredService<IUserService>();
            var members = site.Services.GetRequiredService<IMemberService>();
            var key = audience == "backoffice" ? users.GetByEmail(email)!.Key : members.GetByEmail(email)!.Key;
            var logins = site.Services.GetRequiredService<IExternalLoginWithKeyService>();
            Assert.Equal(subject, Assert.Single(logins.GetExternalLogins(key)).ProviderKey);
            if (audience == "backoffice") Assert.Empty(users.GetByEmail(email)!.Groups);

            // An existing local account is found by email by Umbraco, regardless of its username.
            var existingEmail = "existing-" + email;
            var existingKey = audience == "backoffice"
                ? (await site.CreateUserAsync("local-" + audience, existingEmail, "Existing user")).Key
                : site.CreateMember("local-" + audience, existingEmail, "Existing member").Key;
            Assert.True((await SignIn(site, identity, audience, subject + "1", existingEmail)).GetProperty("succeeded").GetBoolean());
            Assert.Equal(existingKey.ToString(), Assert.Single(logins.Find(scheme, subject + "1")).UserId, ignoreCase: true);

            // Missing email is left to Umbraco's native validation; no synthetic account is made.
            Assert.False((await SignIn(site, identity, audience, subject + "2", null)).GetProperty("succeeded").GetBoolean());
            Assert.Empty(logins.Find(scheme, subject + "2"));

            // Retained links let the native manager reject a retired identity without a custom veto.
            using (var scope = site.CreateScope())
            {
                if (audience == "backoffice")
                    await users.DeleteAsync(Constants.Security.SuperUserKey, key);
                else
                    Assert.True((await scope.ServiceProvider.GetRequiredService<IMemberEditingService>()
                        .DeleteAsync(key, Constants.Security.SuperUserKey)).Success);
            }
            var retired = await SignIn(site, identity, audience, subject, email);
            Assert.False(retired.GetProperty("succeeded").GetBoolean());
            Assert.True(retired.GetProperty("isNotAllowed").GetBoolean() || retired.GetProperty("isLockedOut").GetBoolean());
            Assert.Equal(key.ToString(), Assert.Single(logins.Find(scheme, subject)).UserId, ignoreCase: true);

            // Hosts can opt out using the standard options; linked accounts still sign in.
            if (audience == "backoffice") backofficeOptions.AutoLinkOptions = new ExternalSignInAutoLinkOptions(autoLinkExternalAccount: false);
            else memberOptions.AutoLinkOptions = new MemberExternalSignInAutoLinkOptions(autoLinkExternalAccount: false);
            Assert.False((await SignIn(site, identity, audience, subject + "3", "no-create-" + email)).GetProperty("succeeded").GetBoolean());
            Assert.Empty(logins.Find(scheme, subject + "3"));
            Assert.True((await SignIn(site, identity, audience, subject + "1", existingEmail)).GetProperty("succeeded").GetBoolean());
        }
    }

    private static async Task<JsonElement> SignIn(TestSite site, TestIdentityServer identity, string audience, string subject, string? email)
    {
        using var browser = site.CreateClient();
        using var challenge = await browser.GetAsync("/__tests/native-login/" + audience);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var parameters = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        identity.Subject = subject;
        identity.Email = email;
        identity.Nonce = parameters["nonce"].ToString();
        using var callback = await browser.GetAsync(QueryHelpers.AddQueryString("/signin-oidc/" + audience,
            new Dictionary<string, string?> { ["code"] = "test-code", ["state"] = parameters["state"].ToString() }));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/__tests/native-callback/" + audience, callback.Headers.Location!.OriginalString);
        using var native = await browser.GetAsync(callback.Headers.Location);
        var body = await native.Content.ReadAsStringAsync();
        Assert.True(native.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed class TestIdentityServer : HttpMessageHandler
    {
        public const string Issuer = "https://identity.example.invalid";
        public const string ClientId = "native-test-client";
        private readonly RSA rsa = RSA.Create(2048);
        private readonly RsaSecurityKey key;
        public TestIdentityServer()
        {
            key = new RsaSecurityKey(rsa) { KeyId = "test-signing-key" };
            Metadata = new OpenIdConnectConfiguration
            {
                Issuer = Issuer, AuthorizationEndpoint = Issuer + "/authorize", TokenEndpoint = Issuer + "/token",
            };
            Metadata.SigningKeys.Add(key);
        }
        public OpenIdConnectConfiguration Metadata { get; }
        public string Subject { get; set; } = "";
        public string? Email { get; set; }
        public string Nonce { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(Issuer + "/token", request.RequestUri!.ToString());
            var claims = new List<Claim>
            {
                new("sub", Subject), new("name", "Native test account"), new("nonce", Nonce), new("department", "Operations"),
                new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            };
            if (Email is not null) claims.Add(new Claim("email", Email));
            var token = new JwtSecurityToken(Issuer, ClientId, claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id_token = new JwtSecurityTokenHandler().WriteToken(token), access_token = "test-access-token", token_type = "Bearer", expires_in = 300 }),
            });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) rsa.Dispose();
            base.Dispose(disposing);
        }
    }
}
