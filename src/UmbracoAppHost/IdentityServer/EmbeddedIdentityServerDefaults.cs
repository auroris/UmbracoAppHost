using Microsoft.AspNetCore.Http.Extensions;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.IdentityServer;

/// <summary>Names and addresses the embedded identity server shares between its parts.</summary>
public static class EmbeddedIdentityServerDefaults
{
    /// <summary>
    /// The display-name prefix used by former releases for embedded clients in Umbraco's store.
    /// The legacy guard still recognises these records; the shared server creates no new ones.
    /// </summary>
    public const string ClientDisplayNamePrefix = "UmbracoAppHost identity server client: ";

    /// <summary>The authorization endpoint, relative to the site (and its path base), as OpenIddict registers it.</summary>
    public const string AuthorizationEndpoint = "connect/authorize";

    /// <summary>The token endpoint, relative to the site (and its path base), as OpenIddict registers it.</summary>
    public const string TokenEndpoint = "connect/token";

    /// <summary>A Negotiate failure captured before the validated authorization action runs.</summary>
    internal static readonly object WindowsAuthenticationFailureItem = new();

    internal static bool IsAuthorizationEndpoint(HttpRequest request)
        => request.Path.Equals("/" + AuthorizationEndpoint, StringComparison.OrdinalIgnoreCase);

    /// <summary>The path segment both endpoints live under, reserved from content routing.</summary>
    public static readonly PathString EndpointRoot = "/connect";

    /// <summary>
    /// Whether this site signs in through its own embedded identity server, so that its client
    /// (<c>IdentityServer:ClientId</c>) is allowed there: the provider is on for at least one
    /// audience and has no external authority.
    /// </summary>
    public static bool SignsInThroughEmbeddedServer(IdentityServerSettings site)
        => site.Enabled && site.UsesEmbeddedIdentityServer && (site.BackOfficeEnabled || site.MemberEnabled);

    /// <summary>Whether a request addresses one of the embedded identity server's endpoints.</summary>
    public static bool IsEmbeddedEndpoint(HttpRequest? request)
        => request is not null && (request.Path.StartsWithSegments(EndpointRoot, StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals("/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals("/.well-known/jwks", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The site's base address for a request, built exactly as OpenIddict builds the base URI it
    /// derives the issuer and endpoint addresses from: scheme, host (or <c>localhost</c>) and path base.
    /// </summary>
    public static Uri BaseUri(HttpRequest request)
        => new(UriHelper.BuildAbsolute(request.Scheme, request.Host.HasValue ? request.Host : new HostString("localhost"), request.PathBase));

    /// <summary>Resolves a site-relative endpoint against a base address, keeping the path base.</summary>
    public static Uri Endpoint(Uri baseUri, string relative)
        => new(baseUri.AbsolutePath.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/"), relative);
}
