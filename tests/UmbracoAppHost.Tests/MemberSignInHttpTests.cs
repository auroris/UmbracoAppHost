using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Umbraco.Cms.Core;
using Umbraco.Extensions;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class MemberSignInHttpTests
{
    private const string prefix = "/wing";
    private const string scheme = "UmbracoMembers.TestIdentity";

    [Fact]
    [ResetPublishedContentServices]
    public async Task Member_sso_uses_native_callbacks_and_password_fallback_without_restarting_failed_or_two_factor_flows()
    {
        var provider = new ProviderProbe();
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            VirtualPath = prefix,
            Settings =
            {
                ["IdentityServer:Enabled"] = "true",
                ["IdentityServer:Name"] = "Test Identity",
                ["IdentityServer:Authority"] = "https://identity.example.invalid",
                ["IdentityServer:BackOfficeEnabled"] = "false",
                ["EmbeddedIdentityServer:Enabled"] = "false",
                ["Accounts:MembersManagedBy"] = "Umbraco",
            },
            ConfigureBuilder = builder => UseTestProvider(builder, provider),
            ConfigureApp = app => app.MapGet("/__tests/member-signin-needed", async (SiteVisitor visitor) => {
                await visitor.ResolveAsync();
                return Results.Json(new { visitor.IsBackOfficeUser, StartMemberSignIn = visitor.ShouldStartMemberSignIn(false, false) });
            }),
        });
        var member = site.CreateMember("member-login", "member-login@example.invalid", "Member login");
        const string password = "Local-test-only-Member91!";
        await site.SetPasswordAsync(member, password);
        using var browser = site.CreateClient();
        const string destination = prefix + "/orders-form/?id=4&topic=member-test";

        using (var administrator = site.CreateClient())
        {
            (await administrator.GetAsync($"{prefix}/__tests/login/{Constants.Security.SuperUserKey}?scheme=backoffice&name=Member%20sign-in%20test%20administrator")).EnsureSuccessStatusCode();
            var memberNeeded = await administrator.GetFromJsonAsync<System.Text.Json.JsonElement>(prefix + "/__tests/member-signin-needed");
            Assert.True(memberNeeded.GetProperty("isBackOfficeUser").GetBoolean());
            Assert.True(memberNeeded.GetProperty("startMemberSignIn").GetBoolean());
        }

        var automatic = await browser.GetAsync(destination);
        Assert.Equal(HttpStatusCode.Redirect, automatic.StatusCode);
        Assert.True(automatic.Headers.CacheControl?.NoStore);
        Assert.StartsWith(prefix + "/orders-form/?ufprt=", automatic.Headers.Location!.OriginalString);
        var challenge = await browser.GetAsync(automatic.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Equal("identity.example.invalid", challenge.Headers.Location!.Host);
        Assert.Equal(destination, provider.Properties!.Items[SsoLoginUrls.MemberReturnUrlProperty]);
        var nativeCallback = provider.Properties.RedirectUri!;
        Assert.StartsWith(prefix + "/orders-form/?ufprt=", nativeCallback);

        var state = QueryHelpers.ParseQuery(challenge.Headers.Location.Query)["state"].ToString();
        var remoteFailure = await browser.GetAsync(QueryHelpers.AddQueryString(prefix + "/signin-oidc/member", new Dictionary<string, string?> {
            ["error"] = "temporarily_unavailable", ["error_description"] = "The test provider is unavailable.", ["state"] = state,
        }));
        Assert.Equal(HttpStatusCode.Redirect, remoteFailure.StatusCode);
        var fallbackUrl = remoteFailure.Headers.Location!.OriginalString;
        Assert.StartsWith(destination + "&sso-local=1", fallbackUrl);
        var fallback = await Page(browser, fallbackUrl);
        Assert.Contains("loginModel.Username", fallback);
        Assert.Contains("Sign in with Test Identity", fallback);

        provider.Unavailable = true;
        var fresh = await browser.GetAsync(destination);
        var unavailable = await browser.GetAsync(fresh.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, unavailable.StatusCode);
        Assert.StartsWith(destination + "&sso-local=1", unavailable.Headers.Location!.OriginalString);
        Assert.Contains("The identity server is unavailable", await Page(browser, unavailable.Headers.Location.OriginalString));
        provider.Unavailable = false;

        // An explicit retry is protected by antiforgery and cannot select a local cookie or backoffice scheme.
        var retry = ReadForm(fallback, form => form.Contains("name=\"provider\"", StringComparison.Ordinal));
        var missingToken = new Dictionary<string, string>(retry.Fields);
        missingToken.Remove("__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync(retry.Action, new FormUrlEncodedContent(missingToken))).StatusCode);
        retry.Fields["provider"] = "Identity.Application";
        var wrongProvider = await browser.PostAsync(retry.Action, new FormUrlEncodedContent(retry.Fields));
        Assert.Equal(HttpStatusCode.Redirect, wrongProvider.StatusCode);
        Assert.Contains("sso-local=1", wrongProvider.Headers.Location!.OriginalString);
        retry.Fields["provider"] = scheme;
        var retried = await browser.PostAsync(retry.Action, new FormUrlEncodedContent(retry.Fields));
        Assert.Equal(HttpStatusCode.Redirect, retried.StatusCode);
        Assert.Equal("identity.example.invalid", retried.Headers.Location!.Host);

        // This native callback has no external ticket. Its provider error must show a password form, not restart SSO.
        var callbackError = await Page(browser, nativeCallback);
        Assert.Contains("Invalid response from the login provider", callbackError);
        Assert.Contains("loginModel.Username", callbackError);
        var twoFactor = await Page(browser, destination + "&test-two-factor=1");
        Assert.Contains("Security code for Test authenticator", twoFactor);
        Assert.DoesNotContain("loginModel.Username", twoFactor);
        Assert.DoesNotContain("Sign in with Test Identity", twoFactor);

        var login = ReadForm(callbackError, form => form.Contains("class=\"app-login-form\"", StringComparison.Ordinal));
        Assert.Equal(destination + "&sso-local=1", login.Action);
        Assert.DoesNotContain("ufprt", login.Action);
        login.Fields["loginModel.Username"] = member.Username;
        login.Fields["loginModel.Password"] = "Wrong-password91!";
        var refusedPassword = await browser.PostAsync(login.Action, new FormUrlEncodedContent(login.Fields));
        Assert.Equal(HttpStatusCode.OK, refusedPassword.StatusCode);
        Assert.Contains("Invalid username or password", await refusedPassword.Content.ReadAsStringAsync());
        login.Fields["loginModel.Password"] = password;
        var signedIn = await browser.PostAsync(login.Action, new FormUrlEncodedContent(login.Fields));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(destination, signedIn.Headers.Location!.OriginalString);
        Assert.Contains("data-app-form", await Page(browser, destination));
    }

    /// <summary>
    /// Automatic sign-in goes through a URL the site protects and then reads back, so each site in a process has to use
    /// its own keys for it. It used to work on whichever site got there first, and leave every later one on the page.
    /// </summary>
    [Fact]
    [ResetPublishedContentServices]
    public async Task Automatic_sign_in_works_on_every_site_a_process_starts()
    {
        for (var start = 0; start < 2; start++)
        {
            await using var site = await TestSite.StartAsync(new TestSiteOptions
            {
                Application = typeof(Program).Assembly,
                Settings =
                {
                    ["IdentityServer:Enabled"] = "true",
                    ["IdentityServer:Name"] = "Test Identity",
                    ["IdentityServer:Authority"] = "https://identity.example.invalid",
                    ["IdentityServer:BackOfficeEnabled"] = "false",
                    ["EmbeddedIdentityServer:Enabled"] = "false",
                    ["Accounts:MembersManagedBy"] = "Umbraco",
                },
                ConfigureBuilder = builder => UseTestProvider(builder, new ProviderProbe()),
            });
            using var browser = site.CreateClient();
            var automatic = await browser.GetAsync("/orders-form/?id=4");
            Assert.Equal(HttpStatusCode.Redirect, automatic.StatusCode);
            var challenge = await browser.GetAsync(automatic.Headers.Location);
            Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
            Assert.Equal("identity.example.invalid", challenge.Headers.Location!.Host);
        }
    }

    /// <summary>Exercises the real OIDC challenge and callback handler without an external identity service.</summary>
    private static void UseTestProvider(WebApplicationBuilder builder, ProviderProbe provider)
    {
        builder.Services.PostConfigure<OpenIdConnectOptions>(scheme, options => {
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new() {
                Issuer = "https://identity.example.invalid",
                AuthorizationEndpoint = "https://identity.example.invalid/authorize",
                TokenEndpoint = "https://identity.example.invalid/token",
            });
            options.Events.OnRedirectToIdentityProvider = context => {
                provider.Properties = context.Properties;
                if (provider.Unavailable) throw new HttpRequestException("Test identity provider is unavailable.");
                return Task.CompletedTask;
            };
        });
        builder.Services.Configure<MvcOptions>(options => options.Filters.Add(new TwoFactorState()));
    }

    private static async Task<string> Page(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {html}");
        Assert.DoesNotContain("Could not render", html);
        return html;
    }

    private static (string Action, Dictionary<string, string> Fields) ReadForm(string html, Func<string, bool> matches)
    {
        var form = Regex.Matches(html, "<form\\b[^>]*>.*?</form>", RegexOptions.Singleline).Cast<Match>().Select(match => match.Value).Single(matches);
        var fields = new Dictionary<string, string>();
        foreach (Match input in Regex.Matches(form, "<input\\b[^>]*>"))
        {
            var name = Regex.Match(input.Value, "\\bname=\"([^\"]*)\"");
            if (name.Success) fields[WebUtility.HtmlDecode(name.Groups[1].Value)] = WebUtility.HtmlDecode(Regex.Match(input.Value, "\\bvalue=\"([^\"]*)\"").Groups[1].Value);
        }
        return (WebUtility.HtmlDecode(Regex.Match(form, "\\baction=\"([^\"]*)\"").Groups[1].Value), fields);
    }

    private sealed class ProviderProbe
    {
        public bool Unavailable { get; set; }
        public AuthenticationProperties? Properties { get; set; }
    }

    private sealed class TwoFactorState : IResultFilter
    {
        public void OnResultExecuting(ResultExecutingContext context)
        {
            if (context.HttpContext.Request.Query.ContainsKey("test-two-factor") && context.Result is ViewResult view)
                view.ViewData!.SetTwoFactorProviderNames(["Test authenticator"]);
        }
        public void OnResultExecuted(ResultExecutedContext context) { }
    }
}
