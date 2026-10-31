using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using UmbracoAppHost.Authentication;
using UmbracoConstants = Umbraco.Cms.Core.Constants;

namespace UmbracoAppHost.IdentityServer;

/// <summary>Another application that signs people in through this site's embedded identity server.</summary>
public sealed class EmbeddedIdentityServerClient
{
    public string ClientId { get; set; } = "";

    /// <summary>
    /// Makes the client confidential: it must present this secret at the token endpoint. Empty
    /// registers a public client. Both kinds must use PKCE, which the server requires of everyone.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>An optional label for the configured client.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The exact callback addresses the client may receive codes on. No wildcards, no host-only matching.</summary>
    public string[] RedirectUris { get; set; } = [];

    /// <summary>Retained for configuration compatibility. The shared server offers no sign-out endpoint.</summary>
    public string[] PostLogoutRedirectUris { get; set; } = [];
}

/// <summary>
/// Settings for the identity server this site embeds, bound from the <c>EmbeddedIdentityServer</c>
/// section. IdentityServer.Core supplies Windows authentication claims and issues OpenID Connect
/// tokens. This site signs in through it
/// when <c>IdentityServer:Authority</c> is empty; other applications are listed in <see cref="Clients"/>.
/// </summary>
/// <remarks>
/// There is deliberately no issuer setting: the issuer follows each request's scheme, host and path
/// base, as Umbraco's own OpenIddict endpoints do, so the site keeps working under every host name
/// it answers to. Behind a reverse proxy, configure forwarded headers so the public address is seen.
/// Array properties have no code defaults on purpose: the configuration binder appends configured
/// elements to a pre-populated array.
/// </remarks>
public sealed class EmbeddedIdentityServerSettings
{
    public const string SectionName = "EmbeddedIdentityServer";

    /// <summary>Serves <c>/connect/authorize</c> and <c>/connect/token</c>. On by default: the platform ships with its identity server.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Other applications allowed to sign in here.</summary>
    public EmbeddedIdentityServerClient[] Clients { get; set; } = [];

    /// <summary>
    /// Case-insensitive .NET regular expressions selecting direct Active Directory group names.
    /// IdentityServer.Core sends no roles when empty; local machine accounts have no directory roles.
    /// </summary>
    public string[] Groups { get; set; } = [];

    /// <summary>Legacy setting. The shared server always reads directory profiles for domain accounts.</summary>
    public bool? UseActiveDirectory { get; set; }

    /// <summary>Keep signing and encryption keys across restarts. Off generates fresh keys per start.</summary>
    public bool PersistKeys { get; set; } = true;

    /// <summary>
    /// Legacy setting. Use <see cref="DataPath"/> for the shared server's signing and encryption keys.
    /// </summary>
    public string? KeyPath { get; set; }

    /// <summary>
    /// Directory for IdentityServer.Core's machine-protected signing and encryption keys. Defaults
    /// to keys/identity-server under the instance data folder; relative paths use the content root.
    /// </summary>
    public string? DataPath { get; set; }

    /// <summary>Lifetime of access tokens issued to the embedded server's clients. Unset leaves the server default.</summary>
    public TimeSpan? AccessTokenLifetime { get; set; }

    /// <summary>Lifetime of identity tokens issued to the embedded server's clients. Unset leaves the server default.</summary>
    public TimeSpan? IdentityTokenLifetime { get; set; }

    /// <summary>Lifetime of authorization codes issued to the embedded server's clients. Unset leaves the server default.</summary>
    public TimeSpan? AuthorizationCodeLifetime { get; set; }

    /// <summary>Whether the embedded identity server is switched on in a configuration, before options exist.</summary>
    public static bool IsEnabled(IConfiguration configuration)
        => configuration.GetSection(SectionName).GetValue(nameof(Enabled), true);
}

/// <summary>
/// Rejects configurations the server could not honour safely, at startup rather than at the first
/// sign-in: patterns that do not compile, clients that collide with each other, with this site's own
/// client or with Umbraco's, and redirect addresses that are not exact absolute URLs.
/// </summary>
public sealed class EmbeddedIdentityServerSettingsValidator(IOptions<IdentityServerSettings> site)
    : IValidateOptions<EmbeddedIdentityServerSettings>
{
    private static readonly string[] UmbracoClientIds =
    [
        UmbracoConstants.OAuthClientIds.BackOffice,
        UmbracoConstants.OAuthClientIds.Member,
        UmbracoConstants.OAuthClientIds.OpenApiUi,
        UmbracoConstants.OAuthClientIds.Postman,
    ];

    public ValidateOptionsResult Validate(string? name, EmbeddedIdentityServerSettings settings)
    {
        if (!settings.Enabled) return ValidateOptionsResult.Success;

        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.KeyPath))
            errors.Add("EmbeddedIdentityServer:KeyPath is no longer supported. Use EmbeddedIdentityServer:DataPath for IdentityServer.Core's key directory; old Data Protection signing keys cannot be read by the shared server.");
        if (settings.UseActiveDirectory is not null)
            errors.Add("EmbeddedIdentityServer:UseActiveDirectory is no longer supported. IdentityServer.Core reads Active Directory for domain accounts and skips it for local machine accounts. Remove this setting.");
        foreach (var pattern in settings.Groups)
        {
            try { _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (ArgumentException exception) { errors.Add($"EmbeddedIdentityServer:Groups pattern '{pattern}' is not a valid regular expression: {exception.Message}"); }
        }

        var selfClientId = EmbeddedIdentityServerDefaults.SignsInThroughEmbeddedServer(site.Value) ? site.Value.ClientId : null;
        if (selfClientId is not null && UmbracoClientIds.Contains(selfClientId, StringComparer.OrdinalIgnoreCase))
            errors.Add($"IdentityServer:ClientId '{selfClientId}' belongs to Umbraco; choose another client id for this site.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in settings.Clients)
        {
            var id = client.ClientId?.Trim() ?? "";
            if (id.Length == 0) { errors.Add("Every EmbeddedIdentityServer:Clients entry needs a ClientId."); continue; }
            if (!seen.Add(id)) errors.Add($"EmbeddedIdentityServer:Clients lists '{id}' more than once.");
            if (UmbracoClientIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                errors.Add($"EmbeddedIdentityServer client '{id}' uses a client id that belongs to Umbraco.");
            if (string.Equals(id, selfClientId, StringComparison.OrdinalIgnoreCase))
                errors.Add($"EmbeddedIdentityServer client '{id}' uses this site's own IdentityServer:ClientId.");
            if (client.RedirectUris.Length == 0)
                errors.Add($"EmbeddedIdentityServer client '{id}' needs at least one RedirectUris entry.");
            foreach (var uri in client.RedirectUris) CheckUri(id, "RedirectUris", uri, errors);
            foreach (var uri in client.PostLogoutRedirectUris) CheckUri(id, "PostLogoutRedirectUris", uri, errors);
        }

        if (settings.AccessTokenLifetime <= TimeSpan.Zero || settings.IdentityTokenLifetime <= TimeSpan.Zero || settings.AuthorizationCodeLifetime <= TimeSpan.Zero)
            errors.Add("EmbeddedIdentityServer token lifetimes must be positive when set.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckUri(string clientId, string setting, string value, List<string> errors)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.Fragment))
            errors.Add($"EmbeddedIdentityServer client '{clientId}' {setting} entry '{value}' must be an absolute http or https address without a fragment.");
    }
}
