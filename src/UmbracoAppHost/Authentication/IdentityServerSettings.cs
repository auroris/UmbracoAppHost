using Umbraco.Cms.Core;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Settings for signing in through an OpenID Connect identity server, bound from the
/// <c>IdentityServer</c> section: the site's embedded identity server when <see cref="Authority"/>
/// is empty, or an external one such as OpenIddict-WindowsAuth. The keys match the Umbraco 13 site's
/// section so an existing configuration carries over. Umbraco manages external account creation and linking.
/// </summary>
public sealed class IdentityServerSettings
{
    public const string SectionName = "IdentityServer";

    /// <summary>
    /// Registers the provider at all. The library defaults turn it on against the embedded identity
    /// server; a host without them (the <c>IUmbracoBuilder</c> entry point) leaves it off.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Button label on the login screens. Its letters and digits also form the scheme name
    /// (<c>Umbraco.Domain</c> for <c>Domain</c>). Keep this name unchanged in an existing
    /// site's settings so its stored external-login links continue to match.
    /// </summary>
    public string Name { get; set; } = "Identity Server";

    /// <summary>
    /// The external identity server's issuer URL; discovery is read from
    /// <c>/.well-known/openid-configuration</c> under it. Empty signs in through this site's
    /// embedded identity server, which then must be enabled (<c>EmbeddedIdentityServer:Enabled</c>).
    /// </summary>
    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string? ClientSecret { get; set; }

    /// <summary>Scopes to request. Empty means <c>openid profile email roles</c>.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>Path prefix the callbacks use; the audience is appended. Added to Umbraco's reserved paths automatically.</summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    public bool BackOfficeEnabled { get; set; } = true;

    public bool MemberEnabled { get; set; } = true;

    /// <summary>Umbraco's native backoffice account creation and linking options.</summary>
    public BackOfficeAutoLinkSettings BackOfficeAutoLink { get; set; } = new();

    /// <summary>Umbraco's native member account creation and linking options.</summary>
    public MemberAutoLinkSettings MemberAutoLink { get; set; } = new();

    /// <summary>Hides the local username/password form in the backoffice. Keep false in development.</summary>
    public bool DenyLocalLogin { get; set; }

    /// <summary>
    /// Refuse an <c>http://</c> authority. Turn off only for an identity server on a trusted intranet
    /// without TLS. Irrelevant with the embedded identity server, whose metadata is read in process.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Skip certificate validation on the back-channel calls to the identity server (discovery,
    /// keys, token). For an isolated intranet with a self-signed certificate only; never on an
    /// untrusted network.
    /// </summary>
    public bool AcceptAnyServerCertificate { get; set; }

    /// <summary>Ask the userinfo endpoint for claims as well. Off when the identity server puts everything in the id token.</summary>
    public bool GetClaimsFromUserInfoEndpoint { get; set; }

    /// <summary>
    /// The key this site sends in the <c>X-Api-Key</c> header when it searches an external identity
    /// server's directory API (<c>{Authority}/directory/users</c>) to create accounts from the
    /// directory; one of the server's <c>Directory:ApiKeys</c>. Empty authenticates as the site's
    /// own Windows identity (the application pool account) instead. Unused with the embedded
    /// identity server, whose site queries the directory itself.
    /// </summary>
    public string? DirectoryApiKey { get; set; }

    /// <summary>The letters and digits of <see cref="Name"/>, which is what the scheme names are built from.</summary>
    public string SchemeSuffix
    {
        get
        {
            var suffix = new string(Name.Where(char.IsLetterOrDigit).ToArray());
            return suffix.Length > 0 ? suffix : "IdentityServer";
        }
    }

    public string BackOfficeScheme => Constants.Security.BackOfficeExternalAuthenticationTypePrefix + SchemeSuffix;

    public string MemberScheme => Constants.Security.MemberExternalAuthenticationTypePrefix + SchemeSuffix;

    public IReadOnlyList<string> EffectiveScopes => Scopes.Length > 0 ? Scopes : ["openid", "profile", "email", "roles"];

    /// <summary>Whether the site signs in through its own embedded identity server: <see cref="Authority"/> is empty.</summary>
    public bool UsesEmbeddedIdentityServer => string.IsNullOrWhiteSpace(Authority);
}
