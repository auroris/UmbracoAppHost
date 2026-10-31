using System.Runtime.CompilerServices;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;

namespace UmbracoAppHost.IdentityServer;

/// <summary>
/// The OpenID Connect metadata this site's own sign-in uses when it signs in through its embedded
/// identity server (<c>IdentityServer:Authority</c> empty): issuer, endpoints and signing keys,
/// resolved for the current request from the server running in this process.
/// </summary>
/// <remarks>
/// <para>
/// The handler's options are built before Umbraco knows its application URL, and the server derives
/// its issuer from each request's scheme, host and path base, so there is no fixed authority to
/// configure. The issuer and endpoint addresses are therefore derived per request, by the same rule
/// OpenIddict applies when it serves the discovery document and issues the token, which keeps
/// <c>iss</c> validation consistent under every host name and path base.
/// </para>
/// <para>
/// This deliberately does not download <c>/.well-known/openid-configuration</c> from the request's
/// own address. That address comes from the Host header, which a caller controls: fetching it would
/// let anyone who sends a forged Host header point this site at an identity server of their own,
/// with keys of their own, and sign in as anybody. The signing keys come from the server's options
/// instead, so only tokens this process signed are accepted. The code is still redeemed over HTTP,
/// a loopback call from the site to its own token endpoint, which IIS bindings and DNS must allow.
/// </para>
/// </remarks>
public sealed class EmbeddedIdentityServerConfigurationManager(
    IHttpContextAccessor httpContextAccessor,
    EmbeddedIdentityServerHost server) : IConfigurationManager<OpenIdConnectConfiguration>
{
    // Verification needs only the public halves; computed once per options instance.
    private readonly ConditionalWeakTable<OpenIddictServerOptions, IReadOnlyList<SecurityKey>> publicKeys = new();

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        var request = httpContextAccessor.HttpContext?.Request
            ?? throw new InvalidOperationException("The embedded identity server's metadata is resolved per request, and there is no current request.");
        var options = server.ServerOptions;
        var baseUri = EmbeddedIdentityServerDefaults.BaseUri(request);
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = (options.Issuer ?? baseUri).AbsoluteUri,
            AuthorizationEndpoint = EmbeddedIdentityServerDefaults.Endpoint(baseUri, EmbeddedIdentityServerDefaults.AuthorizationEndpoint).AbsoluteUri,
            TokenEndpoint = EmbeddedIdentityServerDefaults.Endpoint(baseUri, EmbeddedIdentityServerDefaults.TokenEndpoint).AbsoluteUri,
        };
        foreach (var key in publicKeys.GetValue(options, PublicKeys)) configuration.SigningKeys.Add(key);
        return Task.FromResult(configuration);
    }

    /// <summary>Nothing is cached that could go stale: every call reads the server's current options.</summary>
    public void RequestRefresh()
    {
    }

    private static IReadOnlyList<SecurityKey> PublicKeys(OpenIddictServerOptions options)
        => options.SigningCredentials
            .Select(credentials => credentials.Key)
            .OfType<AsymmetricSecurityKey>()
            .Select(key => key is RsaSecurityKey rsa
                ? new RsaSecurityKey(rsa.Rsa?.ExportParameters(includePrivateParameters: false) ?? WithoutPrivateParts(rsa.Parameters)) { KeyId = rsa.KeyId }
                : (SecurityKey)key)
            .ToList();

    private static System.Security.Cryptography.RSAParameters WithoutPrivateParts(System.Security.Cryptography.RSAParameters parameters)
        => new() { Modulus = parameters.Modulus, Exponent = parameters.Exponent };
}
