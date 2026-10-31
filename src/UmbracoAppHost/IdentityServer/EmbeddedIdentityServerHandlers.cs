using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using UmbracoAppHost.Authentication;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace UmbracoAppHost.IdentityServer;

/// <summary>The host's exact client/callback policy, applied to the shared server's protocol pipeline.</summary>
public sealed class EmbeddedClientRules(IOptions<EmbeddedIdentityServerSettings> settings, IOptions<IdentityServerSettings> site)
    : IOpenIddictServerHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>,
      IOpenIddictServerHandler<OpenIddictServerEvents.ValidateTokenRequestContext>
{
    public ValueTask HandleAsync(OpenIddictServerEvents.ValidateAuthorizationRequestContext context)
    {
        var client = FindClient(context.ClientId);
        if (client is null)
            context.Reject(Errors.InvalidRequest, "The specified client_id is not registered with this site's identity server.");
        else
        {
            var redirects = IsSiteClient(context.ClientId)
                ? SiteRedirects(context.Transaction.GetHttpRequest()!) : client.RedirectUris;
            if (!redirects.Contains(context.RedirectUri, StringComparer.Ordinal))
                context.Reject(Errors.InvalidRequest, "The specified redirect_uri is not registered for this client at this address.");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask HandleAsync(OpenIddictServerEvents.ValidateTokenRequestContext context)
    {
        var client = FindClient(context.ClientId);
        if (client is null || !SecretMatches(client.ClientSecret, context.Request.ClientSecret))
            context.Reject(Errors.InvalidClient, "The client credentials are not valid.");
        return ValueTask.CompletedTask;
    }

    private EmbeddedIdentityServerClient? FindClient(string? id)
        => IsSiteClient(id)
            ? new EmbeddedIdentityServerClient { ClientId = site.Value.ClientId, ClientSecret = site.Value.ClientSecret }
            : settings.Value.Clients.FirstOrDefault(client => string.Equals(client.ClientId.Trim(), id, StringComparison.Ordinal));

    private bool IsSiteClient(string? id)
        => EmbeddedIdentityServerDefaults.SignsInThroughEmbeddedServer(site.Value)
            && string.Equals(id, site.Value.ClientId, StringComparison.Ordinal);

    private IEnumerable<string> SiteRedirects(HttpRequest request)
    {
        // Match the OIDC client's BuildRedirectUri, including the original Host-header casing.
        var callback = $"{request.Scheme}://{request.Host}{request.PathBase}{site.Value.CallbackPath}";
        if (site.Value.BackOfficeEnabled)
            yield return callback + "/backoffice";
        if (site.Value.MemberEnabled)
            yield return callback + "/member";
    }

    private static bool SecretMatches(string? expected, string? actual)
        => string.IsNullOrEmpty(expected) || (actual is not null && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(actual))));
}

/// <summary>
/// Lets the site's login screen recover when Windows sign-in is unavailable. Runs only after
/// OpenIddict validates the client, callback and PKCE; successful sign-ins continue in IdentityServer.Core.
/// </summary>
public sealed class EmbeddedWindowsSignInFallback(WindowsAuthenticationAvailability windows, ILogger<EmbeddedWindowsSignInFallback> logger)
    : IOpenIddictServerHandler<OpenIddictServerEvents.HandleAuthorizationRequestContext>
{
    public async ValueTask HandleAsync(OpenIddictServerEvents.HandleAuthorizationRequestContext context)
    {
        var http = context.Transaction.GetHttpRequest()!.HttpContext;
        if ((string?)context.Request.GetParameter(SsoLoginUrls.LocalParameter) == "1"
            || !windows.IsAvailable
            || http.Items.ContainsKey(EmbeddedIdentityServerDefaults.WindowsAuthenticationFailureItem))
        {
            context.Reject(Errors.AccessDenied, windows.IsAvailable
                ? "Windows sign-in was not completed. Use the application's local sign-in."
                : "Windows authentication is unavailable. Use the application's local sign-in.");
            return;
        }

        AuthenticateResult result;
        try { result = await http.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme); }
        catch (Exception exception) when (exception is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Windows authentication failed for client {ClientId}.", context.Request.ClientId);
            context.Reject(Errors.AccessDenied, "Windows authentication failed.");
            return;
        }
        if (!result.None) return;

        await http.ChallengeAsync(NegotiateDefaults.AuthenticationScheme);
        var parameters = context.Request.GetParameters()
            .Where(parameter => parameter.Key != SsoLoginUrls.LocalParameter)
            .Select(parameter => new KeyValuePair<string, StringValues>(parameter.Key, (StringValues)parameter.Value))
            .Append(new(SsoLoginUrls.LocalParameter, "1"));
        var escape = WebUtility.HtmlEncode(http.Request.PathBase + "/" + EmbeddedIdentityServerDefaults.AuthorizationEndpoint + QueryString.Create(parameters));
        http.Response.Headers.CacheControl = "no-store";
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.WriteAsync($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url={escape}\"><title>Continue sign-in</title></head><body><p>Windows sign-in was not completed. <a href=\"{escape}\">Continue to local sign-in</a>.</p></body></html>", http.RequestAborted);
        context.HandleRequest();
    }
}

/// <summary>Disables old embedded-client database registrations at Umbraco's own endpoints after upgrading.</summary>
public sealed class LegacyEmbeddedClientGuard(IOpenIddictApplicationManager applications)
    : IOpenIddictServerHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>,
      IOpenIddictServerHandler<OpenIddictServerEvents.ValidateTokenRequestContext>
{
    public async ValueTask HandleAsync(OpenIddictServerEvents.ValidateAuthorizationRequestContext context)
    {
        if (await IsLegacyClient(context.ClientId, context.CancellationToken))
            context.Reject(Errors.InvalidRequest, "The specified client_id may only use the /connect/authorize endpoint.");
    }

    public async ValueTask HandleAsync(OpenIddictServerEvents.ValidateTokenRequestContext context)
    {
        if (await IsLegacyClient(context.ClientId, context.CancellationToken))
            context.Reject(Errors.UnauthorizedClient, "The specified client_id may only use the /connect/token endpoint.");
    }

    private async ValueTask<bool> IsLegacyClient(string? id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var application = await applications.FindByClientIdAsync(id, cancellationToken);
        return application is not null && (await applications.GetDisplayNameAsync(application, cancellationToken))?
            .StartsWith(EmbeddedIdentityServerDefaults.ClientDisplayNamePrefix, StringComparison.Ordinal) == true;
    }
}
