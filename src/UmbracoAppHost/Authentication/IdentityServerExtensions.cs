using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Umbraco.Cms.Api.Management.Security;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.Security;
using Umbraco.Extensions;
using UmbracoAppHost.IdentityServer;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Registers an OpenID Connect identity server as a login provider for the backoffice and for
/// members: the site's own embedded identity server when <see cref="IdentityServerSettings.Authority"/>
/// is empty, otherwise an external one. The standard OpenID Connect handler validates the protocol
/// and passes the external principal to Umbraco, which owns account creation, linking and sign-in.
/// </summary>
public static class IdentityServerExtensions
{
    private const string LoggerName = "UmbracoAppHost.Authentication.IdentityServer";

    /// <summary>Adds the provider when <see cref="IdentityServerSettings.Enabled"/>. Called by <see cref="SsoExtensions.AddSingleSignOn"/>.</summary>
    public static IUmbracoBuilder AddIdentityServerLogin(this IUmbracoBuilder builder, IdentityServerSettings settings)
    {
        if (!settings.Enabled) return builder;
        if (string.IsNullOrWhiteSpace(settings.ClientId))
            throw new InvalidOperationException("IdentityServer:ClientId is required when IdentityServer:Enabled is true.");
        if (settings.UsesEmbeddedIdentityServer && !EmbeddedIdentityServerSettings.IsEnabled(builder.Config))
            throw new InvalidOperationException("IdentityServer:Authority is empty, which signs in through this site's embedded identity server, but EmbeddedIdentityServer:Enabled is false. Enable the embedded identity server, or set IdentityServer:Authority to an external identity server.");

        // The callback paths must never be routed as content.
        builder.Services.PostConfigure<GlobalSettings>(global =>
            global.ReservedPaths = global.ReservedPaths.EnsureEndsWith(',') + "~" + settings.CallbackPath.EnsureEndsWith('/'));
        if (settings.UsesEmbeddedIdentityServer)
            builder.Services.TryAddSingleton<EmbeddedIdentityServerConfigurationManager>();

        if (settings.BackOfficeEnabled)
        {
            builder.AddBackOfficeExternalLogins(logins => logins.AddBackOfficeLogin(
                authentication => authentication.AddOpenIdConnect(
                    settings.BackOfficeScheme,
                    settings.Name,
                    options => Configure(options, settings, "backoffice")),
                providerOptions =>
                {
                    providerOptions.DenyLocalLogin = settings.DenyLocalLogin;
                    providerOptions.AutoLinkOptions = new ExternalSignInAutoLinkOptions(
                        autoLinkExternalAccount: settings.BackOfficeAutoLink.AutoLinkExternalAccount,
                        defaultUserGroups: settings.BackOfficeAutoLink.DefaultUserGroups,
                        defaultCulture: settings.BackOfficeAutoLink.DefaultCulture,
                        allowManualLinking: settings.BackOfficeAutoLink.AllowManualLinking);
                }));
            UseEmbeddedMetadata(builder, settings, settings.BackOfficeScheme);
            UseFallbackHandler(builder, settings.BackOfficeScheme);
        }

        if (settings.MemberEnabled)
        {
            builder.AddMemberExternalLogins(logins => logins.AddMemberLogin(
                authentication => authentication.AddOpenIdConnect(
                    settings.MemberScheme,
                    settings.Name,
                    options => Configure(options, settings, "member")),
                providerOptions =>
                {
                    providerOptions.AutoLinkOptions = new MemberExternalSignInAutoLinkOptions(
                        autoLinkExternalAccount: settings.MemberAutoLink.AutoLinkExternalAccount,
                        defaultIsApproved: settings.MemberAutoLink.DefaultIsApproved,
                        defaultMemberTypeAlias: settings.MemberAutoLink.DefaultMemberTypeAlias,
                        defaultCulture: settings.MemberAutoLink.DefaultCulture,
                        defaultMemberGroups: settings.MemberAutoLink.DefaultMemberGroups)
                    {
                        ExternalOnly = settings.MemberAutoLink.ExternalOnly,
                    };
                }));
            UseEmbeddedMetadata(builder, settings, settings.MemberScheme);
            UseFallbackHandler(builder, settings.MemberScheme);
        }

        return builder;
    }

