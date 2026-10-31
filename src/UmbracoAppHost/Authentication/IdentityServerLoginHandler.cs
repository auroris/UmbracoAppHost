using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace UmbracoAppHost.Authentication;

/// <summary>Runs the standard OIDC flow, with a local sign-in fallback if starting it fails.</summary>
public sealed class IdentityServerLoginHandler(
    IOptionsMonitor<OpenIdConnectOptions> options,
    ILoggerFactory logger,
    HtmlEncoder htmlEncoder,
    UrlEncoder encoder) : OpenIdConnectHandler(options, logger, htmlEncoder, encoder)
{
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        try
        {
            await base.HandleChallengeAsync(properties);
        }
        catch (Exception exception) when (!Response.HasStarted && !Context.RequestAborted.IsCancellationRequested)
        {
            // Discovery runs before OnRedirectToIdentityProvider. OnRemoteFailure handles
            // errors after the provider returns, so it cannot catch a discovery outage.
            Logger.LogWarning(exception, "Identity server sign-in via {Scheme} could not start.", Scheme.Name);
            const string reason = "The identity server is unavailable. Sign in with a local account or try again later.";
            if (SsoRefusal.IsBackOfficeScheme(Scheme.Name))
                await BackOfficeSsoFallback.RedirectAsync(Context, properties, reason);
            else
                await SsoRefusal.WriteAsync(Context, Scheme.Name, Scheme.DisplayName ?? "Identity server", properties, reason, "failed");
        }
    }
}
