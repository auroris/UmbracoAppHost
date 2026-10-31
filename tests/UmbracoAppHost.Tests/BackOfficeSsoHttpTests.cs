using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class BackOfficeSsoHttpTests
{
    private const string Prefix = "/wing";
    private const string Api = Prefix + "/umbraco/management/api/v1/security/back-office";
    private const string Scheme = "Umbraco.TestIdentity";
    private const string Password = "Local-test-only-Fallback91!";

    [Fact]
    public async Task Discovery_outage_falls_back_to_native_password_and_two_factor_login_without_repeating_sso()
    {
        var metadata = new UnavailableMetadata();
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            EnvironmentName = "Production",
            VirtualPath = Prefix,
            AdminPassword = Password,
            FirstBootGroup = "Settings",
            Settings =
            {
                ["BackOfficeLogin:DefaultFlow"] = "Auto",
                ["IdentityServer:Enabled"] = "true",
                ["IdentityServer:Name"] = "Test Identity",
                ["IdentityServer:Authority"] = "https://identity.example.invalid",
                ["IdentityServer:BackOfficeEnabled"] = "true",
                ["IdentityServer:MemberEnabled"] = "false",
                ["IdentityServer:DenyLocalLogin"] = "false",
                ["EmbeddedIdentityServer:Enabled"] = "false",
                ["Accounts:BackOfficeUsersManagedBy"] = "Umbraco",
                ["Accounts:MembersManagedBy"] = "Umbraco",
            },
            ConfigureBuilder = builder =>
            {
                builder.Services.PostConfigure<OpenIdConnectOptions>(Scheme, options => options.ConfigurationManager = metadata);
                builder.Services.AddSingleton<TestTwoFactorProvider>();
                builder.Services.AddSingleton<ITwoFactorProvider>(services => services.GetRequiredService<TestTwoFactorProvider>());
                // After Umbraco's own configuration of these options, which this callback precedes.
                builder.Services.PostConfigure<BackOfficeIdentityOptions>(options =>
                    options.Tokens.ProviderMap[TestTwoFactorProvider.Name] = new TokenProviderDescriptor(typeof(TestTwoFactorProvider)));
            },
        });
        var address = site.Address;
        var callback = new Uri(address, Prefix + "/umbraco/oauth_complete").ToString();
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = QueryHelpers.AddQueryString(Api + "/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = Constants.OAuthClientIds.BackOffice,
            ["redirect_uri"] = callback,
            ["response_type"] = "code",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["identity_provider"] = Scheme,
            ["state"] = "fallback-state+value",
        });

        using (var browser = site.CreateClient())
        {
            var continuation = await OpenFallback(browser, authorize, challenge);
            Assert.Equal(1, metadata.Calls);
            using var wrongPassword = await browser.PostAsJsonAsync(Api + "/login", new { username = "admin", password = "An incorrect password" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
            Assert.Null(wrongPassword.Headers.Location);
            Assert.Equal(1, metadata.Calls);

            // The form's ReturnUrl still challenges only the native password scheme after a bad password.
            using var unsigned = await browser.GetAsync(continuation);
            Assert.Equal(HttpStatusCode.Redirect, unsigned.StatusCode);
            Assert.Equal(Prefix + "/umbraco/login", new Uri(address, unsigned.Headers.Location!).AbsolutePath);
            Assert.Equal(1, metadata.Calls);

            using var login = await browser.PostAsJsonAsync(Api + "/login", new { username = "admin", password = Password });
            Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
            Assert.Contains(login.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("UMB_UCONTEXT=", StringComparison.Ordinal));
            await CompleteAuthorization(browser, continuation, callback, verifier);
            Assert.Equal(1, metadata.Calls);
        }

        // Turn on a real native two-factor provider, then repeat from a browser with no cookies.
        using (var scope = site.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ITwoFactorLoginService>().SaveAsync(new TwoFactorLogin
            {
                UserOrMemberKey = Constants.Security.SuperUserKey, ProviderName = TestTwoFactorProvider.Name,
                Secret = "test-only-secret", Confirmed = true,
            });
        using (var browser = site.CreateClient())
        {
            var continuation = await OpenFallback(browser, authorize, challenge);
            Assert.Equal(2, metadata.Calls);
            using var login = await browser.PostAsJsonAsync(Api + "/login", new { username = "admin", password = Password });
            Assert.Equal(HttpStatusCode.PaymentRequired, login.StatusCode);
            Assert.Contains(TestTwoFactorProvider.Name, await login.Content.ReadAsStringAsync());
            Assert.False(login.Headers.TryGetValues("Set-Cookie", out var cookies)
                && cookies.Any(cookie => cookie.StartsWith("UMB_UCONTEXT=", StringComparison.Ordinal)));
            Assert.Equal(2, metadata.Calls);

            using var verify = await browser.PostAsJsonAsync(Api + "/verify-2fa", new
            {
                provider = TestTwoFactorProvider.Name, code = TestTwoFactorProvider.Code, isPersistent = true, rememberClient = false,
            });
            Assert.True(verify.IsSuccessStatusCode, await verify.Content.ReadAsStringAsync());
            await CompleteAuthorization(browser, continuation, callback, verifier);
            Assert.Equal(2, metadata.Calls);
        }
    }

    private static async Task<string> OpenFallback(HttpClient browser, string authorize, string challenge)
    {
        using var response = await browser.GetAsync(authorize);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        var location = new Uri(browser.BaseAddress!, response.Headers.Location!);
        Assert.Equal(Prefix + "/umbraco/login", location.AbsolutePath);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Contains("unavailable", query[SsoRefusal.DeniedParameter].ToString());
        var continuation = query["ReturnUrl"].ToString();
        Assert.StartsWith(Api + "/authorize?", continuation);
        var parameters = QueryHelpers.ParseQuery(new Uri(browser.BaseAddress!, continuation).Query);
        Assert.False(parameters.ContainsKey("identity_provider"));
        Assert.Equal("fallback-state+value", parameters["state"].ToString());
        Assert.Equal(challenge, parameters["code_challenge"].ToString());
        Assert.Equal("S256", parameters["code_challenge_method"].ToString());
        using var form = await browser.GetAsync(location);
        var html = await form.Content.ReadAsStringAsync();
        Assert.True(form.IsSuccessStatusCode, html);
        Assert.Contains("<umb-auth", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/umbraco/login/login.js", html, StringComparison.OrdinalIgnoreCase);
        return continuation;
    }

    private static async Task CompleteAuthorization(HttpClient browser, string continuation, string callback, string verifier)
    {
        using var authorize = await browser.GetAsync(continuation);
        Assert.True(authorize.StatusCode == HttpStatusCode.Redirect, await authorize.Content.ReadAsStringAsync());
        var location = new Uri(browser.BaseAddress!, authorize.Headers.Location!);
        Assert.StartsWith(callback + "?", location.ToString());
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("fallback-state+value", query["state"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code"]));
        using var token = await browser.PostAsync(Api + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = Constants.OAuthClientIds.BackOffice,
            ["redirect_uri"] = callback, ["code"] = query["code"].ToString(), ["code_verifier"] = verifier,
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, body);
        using var current = new HttpRequestMessage(HttpMethod.Get, Prefix + "/umbraco/management/api/v1/user/current");
        current.Headers.Authorization = new AuthenticationHeaderValue("Bearer", JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString());
        using var user = await browser.SendAsync(current);
        var userBody = await user.Content.ReadAsStringAsync();
        Assert.True(user.IsSuccessStatusCode, userBody);
        Assert.Contains("admin@localhost.local", userBody, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UnavailableMetadata : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public int Calls { get; private set; }
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("Test discovery outage.");
        }
        public void RequestRefresh() { }
    }

    public sealed class TestTwoFactorProvider : IUserTwoFactorTokenProvider<BackOfficeIdentityUser>, ITwoFactorProvider
    {
        public const string Name = "Fallback test authenticator";
        public const string Code = "123456";
        public string ProviderName => Name;
        public Task<string> GenerateAsync(string purpose, UserManager<BackOfficeIdentityUser> manager, BackOfficeIdentityUser user) => Task.FromResult(Code);
        public Task<bool> ValidateAsync(string purpose, string token, UserManager<BackOfficeIdentityUser> manager, BackOfficeIdentityUser user) => Task.FromResult(token == Code);
        public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<BackOfficeIdentityUser> manager, BackOfficeIdentityUser user) => Task.FromResult(true);
        public Task<ISetupTwoFactorModel> GetSetupDataAsync(Guid userOrMemberKey, string secret) => throw new NotSupportedException();
        public bool ValidateTwoFactorPIN(string secret, string token) => token == Code;
        public bool ValidateTwoFactorSetup(string secret, string token) => token == Code;
    }
}
