using System.Security.Claims;
using AppBlocks.InteractiveReports.Setup;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Authentication;
using UmbracoAppHost.Reports;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class ReportAdministrationTests
{
    /// <summary>
    /// Who administers reports on a running site: Umbraco's Administrators, AppBlocks' own group, this host's configured
    /// groups, and a member through the backoffice user their account links to, which is this host's addition to
    /// AppBlocks' policy. An account's current standing counts, not the ticket.
    /// </summary>
    [Fact]
    public async Task Report_administration_follows_umbraco_appblocks_and_configured_groups_and_a_members_linked_user()
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            EnvironmentName = "Production",
            // The accounts are the fixture; no content is needed.
            FirstBootGroup = "Settings",
            Settings =
            {
                ["Accounts:BackOfficeUsersManagedBy"] = "Umbraco",
                ["Accounts:MembersManagedBy"] = "Umbraco",
                ["Accounts:EnsureUserGroups:0"] = ReportAdministratorGroup.Name,
            },
        }, TestContext.Current.CancellationToken);
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var groups = services.GetRequiredService<IUserGroupService>();
        // Hosts create AppBlocks' recognized group themselves; this host provisions it from configuration.
        Assert.NotNull(await groups.GetAsync(ReportAdministratorGroup.Alias));
        Assert.NotNull(await groups.GetAsync(ConfiguredAccounts.ReportAdministratorsAlias));
        var policy = services.GetRequiredService<ReportAccessPolicy>();

        var appBlocksAdministrator = await site.CreateUserAsync("reports-admin", "reports-admin@example.invalid", "Reports administrator", ReportAdministratorGroup.Alias);
        var hostAdministrator = await site.CreateUserAsync("host-reports-admin", "host-reports-admin@example.invalid", "Host reports administrator", ConfiguredAccounts.ReportAdministratorsAlias);
        var administrator = await site.CreateUserAsync("site-admin", "site-admin@example.invalid", "Site administrator", Constants.Security.AdminGroupAlias);
        var editor = await site.CreateUserAsync("plain-editor", "plain-editor@example.invalid", "Plain editor");
        Assert.True(await policy.IsAdministratorAsync(BackOffice(appBlocksAdministrator)));
        Assert.True(await policy.IsAdministratorAsync(BackOffice(hostAdministrator)));
        Assert.True(await policy.IsAdministratorAsync(BackOffice(administrator)));
        Assert.False(await policy.IsAdministratorAsync(BackOffice(editor)));

        // A member administers through the backoffice user their account links to: by email here, before a first sign-in stores a key.
        var linkedMember = site.CreateMember("reports-admin-member", "reports-admin@example.invalid", "Linked member");
        var plainMember = site.CreateMember("plain-member", "plain-member@example.invalid", "Plain member");
        Assert.True(await policy.IsAdministratorAsync(Member(linkedMember)));
        Assert.False(await policy.IsAdministratorAsync(Member(plainMember)));

        // With both accounts signed in, the backoffice user answers for themselves; otherwise the member's link decides.
        Assert.True(await policy.IsAdministratorAsync(Combined(plainMember, appBlocksAdministrator)));
        Assert.True(await policy.IsAdministratorAsync(Combined(linkedMember, editor)));
        Assert.False(await policy.IsAdministratorAsync(Combined(plainMember, editor)));

        // Standing is read from the store: a disabled account administers nothing, and neither does the member linked to it.
        var users = services.GetRequiredService<IUserService>();
        appBlocksAdministrator.IsApproved = false;
        users.Save(appBlocksAdministrator);
        Assert.False(await policy.IsAdministratorAsync(BackOffice(appBlocksAdministrator)));
        Assert.False(await policy.IsAdministratorAsync(Member(linkedMember)));
    }

    private static ClaimsPrincipal BackOffice(IUser user) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user.Key.ToString())], Constants.Security.BackOfficeExposedAuthenticationType));

    private static ClaimsPrincipal Member(IMember member) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, member.Id.ToString())], IdentityConstants.ApplicationScheme));

    /// <summary>Both sessions, the member first, as AppBlocks combines them.</summary>
    private static ClaimsPrincipal Combined(IMember member, IUser user)
        => new(Member(member).Identities.Concat(BackOffice(user).Identities));
}
