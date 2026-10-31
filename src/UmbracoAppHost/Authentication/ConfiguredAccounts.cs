using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using uSync.BackOffice;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Creates configured accounts and groups, including the report-administrators group.
/// These are not included in the standard uSync export. Existing accounts are preserved.
/// </summary>
public sealed class ConfiguredAccounts(
    IRuntimeState runtimeState,
    IMemberGroupService memberGroupService,
    IUserGroupService userGroupService,
    IUserService userService,
    IShortStringHelper shortStringHelper,
    IMemberService memberService,
    IMemberManager memberManager,
    ICoreScopeProvider scopeProvider,
    IOptions<AccountsSettings> accounts,
    ILogger<ConfiguredAccounts> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>,
    INotificationAsyncHandler<uSyncImportStartingNotification>
{
    public const string ReportAdministratorsAlias = "interactiveReportAdministrators";

    private static readonly Guid ReportAdministratorsKey = new("6f1c0f3e-5d9a-4c2b-9a6e-2b4a7f3d0c04");

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != RuntimeLevel.Run)
        {
            logger.LogInformation("Configured accounts skipped: runtime level is {Level}.", runtimeState.Level);
            return;
        }

        await Step("member groups", EnsureMemberGroupsAsync);
        await Step("members", EnsureMembersAsync);
        await Step("user groups", EnsureUserGroupsAsync);
        await Step("report administrators group", EnsureReportAdministratorsGroupAsync);
        await Step("backoffice users", EnsureBackOfficeUsersAsync);
    }

    /// <summary>
    /// Creates the configured groups before any uSync import, the first boot's included. That import runs
    /// from uSync's <see cref="UmbracoApplicationStartingNotification"/> handler, before the started
    /// notification above, and a group picker drops a group name that does not exist yet: a data source
    /// whose export names its write group would otherwise be published writable by every reader.
    /// </summary>
    public async Task HandleAsync(uSyncImportStartingNotification notification, CancellationToken cancellationToken)
    {
        await Step("member groups", EnsureMemberGroupsAsync);
        await Step("user groups", EnsureUserGroupsAsync);
    }

    /// <summary>
    /// Runs one step on its own: a failure is logged and the remaining steps still run, and a
    /// provisioning failure never takes the site down, so the backoffice stays reachable.
    /// </summary>
    private async Task Step(string name, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Configured accounts: {Step} failed.", name);
        }
    }

    private async Task EnsureMemberGroupsAsync()
    {
        foreach (var name in accounts.Value.EnsureMemberGroups.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await memberGroupService.GetByNameAsync(name) is not null) continue;
            await memberGroupService.CreateAsync(new MemberGroup { Name = name });
            logger.LogInformation("Created member group {Name}.", name);
        }
    }

    /// <summary>Seeds the manual member roster from configuration; the email doubles as the username.</summary>
    private async Task EnsureMembersAsync()
    {
        foreach (var seed in accounts.Value.EnsureMembers)
        {
            if (string.IsNullOrWhiteSpace(seed.Email) || memberService.GetByEmail(seed.Email) is not null) continue;
            using var scope = scopeProvider.CreateCoreScope();
            var member = memberService.CreateMemberWithIdentity(
                seed.Email, seed.Email, string.IsNullOrWhiteSpace(seed.Name) ? seed.Email : seed.Name,
                accounts.Value.MemberTypeAlias, isApproved: true);
            memberService.Save(member);
            await ProvisionedMemberPassword.InitializeAsync(member, memberManager);
            if (seed.MemberGroups.Length > 0) memberService.AssignRoles([member.Username], seed.MemberGroups);
            scope.Complete();
            logger.LogInformation("Created member {Email} in groups {Groups}.", seed.Email, string.Join(", ", seed.MemberGroups));
        }
    }

    /// <summary>
    /// Creates configured backoffice groups by name; the alias is derived from the name. A group that
    /// already exists under that name is left alone whatever its alias, as Umbraco's own groups
    /// (Editors is <c>editor</c>) would otherwise collide on the name.
    /// </summary>
    private async Task EnsureUserGroupsAsync()
    {
        var existing = (await userGroupService.GetAllAsync(0, int.MaxValue)).Items.ToList();
        foreach (var name in accounts.Value.EnsureUserGroups.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var alias = shortStringHelper.CleanStringForSafeAlias(name);
            if (string.IsNullOrWhiteSpace(alias)) continue;
            if (existing.Any(group => string.Equals(group.Alias, alias, StringComparison.OrdinalIgnoreCase)
                || string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            var group = new UserGroup(shortStringHelper) { Alias = alias, Name = name, Icon = "icon-users" };
            group.AddAllowedSection(Constants.Applications.Content);
            var result = await userGroupService.CreateAsync(group, Constants.Security.SuperUserKey, []);
            if (result.Success) logger.LogInformation("Created user group {Name} ({Alias}).", name, alias);
            else logger.LogWarning("Could not create user group {Name}: {Status}.", name, result.Status);
        }
    }

    private async Task EnsureReportAdministratorsGroupAsync()
    {
        if (await userGroupService.GetAsync(ReportAdministratorsAlias) is not null) return;
        var group = new UserGroup(shortStringHelper)
        {
            Key = ReportAdministratorsKey,
            Alias = ReportAdministratorsAlias,
            Name = "Interactive Report Administrators",
            Icon = "icon-bar-chart",
        };
        group.AddAllowedSection(Constants.Applications.Content);
        var result = await userGroupService.CreateAsync(group, Constants.Security.SuperUserKey, []);
        if (result.Success) logger.LogInformation("Created user group {Alias}.", ReportAdministratorsAlias);
        else logger.LogWarning("Could not create user group {Alias}: {Status}.", ReportAdministratorsAlias, result.Status);
    }

    /// <summary>
    /// Seeds the manual backoffice roster from configuration, plus the developer's own Windows
    /// account as an administrator when <see cref="AccountsSettings.SeedCurrentWindowsUserAsAdministrator"/> is set.
    /// </summary>
    private async Task EnsureBackOfficeUsersAsync()
    {
        var seeds = accounts.Value.EnsureBackOfficeUsers.ToList();
        if (accounts.Value.SeedCurrentWindowsUserAsAdministrator)
        {
            seeds.Add(new BackOfficeUserSeed
            {
                Email = $"{Environment.UserName}@localhost",
                Name = Environment.UserName,
                UserGroups = [Constants.Security.AdminGroupAlias],
            });
        }

        foreach (var seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.Email) || userService.GetByEmail(seed.Email) is not null) continue;
            var groupKeys = new HashSet<Guid>();
            foreach (var alias in seed.UserGroups)
            {
                var group = await userGroupService.GetAsync(alias);
                if (group is null) logger.LogWarning("Backoffice user seed {Email} names unknown user group {Alias}.", seed.Email, alias);
                else groupKeys.Add(group.Key);
            }
            var result = await userService.CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel
            {
                Email = seed.Email,
                UserName = seed.Email,
                Name = string.IsNullOrWhiteSpace(seed.Name) ? seed.Email : seed.Name,
                Kind = UserKind.Default,
                UserGroupKeys = groupKeys,
            }, approveUser: true);
            if (result.Success) logger.LogInformation("Created backoffice user {Email} in groups {Groups}.", seed.Email, string.Join(", ", seed.UserGroups));
            else logger.LogWarning("Could not create backoffice user {Email}: {Status}.", seed.Email, result.Status);
        }
    }

}
