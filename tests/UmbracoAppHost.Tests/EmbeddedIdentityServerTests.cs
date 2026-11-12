using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Authentication;
using UmbracoAppHost.IdentityServer;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The embedded identity server against real Umbraco hosts on Kestrel. The tests sign in as the
/// Windows account running them: <see cref="HttpClientHandler.UseDefaultCredentials"/> completes the
/// Negotiate exchange against 127.0.0.1 over HTTP/1.1.
/// </summary>
[Collection("Umbraco hosts")]
[SupportedOSPlatform("windows")]
public sealed class EmbeddedIdentityServerTests
{
    private const string ClientId = "test-client";
    private const string ClientSecret = "Local-test-only-client-secret-91";
    private const string RedirectUri = "https://client.example/callback";

    private static readonly Dictionary<string, string?> ClientSettings = new()
    {
        ["EmbeddedIdentityServer:Clients:0:ClientId"] = ClientId,
        ["EmbeddedIdentityServer:Clients:0:ClientSecret"] = ClientSecret,
        ["EmbeddedIdentityServer:Clients:0:RedirectUris:0"] = RedirectUri,
        ["EmbeddedIdentityServer:Groups:0"] = "^Authenticated Users$",
        ["EmbeddedIdentityServer:Groups:1"] = "^Users$",
    };

