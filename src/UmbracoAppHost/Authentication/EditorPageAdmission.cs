using System.Security.Claims;
using AppBlocks.Authentication;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.AuthorizationStatus;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// This host's rule for a page Umbraco's Public Access protects: a backoffice user who may edit the page is admitted to it
/// without a member account. "May edit" is Umbraco's own <b>Update</b> permission on the page, through the user's groups and
/// within their start nodes, read from the store when the request arrives so the account's current standing counts. AppBlocks
/// asks only once Umbraco has refused the request, that is on a protected page with no member session, and the same answer
/// governs the page and every block endpoint on it. AppBlocks itself admits nobody Umbraco refuses; this rule is the host's.
/// </summary>
public sealed class EditorPageAdmission(ApplicationAccessPolicy access, IUserService users, IContentPermissionService permissions)
    : IProtectedPageAdmission
{
    public async Task<ClaimsPrincipal?> AdmitAsync(HttpContext context, IPublishedContent page, CancellationToken ct)
    {
        if (await access.BackOfficeUserAsync(context) is not { } principal) return null;
        ct.ThrowIfCancellationRequested();
        return await CanEditAsync(principal, page) ? principal : null;
    }

    /// <summary>
    /// Whether the approved, not locked-out backoffice user the principal names has Umbraco's Update permission on the page.
    /// </summary>
    public async Task<bool> CanEditAsync(ClaimsPrincipal principal, IPublishedContent page)
    {
        var user = await ActiveUserAsync(principal);
        return user is not null
            && await permissions.AuthorizeAccessAsync(user, page.Key, ActionUpdate.ActionLetter) == ContentAuthorizationStatus.Success;
    }

    private async Task<IUser?> ActiveUserAsync(ClaimsPrincipal principal)
    {
        if (ApplicationAccessPolicy.BackOfficeIdentity(principal) is not { } identity) return null;
        var id = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var user = Guid.TryParse(id, out var key) ? await users.GetAsync(key)
            : int.TryParse(id, out var legacyId) ? users.GetUserById(legacyId) : null;
        return user is { IsApproved: true, IsLockedOut: false } ? user : null;
    }
}
