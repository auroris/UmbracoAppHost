using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The SID lock: a pre-created account is matched by username or email once, linked to the Windows
/// SID of that first sign-in, and from then on refused to every other SID, under every provider.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class AccountProvisioningTests(AccountsTestHost host) : IClassFixture<AccountsTestHost>
{
    private const string Domain = "Umbraco.Domain";
    private const string DomainMembers = "UmbracoMembers.Domain";

    [Fact]
    public async Task A_password_initialization_failure_rolls_back_new_members_and_allows_the_next_attempt()
    {
        const string seededEmail = "rollback.seed@example.invalid";
        var settings = new AccountsSettings
        {
            MembersManagedBy = AccountSource.ActiveDirectory,
            MemberTypeAlias = AccountsTestHost.MemberTypeAlias,
            LinkByUserNameOnFirstSignIn = false,
            EnsureMembers = [new MemberSeed { Email = seededEmail }],
        };
        var person = AccountsTestHost.Account("S-1-5-21-100-200-300-12012", "DOMAIN\\Rollback.Person", "rollback.person@example.invalid", "DOMAIN\\Rollback Members");
        using (var scope = host.App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMemberGroupService>().CreateAsync(new MemberGroup { Name = "Rollback Members" });
            var members = scope.ServiceProvider.GetRequiredService<IMemberManager>();
            Assert.IsAssignableFrom<UserManager<MemberIdentityUser>>(members).PasswordValidators.Add(new RejectPassword());
            var provisioning = ActivatorUtilities.CreateInstance<AccountProvisioning>(scope.ServiceProvider, Options.Create(settings), members);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provisioning.ProvisionMemberAsync(person, DomainMembers));
            var configured = ActivatorUtilities.CreateInstance<ConfiguredAccounts>(scope.ServiceProvider, Options.Create(settings), members);
            await configured.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);
        }
        Assert.Null(host.Members.GetByEmail(person.EffectiveEmail));
        Assert.Null(host.Members.GetByEmail(seededEmail));
        Assert.Null(host.Members.GetByUsername(person.UserName));
        Assert.Null(host.Members.GetByUsername(seededEmail));
        Assert.Empty(host.ExternalLogins.Find(DomainMembers, person.ProviderKey));

        var retry = await host.ProvisionAsync(p => p.ProvisionMemberAsync(person, DomainMembers), settings);
        Assert.True(retry.Allowed, retry.Reason);
        using (var scope = host.App.Services.CreateScope())
        {
            var configured = ActivatorUtilities.CreateInstance<ConfiguredAccounts>(scope.ServiceProvider, Options.Create(settings));
            await configured.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);
        }
        AssertHasLocalPassword(host.Members.GetByEmail(person.EffectiveEmail)!);
        AssertHasLocalPassword(host.Members.GetByEmail(seededEmail)!);
    }

    private sealed class RejectPassword : IPasswordValidator<MemberIdentityUser>
    {
        public Task<IdentityResult> ValidateAsync(UserManager<MemberIdentityUser> manager, MemberIdentityUser user, string? password)
            => Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "TestPasswordRejected", Description = "Password initialization rejected by the test." }));
    }

    [Fact]
    public async Task New_directory_provisioned_accounts_have_local_passwords_and_keep_administrator_resets_after_SSO()
    {
        var settings = new AccountsSettings
        {
            BackOfficeUsersManagedBy = AccountSource.ActiveDirectory,
            MembersManagedBy = AccountSource.ActiveDirectory,
            MemberTypeAlias = AccountsTestHost.MemberTypeAlias,
        };
        using var scope = host.App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMemberGroupService>().CreateAsync(new MemberGroup { Name = "Fallback Members" });
        var person = AccountsTestHost.Account("S-1-5-21-100-200-300-11011", "DOMAIN\\Fallback.Person", "fallback.person@example.invalid",
            "DOMAIN\\Editors", "DOMAIN\\Fallback Members");
        Assert.True((await host.ProvisionAsync(p => p.ProvisionBackOfficeAsync(person, Domain), settings)).Allowed);
        Assert.True((await host.ProvisionAsync(p => p.ProvisionMemberAsync(person, DomainMembers), settings)).Allowed);
        var user = host.Users.GetByEmail(person.EffectiveEmail)!;
        var member = host.Members.GetByEmail(person.EffectiveEmail)!;
        AssertHasLocalPassword(user);
        AssertHasLocalPassword(member);

        const string localPassword = "Local-fallback-after-reset91!";
        await host.AddPasswordAsync(user, localPassword);
        var manager = scope.ServiceProvider.GetRequiredService<IMemberManager>();
        var identity = (await manager.FindByNameAsync(member.Username))!;
        var reset = await manager.ResetPasswordAsync(identity, await manager.GeneratePasswordResetTokenAsync(identity), localPassword);
        Assert.True(reset.Succeeded, string.Join("; ", reset.Errors.Select(error => error.Description)));
        var userHash = host.Users.GetByEmail(person.EffectiveEmail)!.RawPasswordValue;
        var memberHash = host.Members.GetByEmail(person.EffectiveEmail)!.RawPasswordValue;

        Assert.True((await host.ProvisionAsync(p => p.ProvisionBackOfficeAsync(person, "Umbraco.RemoteIdentity"), settings)).Allowed);
        Assert.True((await host.ProvisionAsync(p => p.ProvisionMemberAsync(person, "UmbracoMembers.RemoteIdentity"), settings)).Allowed);
        Assert.Equal(userHash, host.Users.GetByEmail(person.EffectiveEmail)!.RawPasswordValue);
        Assert.Equal(memberHash, host.Members.GetByEmail(person.EffectiveEmail)!.RawPasswordValue);
        Assert.True(await manager.CheckPasswordAsync((await manager.FindByNameAsync(member.Username))!, localPassword));
        var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        Assert.True(await users.CheckPasswordAsync((await users.FindByIdAsync(user.Key.ToString()))!, localPassword));
    }

    [Fact]
    public async Task Configured_rosters_initialize_passwords_only_when_creating_accounts()
    {
        const string email = "configured.fallback@example.invalid";
        using var scope = host.App.Services.CreateScope();
        var configured = ActivatorUtilities.CreateInstance<ConfiguredAccounts>(scope.ServiceProvider, Options.Create(new AccountsSettings
        {
            MemberTypeAlias = AccountsTestHost.MemberTypeAlias,
            EnsureMembers = [new MemberSeed { Email = email, Name = "Configured member" }],
            EnsureBackOfficeUsers = [new BackOfficeUserSeed { Email = email, Name = "Configured user", UserGroups = ["editor"] }],
        }));
        await configured.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);
        var user = host.Users.GetByEmail(email)!;
        var member = host.Members.GetByEmail(email)!;
        AssertHasLocalPassword(user);
        AssertHasLocalPassword(member);
        const string localPassword = "Configured-local-reset91!";
        await host.AddPasswordAsync(user, localPassword);
        var manager = scope.ServiceProvider.GetRequiredService<IMemberManager>();
        var identity = (await manager.FindByNameAsync(member.Username))!;
        Assert.True((await manager.ResetPasswordAsync(identity, await manager.GeneratePasswordResetTokenAsync(identity), localPassword)).Succeeded);
        var userHash = host.Users.GetByEmail(email)!.RawPasswordValue;
        var memberHash = host.Members.GetByEmail(email)!.RawPasswordValue;

        await configured.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);
        Assert.Equal(userHash, host.Users.GetByEmail(email)!.RawPasswordValue);
        Assert.Equal(memberHash, host.Members.GetByEmail(email)!.RawPasswordValue);
        Assert.True(await manager.CheckPasswordAsync((await manager.FindByNameAsync(member.Username))!, localPassword));
    }

    private static void AssertHasLocalPassword(IMembershipUser account)
    {
        Assert.NotNull(account);
        Assert.False(string.IsNullOrEmpty(account.RawPasswordValue));
        Assert.False(account.RawPasswordValue.StartsWith(Constants.Security.EmptyPasswordPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pre_created_user_is_locked_to_the_SID_that_first_signs_in_under_any_provider()
    {
        const string first = "S-1-5-21-100-200-300-1001";
        const string other = "S-1-5-21-100-200-300-2002";
        var user = await host.CreateUserAsync("test.user", "test.user@example.invalid", "Test User");

        // The first sign-in matches the pre-created username regardless of case, without the email.
        var shouting = AccountsTestHost.Account(first, "DOMAIN\\TEST.USER");
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionUserAsync(shouting, Domain));
        Assert.Equal(new[] { (Domain, first) }, host.Logins(user.Key));

        // A new person given the same username, with the same email, is someone else.
        var successor = AccountsTestHost.Account(other, "DOMAIN\\Test.User", "test.user@example.invalid");
        foreach (var scheme in new[] { Domain, "Umbraco.RemoteIdentity" })
        {
            var refused = await host.ProvisionUserAsync(successor, scheme);
            Assert.False(refused.Allowed);
            Assert.Equal("unauthorized", refused.Status);
            Assert.Equal("The account test.user is linked to a different Windows identity. Ask an administrator to retire that account and create a new one.", refused.Reason);
            Assert.Empty(host.ExternalLogins.Find(scheme, other));
        }
        Assert.Equal(new[] { (Domain, first) }, host.Logins(user.Key));

        // The same person through another provider is found by SID and gains a second link.
        var original = AccountsTestHost.Account(first, "DOMAIN\\Test.User", "test.user@example.invalid");
        Assert.True((await host.ProvisionUserAsync(original, "Umbraco.RemoteIdentity")).Allowed);
        Assert.True((await host.ProvisionUserAsync(original, Domain)).Allowed);
        Assert.Equal(new[] { (Domain, first), ("Umbraco.RemoteIdentity", first) }, host.Logins(user.Key));
        Assert.Equal("test.user", (await host.Users.GetAsync(user.Key))!.Username);
    }

    [Fact]
    public async Task A_pre_created_member_is_locked_to_the_SID_that_first_signs_in_under_any_provider()
    {
        const string first = "S-1-5-21-100-200-300-3003";
        const string other = "S-1-5-21-100-200-300-4004";
        var member = host.CreateMember("test.member", "test.member@example.invalid", "Test Member");

        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionMemberAsync(AccountsTestHost.Account(first, "DOMAIN\\Test.Member"), DomainMembers));
        Assert.Equal(new[] { (DomainMembers, first) }, host.Logins(member.Key));

        var refused = await host.ProvisionMemberAsync(AccountsTestHost.Account(other, "DOMAIN\\Test.Member", "test.member@example.invalid"), "UmbracoMembers.RemoteIdentity");
        Assert.False(refused.Allowed);
        Assert.Equal("The account test.member is linked to a different Windows identity. Ask an administrator to retire that account and create a new one.", refused.Reason);

        Assert.True((await host.ProvisionMemberAsync(AccountsTestHost.Account(first, "DOMAIN\\test.member"), "UmbracoMembers.RemoteIdentity")).Allowed);
        Assert.Equal(new[] { (DomainMembers, first), ("UmbracoMembers.RemoteIdentity", first) }, host.Logins(member.Key));
    }

    [Fact]
    public async Task Active_Directory_mode_refuses_a_new_SID_for_a_taken_username_instead_of_creating_a_duplicate()
    {
        const string first = "S-1-5-21-100-200-300-5005";
        const string other = "S-1-5-21-100-200-300-6006";
        var directory = new AccountsSettings
        {
            BackOfficeUsersManagedBy = AccountSource.ActiveDirectory,
            MembersManagedBy = AccountSource.ActiveDirectory,
            MemberTypeAlias = AccountsTestHost.MemberTypeAlias,
        };
        using (var scope = host.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMemberGroupService>().CreateAsync(new MemberGroup { Name = "Test Team Users" });
        var person = AccountsTestHost.Account(first, "DOMAIN\\Directory.Person", "directory.person@example.invalid", "DOMAIN\\Editors", "DOMAIN\\Test Team Users");
        var successor = AccountsTestHost.Account(other, "DOMAIN\\Directory.Person", "directory.person@example.invalid", "DOMAIN\\Editors", "DOMAIN\\Test Team Users");

        // The first sign-in creates both accounts from the directory groups.
        Assert.True((await host.ProvisionAsync(p => p.ProvisionBackOfficeAsync(person, Domain), directory)).Allowed);
        Assert.True((await host.ProvisionAsync(p => p.ProvisionMemberAsync(person, DomainMembers), directory)).Allowed);
        var user = host.Users.GetByEmail("directory.person@example.invalid")!;
        var member = host.Members.GetByEmail("directory.person@example.invalid")!;
        Assert.Equal(new[] { (Domain, first) }, host.Logins(user.Key));
        Assert.Equal(new[] { (DomainMembers, first) }, host.Logins(member.Key));

        var refusedUser = await host.ProvisionAsync(p => p.ProvisionBackOfficeAsync(successor, Domain), directory);
        var refusedMember = await host.ProvisionAsync(p => p.ProvisionMemberAsync(successor, DomainMembers), directory);
        Assert.Equal($"The account {user.Username} is linked to a different Windows identity. Ask an administrator to retire that account and create a new one.", refusedUser.Reason);
        Assert.Equal($"The account {member.Username} is linked to a different Windows identity. Ask an administrator to retire that account and create a new one.", refusedMember.Reason);
        Assert.Empty(host.ExternalLogins.Find(Domain, other));
        Assert.Empty(host.ExternalLogins.Find(DomainMembers, other));
        Assert.Equal(new[] { (Domain, first) }, host.Logins(user.Key));
    }

    [Fact]
    public async Task Disabled_and_locked_out_accounts_are_refused_with_a_reason_and_nothing_is_linked()
    {
        var user = await host.CreateUserAsync("disabled.user", "disabled.user@example.invalid", "Disabled User");
        var disabledUser = (await host.Users.GetAsync(user.Key))!;
        disabledUser.IsApproved = false;
        host.Users.Save(disabledUser);
        var disabledMember = host.CreateMember("disabled.member", "disabled.member@example.invalid", "Disabled Member");
        disabledMember.IsApproved = false;
        host.Members.Save(disabledMember);
        var lockedMember = host.CreateMember("locked.member", "locked.member@example.invalid", "Locked Member");
        lockedMember.IsLockedOut = true;
        host.Members.Save(lockedMember);

        var refusedUser = await host.ProvisionUserAsync(AccountsTestHost.Account("S-1-5-21-100-200-300-7007", "DOMAIN\\Disabled.User"), Domain);
        var refusedMember = await host.ProvisionMemberAsync(AccountsTestHost.Account("S-1-5-21-100-200-300-8008", "DOMAIN\\Disabled.Member"), DomainMembers);
        var refusedLocked = await host.ProvisionMemberAsync(AccountsTestHost.Account("S-1-5-21-100-200-300-9009", "DOMAIN\\Locked.Member"), DomainMembers);

        Assert.Equal(ProvisioningResult.Unauthorized("The account disabled.user is disabled."), refusedUser);
        Assert.Equal(ProvisioningResult.Unauthorized("The account disabled.member is disabled."), refusedMember);
        Assert.Equal(ProvisioningResult.Unauthorized("The account locked.member is locked out."), refusedLocked);
        Assert.Empty(host.Logins(user.Key));
        Assert.Empty(host.Logins(disabledMember.Key));
        Assert.Empty(host.Logins(lockedMember.Key));

        // Unknown people are still told no account exists, as before.
        var unknown = await host.ProvisionUserAsync(AccountsTestHost.Account("S-1-5-21-100-200-300-9999", "DOMAIN\\Nobody.Here"), Domain);
        Assert.Equal(ProvisioningResult.NotFound("No backoffice account exists for DOMAIN\\Nobody.Here. Ask an administrator to create one."), unknown);
    }
}
