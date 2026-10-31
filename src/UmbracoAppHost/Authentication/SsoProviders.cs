using System.Text.Json;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Infrastructure.Manifest;

namespace UmbracoAppHost.Authentication;

/// <summary>One sign-in provider as the login screens see it.</summary>
/// <param name="Suffix">The part after Umbraco's scheme prefixes (<c>Domain</c>, for example).</param>
/// <param name="Available">
/// Whether the provider can sign someone in where this site runs. The embedded identity server
/// needs Windows authentication (<see cref="WindowsAuthenticationAvailability"/>);
/// an external identity server authenticates people itself.
/// </param>
public sealed record SsoProvider(string Suffix, string DisplayName, string Icon, bool BackOffice, bool Member, bool Available = true)
{
    public string BackOfficeScheme => Constants.Security.BackOfficeExternalAuthenticationTypePrefix + Suffix;
    public string MemberScheme => Constants.Security.MemberExternalAuthenticationTypePrefix + Suffix;
}

/// <summary>
/// The enabled providers, in the order the login screens list them. <see cref="All"/>,
/// <see cref="BackOffice"/> and <see cref="Member"/> are every configured provider, which is what
/// account linking and lookups span; <see cref="BackOfficeLogins"/> and <see cref="MemberLogins"/>
/// are the ones that can sign someone in where this site runs, which is what the login screens offer.
/// </summary>
public sealed class SsoProviderCatalog
{
    public SsoProviderCatalog(IOptions<IdentityServerSettings> identityServer,
        WindowsAuthenticationAvailability? windowsAuthentication = null)
    {
        var available = windowsAuthentication?.IsAvailable ?? true;
        var providers = new List<SsoProvider>();
        if (identityServer.Value.Enabled)
            providers.Add(new SsoProvider(identityServer.Value.SchemeSuffix, identityServer.Value.Name, "icon-lock",
                identityServer.Value.BackOfficeEnabled, identityServer.Value.MemberEnabled,
                // An external identity server authenticates people itself; the embedded one does it here.
                available || !identityServer.Value.UsesEmbeddedIdentityServer));
        All = providers;
    }

    public IReadOnlyList<SsoProvider> All { get; }

    public IEnumerable<SsoProvider> BackOffice => All.Where(provider => provider.BackOffice);

    public IEnumerable<SsoProvider> Member => All.Where(provider => provider.Member);

    /// <summary>The backoffice providers the login screen offers and may start automatically.</summary>
    public IEnumerable<SsoProvider> BackOfficeLogins => BackOffice.Where(provider => provider.Available);

    /// <summary>The member providers the sign-in views offer, one of which may start automatically.</summary>
    public IEnumerable<SsoProvider> MemberLogins => Member.Where(provider => provider.Available);
}

/// <summary>
/// Supplies the backoffice half of each provider from code. Since Umbraco 14 the login screen
/// only shows providers declared by an <c>authProvider</c> extension whose
/// <c>forProviderName</c> matches the scheme, and a manifest built here follows the configured
/// names and labels instead of needing a file per site.
/// </summary>
public sealed class SsoPackageManifestReader(
    SsoProviderCatalog catalog,
    IOptions<BackOfficeLoginSettings> login,
    IOptions<HostingSettings> hosting,
    IOptions<IdentityServerSettings> identityServer) : IPackageManifestReader
{
    public Task<IEnumerable<PackageManifest>> ReadPackageManifestsAsync()
    {
        var autoRedirectScheme = login.Value.GetAutoRedirectScheme(hosting.Value.Debug, identityServer.Value);
        var extensions = catalog.BackOfficeLogins.Select(provider => (object)JsonSerializer.SerializeToElement(new
        {
            type = "authProvider",
            alias = "UmbracoAppHost.AuthProvider." + provider.Suffix,
            name = provider.DisplayName + " Auth Provider",
            forProviderName = provider.BackOfficeScheme,
            meta = new
            {
                label = provider.DisplayName,
                defaultView = new { icon = provider.Icon, color = "default", look = "primary" },
                // Initial sign-in navigates directly; Umbraco uses a popup when a session times out.
                behavior = new { autoRedirect = provider.BackOfficeScheme == autoRedirectScheme, popupTarget = "umbracoAuthPopup" },
                linking = new { allowManualLinking = identityServer.Value.BackOfficeAutoLink.AllowManualLinking },
            },
        })).ToArray();

        IEnumerable<PackageManifest> manifests = extensions.Length == 0
            ? []
            : [new PackageManifest
            {
                Id = "UmbracoAppHost.Sso",
                Name = "UmbracoAppHost Single Sign-On",
                Version = "1.0.0",
                AllowPublicAccess = true,
                Extensions = extensions,
            }];
        return Task.FromResult(manifests);
    }
}
