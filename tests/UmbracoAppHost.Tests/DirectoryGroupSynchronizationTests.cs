using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class DirectoryGroupSynchronizationTests(AccountsTestHost host) : IClassFixture<AccountsTestHost>
{
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(true, "")]
    [InlineData(false, "")]
    public async Task Without_a_pattern_all_groups_including_Administrators_follow_claims(bool backOffice, string? pattern)
    {
        var person = Person("DOMAIN\\Test Team Users");
        var settings = Settings(pattern);
        await EnsureGroupsAsync(backOffice, "Test Team Users");
        await CreateAccountAsync(backOffice, person, "Administrators", "Test Team Admin");

        Assert.Equal(ProvisioningResult.Ok, await ProvisionAsync(backOffice, person, settings));
        Assert.Equal(["Test Team Users"], await GroupsAsync(backOffice, person));

        // Revoking the last group must persist even though the sign-in is refused.
        var refused = await ProvisionAsync(backOffice, person with { Groups = [] }, settings);
        Assert.False(refused.Allowed);
        Assert.Equal("unauthorized", refused.Status);
        Assert.Empty(await GroupsAsync(backOffice, person));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pattern_syncs_managed_groups_and_preserves_unmanaged_Administrators(bool backOffice)
    {
        // Read actual role claims, including a domain prefix and different casing.
        var account = Person("DOMAIN\\test team users", "Editors");
        var person = SsoAccount.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity(account.ToClaims())));
        var settings = Settings("test team .*");
        await EnsureGroupsAsync(backOffice, "Test Team Users", "Editors");
        await CreateAccountAsync(backOffice, person, "Administrators", "Test Team Admin");

        Assert.Equal(ProvisioningResult.Ok, await ProvisionAsync(backOffice, person, settings));
        Assert.Equal(["Administrators", "Test Team Users"], await GroupsAsync(backOffice, person));
        // Repeating a sign-in keeps the same memberships.
        Assert.Equal(ProvisioningResult.Ok, await ProvisionAsync(backOffice, person, settings));
        Assert.Equal(["Administrators", "Test Team Users"], await GroupsAsync(backOffice, person));

        // No managed claims remain, but the manually managed group still grants access.
        Assert.Equal(ProvisioningResult.Ok, await ProvisionAsync(backOffice, person with { Groups = [] }, settings));
        Assert.Equal(["Administrators"], await GroupsAsync(backOffice, person));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task First_sign_in_requires_and_only_assigns_managed_groups(bool backOffice)
    {
        var person = Person("Administrators");
        var settings = Settings("^Test Team (Admin|Users)$");
        await EnsureGroupsAsync(backOffice, "Administrators", "Test Team Users");

        var refused = await ProvisionAsync(backOffice, person, settings);
        Assert.False(refused.Allowed);
        Assert.Equal("unauthorized", refused.Status);
        Assert.Null(FindAccount(backOffice, person));

        person = person with { Groups = ["Administrators", "Test Team Users"] };
        Assert.Equal(ProvisioningResult.Ok, await ProvisionAsync(backOffice, person, settings));
        Assert.Equal(["Test Team Users"], await GroupsAsync(backOffice, person));

        // A filtered policy must also persist removal of its last managed group.
        refused = await ProvisionAsync(backOffice, person with { Groups = ["Administrators"] }, settings);
        Assert.False(refused.Allowed);
        Assert.Equal("unauthorized", refused.Status);
        Assert.Empty(await GroupsAsync(backOffice, person));
    }

    [Theory]
    [InlineData(true, "^Test Team (Admin|Users)$")]
    [InlineData(false, "^Test Team (Admin|Users)$")]
    [InlineData(true, "^No managed groups$")]
    [InlineData(false, "^No managed groups$")]
    public async Task Regex_anchors_and_alternation_leave_names_outside_the_pattern_alone(bool backOffice, string pattern)
    {
        var person = Person("Test Team Admin", "Test Team Users", "Other Test Team Users");
        await EnsureGroupsAsync(backOffice, "Test Team Admin", "Test Team Users", "Other Test Team Users");
        await CreateAccountAsync(backOffice, person, "Test Team Users Archive");

        Assert.Equal(ProvisioningResult.Ok, await ProvisionAsync(backOffice, person, Settings(pattern)));
        var expected = pattern == "^No managed groups$"
            ? new[] { "Test Team Users Archive" }
            : new[] { "Test Team Admin", "Test Team Users", "Test Team Users Archive" };
        Assert.Equal(expected, await GroupsAsync(backOffice, person));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Umbraco_mode_ignores_the_pattern_and_does_not_synchronize_groups(bool backOffice)
    {
        var person = Person("Test Team Users");
        await EnsureGroupsAsync(backOffice, "Test Team Users");
        await CreateAccountAsync(backOffice, person, "Administrators", "Test Team Admin");
        var settings = Settings("[invalid regex");
        if (backOffice) settings.BackOfficeUsersManagedBy = AccountSource.Umbraco;
        else settings.MembersManagedBy = AccountSource.Umbraco;

        Assert.Equal(ProvisioningResult.Ok, await ProvisionAsync(backOffice, person, settings));
        Assert.Equal(["Administrators", "Test Team Admin"], await GroupsAsync(backOffice, person));
    }

    private static AccountsSettings Settings(string? pattern) => new()
    {
        BackOfficeUsersManagedBy = AccountSource.ActiveDirectory,
        MembersManagedBy = AccountSource.ActiveDirectory,
        ActiveDirectoryGroupPattern = pattern,
        MemberTypeAlias = AccountsTestHost.MemberTypeAlias,
    };

    private static SsoAccount Person(params string[] groups)
    {
        var name = "group.sync." + Guid.NewGuid().ToString("N");
        return AccountsTestHost.Account($"S-1-5-21-100-200-300-{Random.Shared.Next(10000, int.MaxValue)}",
            "DOMAIN\\" + name, name + "@example.invalid", groups);
    }

    private Task<ProvisioningResult> ProvisionAsync(bool backOffice, SsoAccount person, AccountsSettings settings)
        => host.ProvisionAsync(p => backOffice
            ? p.ProvisionBackOfficeAsync(person, "Umbraco.Domain")
            : p.ProvisionMemberAsync(person, "UmbracoMembers.Domain"), settings);

    private IMembershipUser? FindAccount(bool backOffice, SsoAccount person)
        => backOffice ? host.Users.GetByEmail(person.EffectiveEmail) : host.Members.GetByEmail(person.EffectiveEmail);

    private async Task<string[]> GroupsAsync(bool backOffice, SsoAccount person)
    {
        var account = FindAccount(backOffice, person);
        Assert.NotNull(account);
        var groups = backOffice
            ? (await host.Users.GetAsync(account.Key))!.Groups.Select(group => group.Name!)
            : host.Members.GetAllRoles(account.Username);
        return groups.Order(StringComparer.Ordinal).ToArray();
    }

    private async Task CreateAccountAsync(bool backOffice, SsoAccount person, params string[] groups)
    {
        await EnsureGroupsAsync(backOffice, groups);
        if (backOffice)
        {
            var user = await host.CreateUserAsync(person.UserName, person.EffectiveEmail, person.DisplayName);
            using var scope = host.App.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IUserGroupService>();
            var keys = (await service.GetAllAsync(0, int.MaxValue)).Items
                .Where(group => groups.Contains(group.Name)).Select(group => group.Key).ToHashSet();
            var updated = await service.UpdateUserGroupsOnUsersAsync(
                userKeys: new HashSet<Guid> { user.Key }, userGroupKeys: keys, performingUserKey: Constants.Security.SuperUserKey);
            Assert.True(updated.Success, updated.Result.ToString());
        }
        else
        {
            var member = host.CreateMember(person.UserName, person.EffectiveEmail, person.DisplayName);
            host.Members.AssignRoles([member.Username], groups);
        }
    }

    private async Task EnsureGroupsAsync(bool backOffice, params string[] names)
    {
        using var scope = host.App.Services.CreateScope();
        if (backOffice)
        {
            var service = scope.ServiceProvider.GetRequiredService<IUserGroupService>();
            var existing = (await service.GetAllAsync(0, int.MaxValue)).Items.Select(group => group.Name).ToHashSet();
            foreach (var name in names.Where(name => !existing.Contains(name)))
            {
                // Deliberately different aliases: the pattern must match group names.
                var group = new UserGroup(scope.ServiceProvider.GetRequiredService<IShortStringHelper>())
                {
                    Alias = "sync" + Guid.NewGuid().ToString("N"), Name = name,
                };
                group.AddAllowedSection(Constants.Applications.Content);
                var created = await service.CreateAsync(group, Constants.Security.SuperUserKey, []);
                Assert.True(created.Success, created.Status.ToString());
            }
        }
        else
        {
            var service = scope.ServiceProvider.GetRequiredService<IMemberGroupService>();
            foreach (var name in names)
                if (await service.GetByNameAsync(name) is null)
                    await service.CreateAsync(new MemberGroup { Name = name });
        }
    }
}
