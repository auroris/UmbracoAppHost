using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

/// <summary>
/// Deleting an account that has signed in retires it: the row and its name stay, the username
/// becomes the Windows SID, sign-in is disabled and the links stay attached, so the SID is refused and
/// the old username is free for a new person.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class AccountRetirementTests(AccountsTestHost host) : IClassFixture<AccountsTestHost>
{
    private const string Domain = "Umbraco.Domain";
    private const string DomainMembers = "UmbracoMembers.Domain";

    [Fact]
    public async Task Deleting_a_user_who_signed_in_retires_it_and_frees_the_username()
    {
        const string first = "S-1-5-21-500-600-700-1001";
        const string successor = "S-1-5-21-500-600-700-2002";
        var users = Assert.IsType<RetiringUserService>(host.Users);
        var user = await host.CreateUserAsync("test.user", "test.user@example.invalid", "Test User");
        var account = AccountsTestHost.Account(first, "DOMAIN\\Test.User", "test.user@example.invalid");
        Assert.True((await host.ProvisionUserAsync(account, Domain)).Allowed);
        Assert.True((await host.ProvisionUserAsync(account, "Umbraco.RemoteIdentity")).Allowed);
        user = await host.MarkSignedInAsync(user);
        Assert.NotNull(user.LastLoginDate);

        Assert.Equal(UserOperationStatus.Success, await users.DeleteAsync(Constants.Security.SuperUserKey, user.Key));

        var retired = await users.GetAsync(user.Key);
        Assert.NotNull(retired);
        Assert.Equal(user.Id, retired.Id);
        Assert.Equal(first, retired.Username);
        Assert.Equal($"{first}@retired.invalid", retired.Email);
        Assert.False(retired.IsApproved);
        Assert.Equal("Test User", retired.Name);
        Assert.Equal(user.LastLoginDate, retired.LastLoginDate);
        Assert.Contains(retired.Groups, group => group.Key == Constants.Security.EditorGroupKey);
        Assert.NotEqual(user.SecurityStamp, retired.SecurityStamp);
        Assert.Equal(new[] { (Domain, first), ("Umbraco.RemoteIdentity", first) }, host.Logins(user.Key));
        using (var scope = host.App.Services.CreateScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<IAuditService>().GetItemsByEntityAsync(user.Id, 0, 20);
            Assert.Contains(audit.Items, item => item.AuditType == AuditType.Custom && item.EntityType == "User"
                && item.Comment!.Contains("username test.user became " + first));
        }

        // The SID now names a retired account and is refused under every provider.
        foreach (var scheme in new[] { Domain, "Umbraco.RemoteIdentity" })
            Assert.Equal(ProvisioningResult.Unauthorized("This account has been retired."), await host.ProvisionUserAsync(account, scheme));

        // The username and email are free: a new account for the new person links to the new SID.
        var replacement = await host.CreateUserAsync("test.user", "test.user@example.invalid", "Test User");
        Assert.NotEqual(user.Key, replacement.Key);
        var newPerson = AccountsTestHost.Account(successor, "DOMAIN\\Test.User", "test.user@example.invalid");
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionUserAsync(newPerson, Domain));
        Assert.Equal(new[] { (Domain, successor) }, host.Logins(replacement.Key));
        Assert.Equal(ProvisioningResult.Unauthorized("This account has been retired."), await host.ProvisionUserAsync(account, Domain));
        Assert.Equal(new[] { (Domain, first), ("Umbraco.RemoteIdentity", first) }, host.Logins(user.Key));

        // Deleting the retired account again keeps it retired under the same SID.
        Assert.Equal(UserOperationStatus.Success, await users.DeleteAsync(Constants.Security.SuperUserKey, user.Key));
        Assert.Equal(first, (await users.GetAsync(user.Key))!.Username);
    }

    [Fact]
    public async Task Users_who_never_signed_in_are_deleted_and_Umbracos_own_rules_still_apply()
    {
        var users = host.Users;
        var neverSignedIn = await host.CreateUserAsync("never.signed.in", "never.signed.in@example.invalid", "Never Signed In");
        Assert.Equal(UserOperationStatus.Success, await users.DeleteAsync(Constants.Security.SuperUserKey, neverSignedIn.Key));
        Assert.Null(await users.GetAsync(neverSignedIn.Key));

        var colleague = await host.CreateUserAsync("colleague", "colleague@example.invalid", "Colleague");
        Assert.Equal(UserOperationStatus.CannotDeleteSelf, await users.DeleteAsync(Constants.Security.SuperUserKey, Constants.Security.SuperUserKey));
        Assert.Equal(UserOperationStatus.CannotDelete, await users.DeleteAsync(colleague.Key, Constants.Security.SuperUserKey));
        Assert.Equal(UserOperationStatus.UserNotFound, await users.DeleteAsync(Constants.Security.SuperUserKey, Guid.NewGuid()));
        Assert.Equal(UserOperationStatus.MissingUser, await users.DeleteAsync(Guid.NewGuid(), colleague.Key));
        var administrator = (await users.GetAsync(Constants.Security.SuperUserKey))!;
        Assert.Equal("admin", administrator.Username);
        Assert.True(administrator.IsApproved);

        // A bulk delete handles each user by its own history.
        var unused = await host.CreateUserAsync("bulk.unused", "bulk.unused@example.invalid", "Bulk Unused");
        var signedIn = await host.MarkSignedInAsync(await host.CreateUserAsync("bulk.signed.in", "bulk.signed.in@example.invalid", "Bulk Signed In"));
        Assert.Equal(UserOperationStatus.Success, await users.DeleteAsync(Constants.Security.SuperUserKey, new HashSet<Guid> { unused.Key, signedIn.Key }));
        Assert.Null(await users.GetAsync(unused.Key));
        var retired = (await users.GetAsync(signedIn.Key))!;
        Assert.Equal("retired-" + signedIn.Key.ToString("N"), retired.Username);
        Assert.Equal($"retired-{signedIn.Key:N}@retired.invalid", retired.Email);
        Assert.Equal("Bulk Signed In", retired.Name);

        // The older synchronous API retires too, and still only disables when not asked to delete.
        var legacy = await host.MarkSignedInAsync(await host.CreateUserAsync("legacy.api", "legacy.api@example.invalid", "Legacy Api"));
        users.Delete(legacy, deletePermanently: true);
        Assert.Equal("retired-" + legacy.Key.ToString("N"), (await users.GetAsync(legacy.Key))!.Username);
        var disabled = await host.MarkSignedInAsync(await host.CreateUserAsync("legacy.disable", "legacy.disable@example.invalid", "Legacy Disable"));
        users.Delete(disabled, deletePermanently: false);
        var stillNamed = (await users.GetAsync(disabled.Key))!;
        Assert.Equal("legacy.disable", stillNamed.Username);
        Assert.False(stillNamed.IsApproved);

        // With RetireDeletedUsers off, Umbraco's own rule is back: no deleting a user with a sign-in history.
        var configuration = (IConfigurationRoot)host.App.Configuration;
        var kept = await host.MarkSignedInAsync(await host.CreateUserAsync("setting.off", "setting.off@example.invalid", "Setting Off"));
        configuration["Accounts:RetireDeletedUsers"] = "false";
        configuration.Reload();
        try
        {
            Assert.Equal(UserOperationStatus.CannotDeleteUserWithLoginHistory, await users.DeleteAsync(Constants.Security.SuperUserKey, kept.Key));
            Assert.Equal("setting.off", (await users.GetAsync(kept.Key))!.Username);
        }
        finally
        {
            configuration["Accounts:RetireDeletedUsers"] = "true";
            configuration.Reload();
        }
    }

    [Fact]
    public async Task Deleting_a_member_who_signed_in_retires_it_and_frees_the_username()
    {
        const string first = "S-1-5-21-500-600-700-3003";
        const string successor = "S-1-5-21-500-600-700-4004";
        using (var scope = host.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMemberGroupService>().CreateAsync(new MemberGroup { Name = "Test Team Users" });
        var member = host.CreateMember("test.user", "test.user@example.invalid", "Test User");
        host.Members.AssignRoles([member.Username], ["Test Team Users"]);
        var account = AccountsTestHost.Account(first, "DOMAIN\\Test.User", "test.user@example.invalid");
        Assert.True((await host.ProvisionMemberAsync(account, DomainMembers)).Allowed);
        var neverSignedIn = host.CreateMember("never.signed.in", "never.signed.in@example.invalid", "Never Signed In");

        using (var scope = host.App.Services.CreateScope())
        {
            var editing = scope.ServiceProvider.GetRequiredService<IMemberEditingService>();
            Assert.IsType<RetiringMemberEditingService>(editing);
            Assert.True((await editing.DeleteAsync(member.Key, Constants.Security.SuperUserKey)).Success);
            Assert.True((await editing.DeleteAsync(neverSignedIn.Key, Constants.Security.SuperUserKey)).Success);
        }

        Assert.Null(host.Members.GetById(neverSignedIn.Key));
        var retired = host.Members.GetById(member.Key);
        Assert.NotNull(retired);
        Assert.Equal(first, retired.Username);
        Assert.Equal($"{first}@retired.invalid", retired.Email);
        Assert.False(retired.IsApproved);
        Assert.Equal("Test User", retired.Name);
        Assert.Equal(new[] { "Test Team Users" }, host.Members.GetAllRoles(retired.Id));
        Assert.Equal(new[] { (DomainMembers, first) }, host.Logins(member.Key));
        Assert.Equal(ProvisioningResult.Unauthorized("This account has been retired."), await host.ProvisionMemberAsync(account, "UmbracoMembers.RemoteIdentity"));

        var replacement = host.CreateMember("test.user", "test.user@example.invalid", "Test User");
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionMemberAsync(AccountsTestHost.Account(successor, "DOMAIN\\Test.User", "test.user@example.invalid"), DomainMembers));
        Assert.Equal(new[] { (DomainMembers, successor) }, host.Logins(replacement.Key));
    }

    [Fact]
    public async Task The_backoffice_delete_endpoints_retire_accounts_and_end_their_sessions()
    {
        const string userSid = "S-1-5-21-500-600-700-5005";
        const string memberSid = "S-1-5-21-500-600-700-6006";
        const string password = "Local-test-only-Session91!";
        const string currentUser = "/umbraco/management/api/v1/user/current";
        var user = await host.CreateUserAsync("http.user", "http.user@example.invalid", "Http User");
        Assert.True((await host.ProvisionUserAsync(AccountsTestHost.Account(userSid, "DOMAIN\\Http.User"), Domain)).Allowed);
        await host.AddPasswordAsync(user, password);
        var member = host.CreateMember("http.member", "http.member@example.invalid", "Http Member");
        Assert.True((await host.ProvisionMemberAsync(AccountsTestHost.Account(memberSid, "DOMAIN\\Http.Member"), DomainMembers)).Allowed);

        // The user is signed in to the backoffice, which gives it a sign-in history.
        using var session = await host.SignInAsync("http.user", password);
        using (var before = await session.GetAsync(currentUser, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.NotNull((await host.Users.GetAsync(user.Key))!.LastLoginDate);

        using var administrator = await host.SignInAsAdministratorAsync();
        using var deleteUser = await administrator.DeleteAsync($"/umbraco/management/api/v1/user/{user.Key}", TestContext.Current.CancellationToken);
        Assert.True(deleteUser.StatusCode == HttpStatusCode.OK, $"Deleting the user returned {deleteUser.StatusCode}: {await deleteUser.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        using var deleteMember = await administrator.DeleteAsync($"/umbraco/management/api/v1/member/{member.Key}", TestContext.Current.CancellationToken);
        Assert.True(deleteMember.StatusCode == HttpStatusCode.OK, $"Deleting the member returned {deleteMember.StatusCode}: {await deleteMember.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");

        var retiredUser = (await host.Users.GetAsync(user.Key))!;
        Assert.Equal(userSid, retiredUser.Username);
        Assert.False(retiredUser.IsApproved);
        Assert.Equal("Http User", retiredUser.Name);
        var retiredMember = host.Members.GetById(member.Key)!;
        Assert.Equal(memberSid, retiredMember.Username);
        Assert.False(retiredMember.IsApproved);

        // The retired user's token is revoked, and its cookie can no longer obtain one, not even
        // after its old username has been given to a new account.
        using (var after = await session.GetAsync(currentUser, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        await host.CreateUserAsync("http.user", "http.user@example.invalid", "Http User");
        using (var reauthorize = await host.AuthorizeAsync(session))
        {
            var location = reauthorize.Headers.Location?.ToString() ?? "";
            Assert.False(location.StartsWith(host.RedirectUri, StringComparison.OrdinalIgnoreCase),
                $"The retired user's cookie was authorized again: {reauthorize.StatusCode} {location}");
            Assert.Contains("/umbraco/login", location);
        }

        // Umbraco's own refusals still reach the backoffice as problem details: nobody deletes themselves.
        using var deleteSelf = await administrator.DeleteAsync($"/umbraco/management/api/v1/user/{Constants.Security.SuperUserKey}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, deleteSelf.StatusCode);
        Assert.Equal("admin", (await host.Users.GetAsync(Constants.Security.SuperUserKey))!.Username);
    }
}
