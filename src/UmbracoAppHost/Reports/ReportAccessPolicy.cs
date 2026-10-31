using System.Security.Claims;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Authentication;
using AppBlocks.InteractiveReports.Reports;

namespace UmbracoAppHost.Reports;

/// <summary>
/// Decides who administers Interactive Reports by bridging Umbraco's two identities.
/// </summary>
/// <remarks>
/// A backoffice user is an administrator when one of their user groups is listed in
/// <see cref="AccountsSettings.ReportAdministratorUserGroups"/>. A member is an
/// administrator when the account they signed in with is also linked to such a backoffice
/// user: the provider key stored with the external login (the Windows SID, whichever
/// provider supplied it) joins the two.
/// Everyone else is an ordinary report user.
/// </remarks>
public sealed class ReportAccessPolicy(
    IUserService userService,
    IMemberService memberService,
    IEntityService entityService,
    IExternalLoginWithKeyService externalLogins,
    SsoProviderCatalog providers,
    IOptions<AccountsSettings> accounts,
    IOptions<InteractiveReportOptions> reportOptions,
    ILogger<ReportAccessPolicy> logger,
    AppBlocks.InteractiveReports.DataSources.PublishedReportCatalog? definitions = null) : IReportAccessPolicy
{
    /// <summary>Applies the Interactive Reports action/resource decision table for one request.</summary>
    public async ValueTask<bool> AuthorizeAsync(InteractiveReportAuthorizationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = request.User;
        // This host shows anonymous visitors a sign-in form instead of blocks, so its report endpoints answer the same way.
        if (user.Identity?.IsAuthenticated != true)
            return false;
        // Published block settings own their starting view. Saved personal views remain available.
        if (AppBlocks.Blocks.AppBlockContent.ParseReportName(request.Resource.ReportName, out _, out _)
            && request.Action == InteractiveReportAction.SelectDefaultReport) return false;

        var isAdministrator = await IsAdministratorAsync(user);
        var saved = request.Resource.SavedReport;
        var caller = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var isOwner = saved is not null && caller is not null
            && string.Equals(saved.Owner, caller, StringComparison.Ordinal);
        var isPublic = saved is { IsGlobal: true } or { IsDefault: true };

        var allowed = request.Action switch
        {
            InteractiveReportAction.ViewReport
                or InteractiveReportAction.Query
                or InteractiveReportAction.Export
                or InteractiveReportAction.ListSavedReports
                or InteractiveReportAction.CreateSavedReport =>
                isAdministrator || !IsAdministratorsOnly(request.Resource.SourceReportName ?? request.Resource.ReportName),

            InteractiveReportAction.ReadSavedReport => isPublic || isOwner || isAdministrator,

            InteractiveReportAction.UpdateSavedReport
                or InteractiveReportAction.DeleteSavedReport => isOwner || isAdministrator,

            _ => isAdministrator,
        };
        logger.LogDebug(
            "Report authorization: {Action} on {Report} for {Identity} ({AuthenticationType}) administrator={Administrator} owner={Owner} public={Public} => {Allowed}; administrator groups [{Groups}]",
            request.Action, request.Resource.ReportName, caller, user.Identity?.AuthenticationType,
            isAdministrator, isOwner, isPublic, allowed, string.Join(", ", accounts.Value.ReportAdministratorUserGroups));
        return allowed;
    }

    /// <summary>Whether the report definition, or the built-in listing, is reserved for administrators.</summary>
    private bool IsAdministratorsOnly(string reportName)
    {
        if (reportName.StartsWith("__", StringComparison.Ordinal))
            return true;
        return (definitions?.Reports ?? reportOptions.Value.Reports).TryGetValue(reportName, out var definition)
            && definition.Authorization?.Policy == ReportAdministratorRequirement.Policy;
    }

    /// <summary>Whether the principal, backoffice user or member, administers reports.</summary>
    public async Task<bool> IsAdministratorAsync(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null)
            return false;

        var backOffice = user.Identity?.AuthenticationType is Constants.Security.BackOfficeAuthenticationType
            or Constants.Security.BackOfficeExposedAuthenticationType;
        var umbracoUser = backOffice
            ? await FindUserAsync(id)
            : await FindLinkedBackOfficeUserAsync(id);
        if (umbracoUser is null)
            return false;

        var administratorGroups = accounts.Value.ReportAdministratorUserGroups;
        return umbracoUser.Groups.Any(group => administratorGroups.Contains(group.Alias, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<IUser?> FindUserAsync(string id)
    {
        if (Guid.TryParse(id, out var key))
            return await userService.GetAsync(key);
        return int.TryParse(id, out var legacyId) ? userService.GetUserById(legacyId) : null;
    }

    /// <summary>
    /// Follows member → provider key → backoffice user through the external-login table, falling
    /// back to the member's email, which is how a pre-created backoffice user is matched before
    /// its first sign-in links the key.
    /// </summary>
    private async Task<IUser?> FindLinkedBackOfficeUserAsync(string memberId)
    {
        // The member cookie carries the integer member id as NameIdentifier; external logins are keyed by GUID.
        var member = Guid.TryParse(memberId, out var key)
            ? entityService.GetId(key, UmbracoObjectTypes.Member) is { Success: true } id ? memberService.GetById(id.Result) : null
            : int.TryParse(memberId, out var legacyId) ? memberService.GetById(legacyId) : null;
        if (member is null)
            return null;

        // Every provider stores the same key (the SID), so a member who signed in with Windows
        // still maps to a backoffice user who signed in through the identity server.
        var keys = externalLogins.GetExternalLogins(member.Key)
            .Where(login => login.LoginProvider.StartsWith(Constants.Security.MemberExternalAuthenticationTypePrefix, StringComparison.Ordinal))
            .Select(login => login.ProviderKey)
            .Distinct(StringComparer.Ordinal);
        foreach (var providerKey in keys)
        {
            foreach (var provider in providers.BackOffice)
            {
                var backOfficeLogin = externalLogins.Find(provider.BackOfficeScheme, providerKey).FirstOrDefault();
                var linked = backOfficeLogin is null ? null : await FindUserAsync(backOfficeLogin.UserId);
                if (linked is null) continue;
                logger.LogDebug("Member {Member} maps to backoffice user {UserName} through the {Scheme} key.", member.Username, linked.Username, provider.BackOfficeScheme);
                return linked;
            }
        }

        var user = string.IsNullOrWhiteSpace(member.Email) ? null : userService.GetByEmail(member.Email);
        if (user is not null)
            logger.LogDebug("Member {Member} maps to backoffice user {UserName} through the email address.", member.Username, user.Username);
        return user;
    }
}
