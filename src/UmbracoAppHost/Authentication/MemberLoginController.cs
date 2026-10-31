using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Common.Filters;
using Umbraco.Cms.Web.Common.Security;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace UmbracoAppHost.Authentication;

/// <summary>Starts a member provider on the current page, retaining Umbraco's native callback and two-factor flow.</summary>
[AllowAnonymous]
public sealed class MemberLoginController(
    IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
    ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger,
    IPublishedUrlProvider publishedUrlProvider, IMemberSignInManager members,
    SsoProviderCatalog providers, ILogger<MemberLoginController> logger)
    : SurfaceController(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
{
    [HttpGet]
    public Task<IActionResult> Login(string? provider, string? returnUrl) => StartAsync(provider, returnUrl);

    [HttpPost]
    [ValidateAntiForgeryToken]
    [ValidateUmbracoFormRouteString]
    public Task<IActionResult> Retry(string? provider, string? returnUrl) => StartAsync(provider, returnUrl);

    private async Task<IActionResult> StartAsync(string? provider, string? returnUrl)
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        var destination = SsoLoginUrls.CleanReturnUrl(HttpContext, returnUrl);
        if (!providers.MemberLogins.Any(candidate => candidate.MemberScheme == provider))
            return LocalRedirect(SsoLoginUrls.LocalMemberUrl(HttpContext, destination, "That sign-in provider is not available."));

        var callback = SsoLoginUrls.SurfaceAction(Url, nameof(UmbExternalLoginController.ExternalLoginCallback), "UmbExternalLogin", new { returnUrl = destination });
        var properties = members.ConfigureExternalAuthenticationProperties(provider!, callback);
        properties.Items[SsoLoginUrls.MemberReturnUrlProperty] = destination;
        try
        {
            // ChallengeResult executes after the action returns, outside its exception boundary.
            await HttpContext.ChallengeAsync(provider!, properties);
            return new EmptyResult();
        }
        catch (Exception exception) when (!Response.HasStarted && !(exception is OperationCanceledException && HttpContext.RequestAborted.IsCancellationRequested))
        {
            logger.LogWarning(exception, "Member sign-in via {Provider} could not start.", provider);
            return LocalRedirect(SsoLoginUrls.LocalMemberUrl(HttpContext, destination, "Single sign-on is unavailable. Sign in with your username and password, or try again."));
        }
    }
}
