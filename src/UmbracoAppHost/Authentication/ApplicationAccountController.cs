using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core;
using Umbraco.Cms.Web.Common.Security;
using AppBlocks.Authentication;

namespace UmbracoAppHost.Authentication;

[Route("application/account")]
public sealed class ApplicationAccountController(ApplicationAccessPolicy access, IAntiforgery antiforgery,
    IMemberSignInManager members, IBackOfficeSignInManager backOffice) : Controller
{
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromForm] string? returnUrl)
    {
        var member = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var backOfficeUser = await access.BackOfficeUserAsync(HttpContext);
        var principal = member.Succeeded ? member.Principal : backOfficeUser;
        if (principal is not null) HttpContext.User = principal;
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return BadRequest(); }

        // End both application sessions if present, so signing out cannot silently switch
        // from a member account to a backoffice account on the next request.
        if (member.Succeeded) await members.SignOutAsync();
        if (backOfficeUser is not null)
        {
            HttpContext.User = backOfficeUser;
            await backOffice.SignOutAsync();
            await HttpContext.SignOutAsync(Constants.Security.BackOfficeExposedAuthenticationType);
        }
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Request.PathBase + "/");
    }
}