    [Theory]
    [InlineData("")]
    [InlineData("/wing")]
    public async Task Missing_or_failed_Windows_credentials_return_through_the_validated_client_request(string prefix)
    {
        var root = Directory.CreateTempSubdirectory("embedded-idsrv-fallback-").FullName;
        await using var site = await StartAsync(root, new Dictionary<string, string?>(ClientSettings)
        {
            ["Umbraco:CMS:Hosting:ApplicationVirtualPath"] = prefix,
        });
        using var anonymous = site.CreateClient();
        var url = AuthorizationUrl(prefix + "/connect/authorize", ClientId, RedirectUri, "openid");
        using var challenge = await anonymous.GetAsync(url, TestContext.Current.CancellationToken);
        var body = await challenge.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Contains(challenge.Headers.WwwAuthenticate, header => header.Scheme == NegotiateDefaults.AuthenticationScheme);
        Assert.True(challenge.Headers.CacheControl?.NoStore);
        Assert.Contains("http-equiv=\"refresh\"", body);
        var link = Regex.Match(body, "<a href=\"([^\"]+)\">");
        Assert.True(link.Success, body);
        var local = WebUtility.HtmlDecode(link.Groups[1].Value);
        Assert.StartsWith(prefix + "/connect/authorize?", local);
        var localQuery = QueryHelpers.ParseQuery(new Uri(site.Address, local).Query);
        Assert.Equal("1", localQuery[SsoLoginUrls.LocalParameter].ToString());
        Assert.Equal("state-1", localQuery["state"].ToString());
        Assert.Equal(RedirectUri, localQuery["redirect_uri"].ToString());
        using (var refused = await anonymous.GetAsync(local, TestContext.Current.CancellationToken)) AssertRefused(refused);

        // A broken Negotiate handshake occurs before MVC. It must still run OpenIddict's
        // validation and return an authorization error through the registered callback.
        using var broken = new HttpRequestMessage(HttpMethod.Get, url);
        broken.Headers.Authorization = new AuthenticationHeaderValue(NegotiateDefaults.AuthenticationScheme, "invalid-token");
        using (var refused = await anonymous.SendAsync(broken, TestContext.Current.CancellationToken)) AssertRefused(refused);

        foreach (var invalid in new[]
        {
            AuthorizationUrl(prefix + "/connect/authorize", "not-registered", RedirectUri, "openid"),
            AuthorizationUrl(prefix + "/connect/authorize", ClientId, "https://other.example/callback", "openid"),
            AuthorizationUrl(prefix + "/connect/authorize", "umbraco-backoffice", "https://other.example/signin-oidc/backoffice", "openid"),
            AuthorizationUrl(prefix + "/connect/authorize", "umbraco-backoffice", Address(site) + prefix + "/some-other-callback", "openid"),
        })
        {
            using var response = await anonymous.GetAsync(QueryHelpers.AddQueryString(invalid, SsoLoginUrls.LocalParameter, "1"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }

        // The automatically configured site client needs no database registration. Its exact
        // callback follows the request origin and virtual path, including Host-header casing.
        var host = "LOCALHOST:" + site.Address.Port;
        var ownCallback = "http://" + host + prefix + "/signin-oidc/backoffice";
        using var ownRequest = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString(
            AuthorizationUrl(prefix + "/connect/authorize", "umbraco-backoffice", ownCallback, "openid"), SsoLoginUrls.LocalParameter, "1"));
        ownRequest.Headers.Host = host;
        using var ownResponse = await anonymous.SendAsync(ownRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, ownResponse.StatusCode);
        Assert.Equal("access_denied", QueryHelpers.ParseQuery(ownResponse.Headers.Location!.Query)["error"].ToString());

        var withoutPkce = QueryHelpers.ParseQuery(new Uri(site.Address, url).Query)
            .Where(parameter => !parameter.Key.StartsWith("code_challenge", StringComparison.Ordinal))
            .ToDictionary(parameter => parameter.Key, parameter => (string?)parameter.Value.ToString());
        using var missingPkce = await anonymous.GetAsync(prefix + "/connect/authorize" + QueryString.Create(withoutPkce), TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.Unauthorized, missingPkce.StatusCode);
        var pkceError = missingPkce.Headers.Location?.Query ?? await missingPkce.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("invalid_request", pkceError);

        // Other Negotiate consumers retain native failure handling.
        var options = site.Services.GetRequiredService<IOptionsMonitor<NegotiateOptions>>().Get(NegotiateDefaults.AuthenticationScheme);
        var context = new DefaultHttpContext { RequestServices = site.Services };
        context.Request.Path = "/some-other-windows-route";
        var failure = new AuthenticationFailedContext(context,
            new AuthenticationScheme(NegotiateDefaults.AuthenticationScheme, null, typeof(NegotiateHandler)), options)
        { Exception = new InvalidOperationException("Windows authentication unavailable") };
        await options.Events!.OnAuthenticationFailed(failure);
        Assert.Null(failure.Result);
        Assert.Empty(context.Items);

        static void AssertRefused(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.NotNull(response.Headers.Location);
            Assert.StartsWith(RedirectUri + "?", response.Headers.Location.ToString());
            var returned = QueryHelpers.ParseQuery(response.Headers.Location.Query);
            Assert.Equal(OpenIddictConstants.Errors.AccessDenied, returned["error"].ToString());
            Assert.Equal("state-1", returned["state"].ToString());
            Assert.False(returned.ContainsKey("code"));
        }
    }

    [Fact]
    public async Task Registered_clients_get_windows_tokens_signed_with_a_key_that_survives_a_restart()
    {
        var root = Directory.CreateTempSubdirectory("embedded-idsrv-").FullName;
        string idToken, issuer, keyId;
        await using (var site = await StartAsync(root, ClientSettings))
        {
            var address = Address(site);
            keyId = site.Services.GetRequiredService<EmbeddedIdentityServerHost>().ServerOptions.SigningCredentials[0].Key.KeyId;
            Assert.False(string.IsNullOrEmpty(keyId));
            Assert.True(File.Exists(Path.Combine(site.DataRoot, "keys", "identity-server", "signing-key.bin")));
            Assert.True(File.Exists(Path.Combine(site.DataRoot, "keys", "identity-server", "encryption-key.bin")));
            var umbracoServer = site.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
            Assert.False(umbracoServer.EnableDegradedMode);
            Assert.DoesNotContain(umbracoServer.AuthorizationEndpointUris, uri => uri.ToString().Contains("connect/authorize"));
            Assert.DoesNotContain(umbracoServer.SigningCredentials, credentials => credentials.Key.KeyId == keyId);

            using var anonymous = site.CreateClient();
            var discovery = await GetJsonAsync(anonymous, "/.well-known/openid-configuration");
            issuer = discovery.GetProperty("issuer").GetString()!;
            Assert.Equal(address + "/", issuer);
            Assert.Equal(address + "/connect/authorize", discovery.GetProperty("authorization_endpoint").GetString());
            Assert.Equal(address + "/connect/token", discovery.GetProperty("token_endpoint").GetString());
            var scopes = discovery.GetProperty("scopes_supported").EnumerateArray().Select(scope => scope.GetString()).ToArray();
            Assert.Contains("profile", scopes);
            Assert.Contains("roles", scopes);
            Assert.Contains(ClaimTypes.WindowsAccountName, discovery.GetProperty("claims_supported").EnumerateArray().Select(claim => claim.GetString()));
            var jwks = await anonymous.GetStringAsync(discovery.GetProperty("jwks_uri").GetString(), TestContext.Current.CancellationToken);
            Assert.Contains(new JsonWebKeySet(jwks).Keys, key => key.Kid == keyId);

                using var windows = WindowsClient(address, out _);
            var (code, verifier) = await AuthorizeAsync(windows, "/connect/authorize", ClientId, RedirectUri, "openid profile email roles");
            // A known client still needs its secret; the standalone server's wildcard/open-client
            // policy must not leak into this adapter. Failed authentication does not redeem the code.
            foreach (var secret in new[] { "", "wrong-secret" })
            {
                using var rejected = await anonymous.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
                    ["client_id"] = ClientId, ["client_secret"] = secret, ["code_verifier"] = verifier,
                }), TestContext.Current.CancellationToken);
                Assert.False(rejected.IsSuccessStatusCode);
                Assert.Equal("invalid_client", JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("error").GetString());
            }
            using var tokenResponse = await anonymous.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
                ["client_id"] = ClientId, ["client_secret"] = ClientSecret, ["code_verifier"] = verifier,
            }), TestContext.Current.CancellationToken);
            var tokenJson = await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(tokenResponse.IsSuccessStatusCode, tokenJson);
            var tokens = JsonDocument.Parse(tokenJson).RootElement;
            idToken = tokens.GetProperty("id_token").GetString()!;
            var accessToken = tokens.GetProperty("access_token").GetString()!;

            var validated = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = issuer, ValidAudience = ClientId, IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
            });
            Assert.True(validated.IsValid, validated.Exception?.ToString());
            var token = (JsonWebToken)validated.SecurityToken;
            Assert.Equal(keyId, token.Kid);
            var current = WindowsIdentity.GetCurrent();
            Assert.Equal(current.User!.Value, token.Subject);
            Assert.Equal("nonce-1", token.GetClaim("nonce").Value);
            Assert.False(string.IsNullOrWhiteSpace(token.GetClaim(ClaimTypes.Name).Value));
            Assert.Equal(current.Name, token.GetClaim(ClaimTypes.WindowsAccountName).Value, ignoreCase: true);
            var email = token.GetClaim(ClaimTypes.Email).Value;
            if (current.Name.StartsWith(Environment.MachineName + "\\", StringComparison.OrdinalIgnoreCase))
                Assert.Equal($"{Environment.UserName}@localhost", email, ignoreCase: true);
            else Assert.False(string.IsNullOrWhiteSpace(email));
            // Groups are filtered by EmbeddedIdentityServer:Groups and sent without a domain prefix.
            var roles = token.Claims.Where(claim => claim.Type == ClaimTypes.Role).Select(claim => claim.Value).ToArray();
            if (current.HasClaim(ClaimTypes.GroupSid, "S-1-5-113")) Assert.Empty(roles);
            Assert.DoesNotContain("Everyone", roles);
            Assert.All(roles, role => Assert.Contains(role, new[] { "Authenticated Users", "Users" }));

            // Only exact, registered redirect addresses and clients of this server are served here.
            var (badRedirect, _, _) = await AuthorizeResponseAsync(windows, "/connect/authorize", ClientId, "https://client.example/other", "openid");
            Assert.Equal(HttpStatusCode.BadRequest, badRedirect);
            var (unknown, _, _) = await AuthorizeResponseAsync(windows, "/connect/authorize", "not-registered", RedirectUri, "openid");
            Assert.Equal(HttpStatusCode.BadRequest, unknown);
            var backOfficeCallback = address + "/umbraco/oauth_complete";
            // Umbraco registers its backoffice client's callback on the first backoffice request.
            await AuthorizeResponseAsync(anonymous, "/umbraco/management/api/v1/security/back-office/authorize", Constants.OAuthClientIds.BackOffice, backOfficeCallback, "offline_access");
            var (umbraco, _, umbracoBody) = await AuthorizeResponseAsync(windows, "/connect/authorize", Constants.OAuthClientIds.BackOffice, backOfficeCallback, "openid");
            Assert.Equal(HttpStatusCode.BadRequest, umbraco);
            Assert.Contains("not registered with this site's identity server", umbracoBody);
            // ...and this server's clients cannot use Umbraco's backoffice endpoint either.
            var (crossed, _, crossedBody) = await AuthorizeResponseAsync(windows, "/umbraco/management/api/v1/security/back-office/authorize", ClientId, RedirectUri, "openid");
            Assert.Equal(HttpStatusCode.BadRequest, crossed);
            Assert.Contains("client_id", crossedBody);

            // An older release may have left an embedded client in Umbraco's application store.
            using (var scope = site.CreateScope())
            {
                var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
                Assert.Null(await applications.FindByClientIdAsync(ClientId, TestContext.Current.CancellationToken));
                await applications.CreateAsync(new OpenIddictApplicationDescriptor
                {
                    ClientId = ClientId,
                    ClientType = OpenIddictConstants.ClientTypes.Public,
                    DisplayName = EmbeddedIdentityServerDefaults.ClientDisplayNamePrefix + ClientId,
                    RedirectUris = { new Uri(RedirectUri) },
                    Permissions =
                    {
                        OpenIddictConstants.Permissions.Endpoints.Authorization,
                        OpenIddictConstants.Permissions.Endpoints.Token,
                        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                        OpenIddictConstants.Permissions.ResponseTypes.Code,
                    },
                }, TestContext.Current.CancellationToken);
            }
            var (legacyStatus, _, legacyBody) = await AuthorizeResponseAsync(windows,
                "/umbraco/management/api/v1/security/back-office/authorize", ClientId, RedirectUri, "openid");
            Assert.Equal(HttpStatusCode.BadRequest, legacyStatus);
            Assert.Contains("may only use the /connect/authorize endpoint", legacyBody);

            // Access tokens issued here are for other applications, never for Umbraco's APIs.
            using var api = new HttpRequestMessage(HttpMethod.Get, "/umbraco/management/api/v1/user/current");
            api.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var apiResponse = await anonymous.SendAsync(api, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, apiResponse.StatusCode);
            Assert.Contains("invalid_token", apiResponse.Headers.WwwAuthenticate.ToString());
        }

        // A restart on the same data folder signs with the same key, so the earlier token still validates.
        await using (var site = await StartAsync(root, ClientSettings))
        {
            Assert.Equal(keyId, site.Services.GetRequiredService<EmbeddedIdentityServerHost>().ServerOptions.SigningCredentials[0].Key.KeyId);
            using var anonymous = site.CreateClient();
            var jwks = new JsonWebKeySet(await anonymous.GetStringAsync("/.well-known/jwks", TestContext.Current.CancellationToken));
            Assert.Contains(jwks.Keys, key => key.Kid == keyId);
            var validated = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = issuer, ValidAudience = ClientId, IssuerSigningKeys = jwks.GetSigningKeys(),
            });
            Assert.True(validated.IsValid, validated.Exception?.ToString());
        }
    }

    [Fact]
    public async Task The_backoffice_signs_in_through_the_embedded_identity_server()
    {
        var root = Directory.CreateTempSubdirectory("embedded-idsrv-backoffice-").FullName;
        await using var site = await StartAsync(root, new Dictionary<string, string?>
        {
            // IdentityServer:Enabled with an empty Authority is the library default: the embedded server.
            ["Accounts:SeedCurrentWindowsUserAsAdministrator"] = "true",
        });
        var address = Address(site);
        var email = $"{Environment.UserName}@localhost";
        Guid? userKey = null;
        for (var attempt = 0; attempt < 100 && userKey is null; attempt++)
        {
            userKey = site.Services.GetRequiredService<IUserService>().GetByEmail(email)?.Key;
            if (userKey is null) await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.NotNull(userKey);

        using var browser = WindowsClient(address, out _);
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var url = "/umbraco/management/api/v1/security/back-office/authorize" + QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = Constants.OAuthClientIds.BackOffice,
            ["redirect_uri"] = address + "/umbraco/oauth_complete",
            ["response_type"] = "code",
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
            ["identity_provider"] = "Umbraco.Domain",
            ["state"] = "backoffice-state",
        });

        // Follow the redirects as a browser would: Umbraco's authorize endpoint, the OpenID Connect
        // challenge to /connect/authorize, the callback (which redeems the code against this same
        // site), and Umbraco's authorize endpoint again, which completes the backoffice sign-in.
        var visited = new List<string>();
        Uri? location = new(url, UriKind.Relative);
        HttpResponseMessage? response = null;
        for (var hop = 0; hop < 10 && location is not null && !location.ToString().StartsWith(address + "/umbraco/oauth_complete", StringComparison.Ordinal); hop++)
        {
            visited.Add(location.ToString());
            response?.Dispose();
            response = await browser.GetAsync(location, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect || response.StatusCode == HttpStatusCode.Found,
                $"{location} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}\nVisited: {string.Join("\n", visited)}");
            location = response.Headers.Location is { } next && !next.IsAbsoluteUri ? new Uri(new Uri(address), next) : response.Headers.Location;
        }
        response?.Dispose();
        Assert.NotNull(location);
        Assert.StartsWith(address + "/umbraco/oauth_complete", location.ToString());
        Assert.Contains("state=backoffice-state", location.Query);
        Assert.Contains(visited, hop => hop.StartsWith(address + "/connect/authorize", StringComparison.Ordinal));
        Assert.Contains(visited, hop => hop.StartsWith(address + "/signin-oidc/backoffice", StringComparison.Ordinal));

        // The administrator is now linked to the Windows account by its SID.
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var login = Assert.Single(site.Services.GetRequiredService<IExternalLoginWithKeyService>().Find("Umbraco.Domain", sid));
        Assert.Equal(userKey.ToString(), login.UserId, ignoreCase: true);

        // Umbraco's own token endpoint and API are unaffected: the backoffice app exchanges the
        // redacted code and calls the management API, both tokens travelling in cookies.
        using var tokenResponse = await browser.PostAsync("/umbraco/management/api/v1/security/back-office/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = Constants.OAuthClientIds.BackOffice,
            ["redirect_uri"] = address + "/umbraco/oauth_complete",
            ["code"] = QueryHelpers.ParseQuery(location.Query)["code"].ToString(),
            ["code_verifier"] = verifier,
        }), TestContext.Current.CancellationToken);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(tokenResponse.IsSuccessStatusCode, tokenBody);
        using var current = new HttpRequestMessage(HttpMethod.Get, "/umbraco/management/api/v1/user/current");
        current.Headers.Authorization = new AuthenticationHeaderValue("Bearer", JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString());
        using var currentResponse = await browser.SendAsync(current, TestContext.Current.CancellationToken);
        var currentBody = await currentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(currentResponse.IsSuccessStatusCode, currentBody);
        Assert.Contains(email, currentBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_endpoints_follow_the_virtual_path()
    {
        var root = Directory.CreateTempSubdirectory("embedded-idsrv-wing-").FullName;
        await using var site = await StartAsync(root, new Dictionary<string, string?>(ClientSettings)
        {
            ["Umbraco:CMS:Hosting:ApplicationVirtualPath"] = "/wing",
        });
        var address = Address(site);
        using var anonymous = site.CreateClient();
        var discovery = await GetJsonAsync(anonymous, "/wing/.well-known/openid-configuration");
        Assert.Equal(address + "/wing/connect/authorize", discovery.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(address + "/wing/connect/token", discovery.GetProperty("token_endpoint").GetString());
        var issuer = discovery.GetProperty("issuer").GetString()!;

        using var windows = WindowsClient(address, out _);
        var (code, verifier) = await AuthorizeAsync(windows, "/wing/connect/authorize", ClientId, RedirectUri, "openid");
        using var tokenResponse = await anonymous.PostAsync("/wing/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId, ["client_secret"] = ClientSecret, ["code_verifier"] = verifier,
        }), TestContext.Current.CancellationToken);
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(tokenResponse.IsSuccessStatusCode, tokenJson);
        var idToken = new JsonWebToken(JsonDocument.Parse(tokenJson).RootElement.GetProperty("id_token").GetString());
        Assert.Equal(issuer, idToken.Issuer);
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, idToken.Subject);
    }

    [Fact]
    public async Task Switched_off_it_leaves_Umbracos_server_as_it_was()
    {
        var root = Directory.CreateTempSubdirectory("embedded-idsrv-disabled-").FullName;
        await using var site = await StartAsync(root, new Dictionary<string, string?>
        {
            ["IdentityServer:Enabled"] = "false",
            ["EmbeddedIdentityServer:Enabled"] = "false",
        });
        using var anonymous = site.CreateClient();
        var discovery = await GetJsonAsync(anonymous, "/.well-known/openid-configuration");
        Assert.DoesNotContain("/connect/", discovery.GetProperty("authorization_endpoint").GetString());
        Assert.DoesNotContain("/connect/", discovery.GetProperty("token_endpoint").GetString());
        // No endpoint is mapped: the path falls through to content routing.
        Assert.Null(site.Services.GetService<EmbeddedIdentityServerHost>());
        using var authorize = await anonymous.GetAsync("/connect/authorize?client_id=anything", TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.BadRequest, authorize.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, authorize.StatusCode);
        Assert.False(File.Exists(Path.Combine(site.DataRoot, "keys", "identity-server", "signing-key.bin")));
    }

    [Fact]
    public void Settings_are_validated_before_the_site_starts()
    {
        var validator = new EmbeddedIdentityServerSettingsValidator(Options.Create(new IdentityServerSettings { Enabled = true, ClientId = "umbraco-backoffice" }));
        var result = validator.Validate(null, new EmbeddedIdentityServerSettings
        {
            Groups = ["(unclosed"],
            Clients =
            [
                new EmbeddedIdentityServerClient { ClientId = "app", RedirectUris = ["https://app.example/callback"] },
                new EmbeddedIdentityServerClient { ClientId = "APP", RedirectUris = ["/relative"] },
                new EmbeddedIdentityServerClient { ClientId = "umbraco-back-office", RedirectUris = ["https://app.example/#fragment"] },
                new EmbeddedIdentityServerClient { ClientId = "umbraco-backoffice" },
            ],
        });
        Assert.True(result.Failed);
        var failures = string.Join("\n", result.Failures!);
        Assert.Contains("(unclosed", failures);
        Assert.Contains("lists 'APP' more than once", failures);
        Assert.Contains("'/relative' must be an absolute", failures);
        Assert.Contains("belongs to Umbraco", failures);
        Assert.Contains("this site's own IdentityServer:ClientId", failures);
        Assert.Contains("needs at least one RedirectUris entry", failures);

        Assert.True(validator.Validate(null, new EmbeddedIdentityServerSettings
        {
            Clients = [new EmbeddedIdentityServerClient { ClientId = "app", RedirectUris = ["https://app.example/callback"] }],
        }).Succeeded);
    }

    [Fact]
    public void Legacy_key_and_directory_overrides_require_explicit_migration()
    {
        var validator = new EmbeddedIdentityServerSettingsValidator(Options.Create(new IdentityServerSettings()));
        var result = validator.Validate(null, new EmbeddedIdentityServerSettings
        {
            KeyPath = "old-signing.key", UseActiveDirectory = false,
        });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("EmbeddedIdentityServer:DataPath"));
        Assert.Contains(result.Failures!, failure => failure.Contains("EmbeddedIdentityServer:UseActiveDirectory"));
    }

    [Fact]
    public void An_empty_authority_requires_the_embedded_identity_server()
    {
        var root = Directory.CreateTempSubdirectory("embedded-idsrv-off-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root, ApplicationName = typeof(Program).Assembly.GetName().Name, EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InstanceRoot"] = Path.Combine(root, "instance"),
            ["EmbeddedIdentityServer:Enabled"] = "false",
        });
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddUmbracoAppHost());
        Assert.Contains("EmbeddedIdentityServer:Enabled is false", error.Message);
        // Release the instance lease the failed registration took.
        foreach (var instance in builder.Services.Where(service => service.ServiceType == typeof(UmbracoAppHost.Hosting.AppHostInstance)))
            (instance.ImplementationFactory?.Invoke(null!) as IDisposable)?.Dispose();
    }

    /// <summary>A site on <paramref name="root"/>, so a second one on the same root is a restart of the first.</summary>
    private static Task<TestSite> StartAsync(string root, IDictionary<string, string?> settings)
    {
        var options = new TestSiteOptions { Application = typeof(Program).Assembly, Root = root, EnvironmentName = "Production", FirstBootGroup = "Settings" };
        foreach (var (key, value) in settings) options.Settings[key] = value;
        return TestSite.StartAsync(options);
    }

    /// <summary>The site's address as the identity server writes it into its discovery document, without a trailing slash.</summary>
    private static string Address(TestSite site) => site.Address.GetLeftPart(UriPartial.Authority);

    /// <summary>A browser stand-in: keeps cookies, does not follow redirects, answers Negotiate as the current Windows user.</summary>
    private static HttpClient WindowsClient(string address, out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClient(new HttpClientHandler { UseDefaultCredentials = true, AllowAutoRedirect = false, CookieContainer = cookies })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>Runs an authorization request that must succeed and returns the code with its PKCE verifier.</summary>
    private static async Task<(string Code, string Verifier)> AuthorizeAsync(HttpClient client, string path, string clientId, string redirectUri, string scope)
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var (status, location, body) = await AuthorizeResponseAsync(client, path, clientId, redirectUri, scope, verifier);
        Assert.True(status == HttpStatusCode.Redirect || status == HttpStatusCode.Found, $"Authorize answered {(int)status}: {body}");
        Assert.StartsWith(redirectUri + "?", location!.ToString());
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("state-1", query["state"].ToString());
        return (query["code"].ToString(), verifier);
    }

    private static async Task<(HttpStatusCode Status, Uri? Location, string Body)> AuthorizeResponseAsync(
        HttpClient client, string path, string clientId, string redirectUri, string scope, string? verifier = null)
    {
        using var response = await client.GetAsync(AuthorizationUrl(path, clientId, redirectUri, scope, verifier));
        return (response.StatusCode, response.Headers.Location, await response.Content.ReadAsStringAsync());
    }

    private static string AuthorizationUrl(string path, string clientId, string redirectUri, string scope, string? verifier = null)
        => path + QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = "state-1",
            ["nonce"] = "nonce-1",
            ["code_challenge"] = Challenge(verifier ?? Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32))),
            ["code_challenge_method"] = "S256",
        });

    private static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
