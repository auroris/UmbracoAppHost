using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Manifest;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

/// <summary>
/// A site in IIS with only Anonymous Authentication enabled. IIS then registers a server
/// authentication service that reports Windows authentication as disabled, which makes the Negotiate
/// handler's options throw when they are built. The site must still serve every request, the embedded
/// identity server must refuse cleanly, and the login screens must offer no provider that cannot work.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class AnonymousHostingTests
{
    private const string ClientId = "anonymous-hosting-client";
    private const string RedirectUri = "https://client.example/callback";

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Embedded_server_uses_the_outer_hosts_integrated_Windows_handler()
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            EnvironmentName = "Production",
            FirstBootGroup = "Settings",
            Settings =
            {
                ["EmbeddedIdentityServer:Clients:0:ClientId"] = ClientId,
                ["EmbeddedIdentityServer:Clients:0:RedirectUris:0"] = RedirectUri,
            },
            ConfigureBuilder = builder =>
            {
                // IIS supplies both this capability and its native authentication handler to the
                // outer host. The embedded service container must delegate to that existing handler.
                builder.Services.AddSingleton<IServerIntegratedAuth>(new WindowsServer());
                builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NativeWindowsHandler>("TestWindows", _ => { });
            },
        }, TestContext.Current.CancellationToken);
        using var browser = site.CreateClient();
        using var response = await browser.GetAsync("/connect/authorize" + QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = ClientId, ["redirect_uri"] = RedirectUri, ["response_type"] = "code", ["scope"] = "openid",
            ["state"] = "native-windows", ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", ["code_challenge_method"] = "S256",
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var returned = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.False(returned.ContainsKey("error"), response.Headers.Location.ToString());
        Assert.False(string.IsNullOrEmpty(returned["code"]));
        Assert.Equal("native-windows", returned["state"].ToString());
    }

    [Fact]
    public async Task Anonymous_only_hosting_keeps_serving_and_falls_back_to_passwords()
    {
        var log = new StartupLog();
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            EnvironmentName = "Production",
            FirstBootGroup = "Settings",
            Settings =
            {
                ["EmbeddedIdentityServer:Clients:0:ClientId"] = ClientId,
                ["EmbeddedIdentityServer:Clients:0:RedirectUris:0"] = RedirectUri,
            },
            ConfigureBuilder = builder =>
            {
                // What IIS registers for a site whose Windows Authentication feature is switched off.
                builder.Services.AddSingleton<IServerIntegratedAuth>(new AnonymousOnlyServer());
                builder.Services.AddSingleton<ILogger<WindowsAuthenticationStartupLog>>(log);
            },
            ConfigureApp = app =>
            {
                app.MapGet("/__tests/negotiate", async (HttpContext context) =>
                {
                    var result = await context.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme);
                    return Results.Json(new { result.Succeeded, result.None, Failure = result.Failure?.Message });
                });
                app.MapGet("/__tests/member-signin", async (SiteVisitor visitor) =>
                {
                    await visitor.ResolveAsync();
                    return Results.Json(new { Automatic = visitor.AutomaticProvider?.Suffix, Providers = visitor.Providers.Count, Start = visitor.ShouldStartMemberSignIn(false, false) });
                });
            },
        }, TestContext.Current.CancellationToken);
        using var browser = site.CreateClient();

        // Every request runs the authentication middleware; none may fail because of the Negotiate handler.
        using (var login = await browser.GetAsync("/umbraco/login", TestContext.Current.CancellationToken))
        {
            var html = await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(login.IsSuccessStatusCode, html);
            Assert.Contains("<umb-auth", html, StringComparison.OrdinalIgnoreCase);
        }
        var negotiate = await browser.GetFromJsonAsync<JsonElement>("/__tests/negotiate", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(negotiate.GetProperty("succeeded").GetBoolean());
        Assert.Contains("Windows authentication", negotiate.GetProperty("failure").GetString());

        // The embedded identity server refuses through the client's validated redirect address.
        using (var authorize = await browser.GetAsync("/connect/authorize" + QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = ClientId, ["redirect_uri"] = RedirectUri, ["response_type"] = "code", ["scope"] = "openid",
            ["state"] = "state-1", ["nonce"] = "nonce-1", ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", ["code_challenge_method"] = "S256",
        }), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
            Assert.StartsWith(RedirectUri + "?", authorize.Headers.Location!.ToString());
            var returned = QueryHelpers.ParseQuery(authorize.Headers.Location.Query);
            Assert.Equal(OpenIddictConstants.Errors.AccessDenied, returned["error"].ToString());
            Assert.Contains("Windows authentication is unavailable", returned["error_description"].ToString());
            Assert.Equal("state-1", returned["state"].ToString());
        }

        // The login screens omit the unavailable embedded provider and start nothing.
        var catalog = site.Services.GetRequiredService<SsoProviderCatalog>();
        Assert.Single(catalog.All);
        Assert.All(catalog.All, provider => Assert.False(provider.Available));
        Assert.Empty(catalog.BackOfficeLogins);
        Assert.Empty(catalog.MemberLogins);
        Assert.Empty(await site.Services.GetServices<IPackageManifestReader>().OfType<SsoPackageManifestReader>().Single().ReadPackageManifestsAsync());
        var member = await browser.GetFromJsonAsync<JsonElement>("/__tests/member-signin", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, member.GetProperty("automatic").ValueKind);
        Assert.Equal(0, member.GetProperty("providers").GetInt32());
        Assert.False(member.GetProperty("start").GetBoolean());

        Assert.Contains(log.Messages, message => message.Contains("Anonymous Authentication", StringComparison.Ordinal)
            && message.Contains("the embedded identity server", StringComparison.Ordinal)
            && message.Contains("with a username and password", StringComparison.Ordinal));
    }

    [Fact]
    public void An_external_identity_server_stays_available_without_Windows_authentication()
    {
        var external = Options.Create(new IdentityServerSettings { Enabled = true, Name = "Domain", Authority = "https://identity.example" });
        var embedded = Options.Create(new IdentityServerSettings { Enabled = true, Name = "Domain" });

        Assert.True(new WindowsAuthenticationAvailability([]).IsAvailable);
        var anonymous = new WindowsAuthenticationAvailability([new AnonymousOnlyServer()]);
        Assert.False(anonymous.IsAvailable);

        var catalog = new SsoProviderCatalog(external, anonymous);
        Assert.Equal(["Domain"], catalog.BackOfficeLogins.Select(provider => provider.Suffix));
        Assert.Equal(["Domain"], catalog.MemberLogins.Select(provider => provider.Suffix));
        Assert.Equal(["Domain"], catalog.All.Select(provider => provider.Suffix));

        Assert.Empty(new SsoProviderCatalog(embedded, anonymous).BackOfficeLogins);
        Assert.Single(new SsoProviderCatalog(embedded, new WindowsAuthenticationAvailability([])).BackOfficeLogins);
        Assert.Single(new SsoProviderCatalog(embedded).BackOfficeLogins);
    }

    private sealed class AnonymousOnlyServer : IServerIntegratedAuth
    {
        public bool IsEnabled => false;
        public string AuthenticationScheme => "Windows";
    }

    private sealed class WindowsServer : IServerIntegratedAuth
    {
        public bool IsEnabled => true;
        public string AuthenticationScheme => "TestWindows";
    }

    [SupportedOSPlatform("windows")]
    private sealed class NativeWindowsHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(WindowsIdentity.GetCurrent()), Scheme.Name)));
    }

    private sealed class StartupLog : ILogger<WindowsAuthenticationStartupLog>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));
    }
}
