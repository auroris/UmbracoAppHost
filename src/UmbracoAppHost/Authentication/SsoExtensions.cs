using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Infrastructure.Manifest;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Notifications;
using UmbracoAppHost.IdentityServer;
using AppBlocks.Authentication;

namespace UmbracoAppHost.Authentication;

/// <summary>Registers account services and the OpenID Connect sign-in provider.</summary>
public static class SsoExtensions
{
    public static IUmbracoBuilder AddSingleSignOn(this IUmbracoBuilder builder, IConfiguration configuration)
    {
        builder.Services.AddOptions<AccountsSettings>()
            .Bind(configuration.GetSection(AccountsSettings.SectionName))
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AccountsSettings>, AccountsSettingsValidator>());
        builder.Services.Configure<IdentityServerSettings>(configuration.GetSection(IdentityServerSettings.SectionName));
        builder.Services.AddOptions<BackOfficeLoginSettings>()
            .Bind(configuration.GetSection(BackOfficeLoginSettings.SectionName))
            .Validate(settings => Enum.IsDefined(settings.DefaultFlow)
                && (settings.DebugFlow is null || Enum.IsDefined(settings.DebugFlow.Value)),
                "BackOfficeLogin flows must be Auto, Form, IdentityServer, or Debug.")
            .ValidateOnStart();
        builder.Services.AddScoped<AccountProvisioning>();
        builder.Services.AddSingleton<SsoProviderCatalog>();
        // Whether Windows authentication works here decides which providers the login screens offer.
        builder.Services.TryAddSingleton<WindowsAuthenticationAvailability>();
        builder.AddNotificationHandler<UmbracoApplicationStartedNotification, WindowsAuthenticationStartupLog>();
        builder.Services.AddScoped<SiteVisitor>();
        builder.Services.AddScoped<ApplicationAccessPolicy>();
        builder.Services.AddSingleton<IPackageManifestReader, SsoPackageManifestReader>();

        // Provider registration happens while services are being built, so the sections are read directly here.
        var identityServer = configuration.GetSection(IdentityServerSettings.SectionName).Get<IdentityServerSettings>() ?? new IdentityServerSettings();
        builder.Services.AddOptions<BackOfficeLoginSettings>()
            .Validate<Microsoft.Extensions.Options.IOptions<HostingSettings>>((settings, hosting) =>
            {
                // Catch an unavailable default provider at startup, with an actionable configuration error.
                settings.GetAutoRedirectScheme(hosting.Value.Debug, identityServer);
                return true;
            });
        return builder
            .AddIdentityServerLogin(identityServer)
            .AddEmbeddedIdentityServer(configuration)
            .AddAccountRetirement(configuration);
    }
}