    private static void UseFallbackHandler(IUmbracoBuilder builder, string scheme)
    {
        // Keep the framework's OpenID Connect option setup and replace only our registered
        // handler. Its events cover callback failures, but not a failed discovery challenge.
        builder.Services.TryAddTransient<IdentityServerLoginHandler>();
        builder.Services.Configure<AuthenticationOptions>(options =>
            options.Schemes.Single(candidate => candidate.Name == scheme).HandlerType = typeof(IdentityServerLoginHandler));
    }

    /// <summary>
    /// With the embedded identity server there is no authority to point the handler at: its
    /// metadata is resolved per request from the server in this process (see
    /// <see cref="EmbeddedIdentityServerConfigurationManager"/>), so neither <c>Authority</c> nor
    /// <c>MetadataAddress</c> is set and <c>RequireHttpsMetadata</c> has nothing to check.
    /// </summary>
    private static void UseEmbeddedMetadata(IUmbracoBuilder builder, IdentityServerSettings settings, string scheme)
    {
        if (!settings.UsesEmbeddedIdentityServer) return;
        builder.Services.AddOptions<OpenIdConnectOptions>(scheme)
            .Configure<EmbeddedIdentityServerConfigurationManager>((options, metadata) => options.ConfigurationManager = metadata);
    }

    private static void Configure(OpenIdConnectOptions options, IdentityServerSettings settings, string audience)
    {
        if (!settings.UsesEmbeddedIdentityServer) options.Authority = settings.Authority;
        options.ClientId = settings.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        // A GET callback with Lax cookies: browsers send Lax cookies on a top-level GET
        // navigation even from another site, so the identity server may live anywhere and this
        // site may even run over plain HTTP. The handler's default (form_post with SameSite=None)
        // needs the Secure attribute, which an HTTP site cannot set, and then only a same-site
        // identity server (a sub-application, or the same host on another port) would work.
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        // Lax cookies need no Secure attribute, and the handler's default (always Secure) is never
        // sent back over plain HTTP, which fails every sign-in there with "Correlation failed".
        // Over HTTPS they stay Secure.
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.CallbackPath = $"{settings.CallbackPath}/{audience}";
        options.SignedOutCallbackPath = $"{settings.CallbackPath}/{audience}/signed-out";
        options.RemoteSignOutPath = $"{settings.CallbackPath}/{audience}/remote-signout";
        options.Scope.Clear();
        foreach (var scope in settings.EffectiveScopes) options.Scope.Add(scope);
        options.GetClaimsFromUserInfoEndpoint = settings.GetClaimsFromUserInfoEndpoint;
        options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
        options.SaveTokens = false;
        options.TokenValidationParameters.NameClaimType = ClaimTypes.Name;
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        // Remote servers may send the standard OIDC name instead of the .NET claim URI
        // emitted by IdentityServer.Core. Keep all claims and expose that name to Umbraco.
        options.TokenValidationParameters.NameClaimTypeRetriever = (token, issuer) => token switch
        {
            JsonWebToken jwt when jwt.TryGetClaim("name", out _) => "name",
            JwtSecurityToken jwt when jwt.Payload.ContainsKey("name") => "name",
            _ => ClaimTypes.Name,
        };
        // Also map a name returned only by the optional userinfo endpoint.
        options.ClaimActions.MapUniqueJsonKey(ClaimTypes.Name, "name");
        if (settings.AcceptAnyServerCertificate)
        {
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
        }
        options.Events.OnRemoteFailure = OnRemoteFailureAsync;
    }

    /// <summary>Shows protocol failures (an identity server error, a lost correlation cookie) the same way as a refusal.</summary>
    private static async Task OnRemoteFailureAsync(RemoteFailureContext context)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerName);
        var reason = context.Failure?.Message ?? "The identity server did not complete the sign-in.";
        logger.LogWarning(context.Failure, "Identity server sign-in via {Scheme} failed: {Reason}", context.Scheme.Name, reason);
        context.HandleResponse();
        await SsoRefusal.WriteAsync(context.HttpContext, context.Scheme.Name, context.Scheme.DisplayName ?? "Identity server", context.Properties, reason, "failed");
    }
}
