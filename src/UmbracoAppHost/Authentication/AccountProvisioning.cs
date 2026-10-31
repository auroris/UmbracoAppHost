using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// The outcome of provisioning: allowed, or refused with a reason the user can read and a
/// status from the backoffice app's external-login vocabulary (<c>user-not-found</c>,
/// <c>unauthorized</c>, <c>failed</c>).
/// </summary>
public sealed record ProvisioningResult(bool Allowed, string? Reason = null, string Status = "unauthorized")
{
    public static readonly ProvisioningResult Ok = new(true);
    public static ProvisioningResult NotFound(string reason) => new(false, reason, "user-not-found");
    public static ProvisioningResult Unauthorized(string reason) => new(false, reason, "unauthorized");
    public static ProvisioningResult Failed(string reason) => new(false, reason, "failed");
}

/// <summary>
/// Explicit account provisioning and SID linking for callers that opt into <see cref="AccountSource"/>.
/// OpenID Connect sign-in uses Umbraco's native external-login managers instead of this policy.
/// The directory picker uses <see cref="LinkAsync"/> when an administrator creates an account.
/// </summary>
/// <remarks>
/// <para>
/// An account belongs to the Windows SID that first signed in to it. Every provider stores the SID
/// as the external login's provider key, so the account linked to the SID under any provider's
/// scheme is the person's own, and switching providers only adds a link. Without such a link, a
/// pre-created account is matched on its email or username, and refused when it is already linked
/// to another SID: Active Directory may give a new person the username of someone it deleted, and
/// that person must not inherit the old account. Retired accounts (<see cref="AccountRetirement"/>)
/// and disabled or locked-out accounts are refused with a reason, which Umbraco's own sign-in
/// would not give.
/// </para>
/// <para>
/// Accounts created from the directory are linked to the SID when they are created
/// (<see cref="LinkAsync"/>), so the match by email or username is only a fallback for accounts
/// created by hand. <see cref="AccountsSettings.LinkByUserNameOnFirstSignIn"/> turns it off: a SID
/// that no account is linked to then finds nothing, whatever the name.
/// </para>
/// <para>
/// Group matching compares each directory group name (<c>DOMAIN\Group</c>) and its part after
/// the backslash with the Umbraco group name, ignoring case. A member group named
/// <c>Report Users</c> therefore admits the AD group <c>DOMAIN\Report Users</c>.
/// </para>
/// </remarks>
public sealed class AccountProvisioning(
    IUserService userService,
    IUserGroupService userGroupService,
    IMemberService memberService,
    IMemberManager memberManager,
    IMemberGroupService memberGroupService,
    IEntityService entityService,
    IExternalLoginWithKeyService externalLogins,
    ICoreScopeProvider scopeProvider,
    SsoProviderCatalog providers,
    IOptions<AccountsSettings> options,
    IOptions<SecuritySettings> security,
    ILogger<AccountProvisioning> logger)
{
    /// <summary>The refusal when only the SID may find an account and none is linked to it.</summary>
    public const string NotLinkedReason = "No account is linked to your Windows identity. Ask an administrator to create one from the directory.";

    private AccountsSettings Settings => options.Value;

    /// <param name="account">The person who signed in.</param>
    /// <param name="scheme">The backoffice provider scheme, stored as the external login's provider name.</param>
    public async Task<ProvisioningResult> ProvisionBackOfficeAsync(SsoAccount account, string scheme)
    {
        var match = await FindAccountAsync<IUser>(
            account,
            LinkSchemes(scheme, provider => provider.BackOfficeScheme, LegacyWindowsLoginSchemes.BackOffice),
            userService.GetAsync,
            userService.GetByEmail,
            userService.GetByUsername,
            security.Value.UserDefaultLockoutTimeInMinutes);
        if (match.Refusal is not null) return match.Refusal;
        var user = match.Account;
        var mode = Settings.BackOfficeUsersManagedBy;

        if (mode == AccountSource.Umbraco)
        {
            if (user is null)
                return ProvisioningResult.NotFound(Settings.LinkByUserNameOnFirstSignIn
                    ? $"No backoffice account exists for {account.AccountName}. Ask an administrator to create one."
                    : NotLinkedReason);
            await EnsureLinkAsync(user.Key, scheme, account.ProviderKey);
            return ProvisioningResult.Ok;
        }

        var groups = (await userGroupService.GetAllAsync(0, int.MaxValue)).Items
            .Where(group => IsDirectoryManagedGroup(group.Name) && account.MatchesGroup(group.Name))
            .ToArray();
        var groupKeys = groups.Select(group => group.Key).ToHashSet();
        if (user is null)
        {
            if (groupKeys.Count == 0)
                return ProvisioningResult.Unauthorized($"{account.AccountName} is not in any directory group that grants backoffice access.");

            var created = await userService.CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel
            {
                Email = account.EffectiveEmail,
                UserName = account.EffectiveEmail,
                Name = account.DisplayName,
                Kind = UserKind.Default,
                UserGroupKeys = groupKeys,
            }, approveUser: true);
            if (!created.Success || created.Result.CreatedUser is null)
                return ProvisioningResult.Failed($"The backoffice account for {account.AccountName} could not be created ({created.Status}).");
            user = created.Result.CreatedUser;
            logger.LogInformation("Created backoffice user {Email} in groups {Groups} from account {Account} via {Scheme}.",
                user.Email, string.Join(", ", groups.Select(g => g.Name)), account.AccountName, scheme);
        }
        else
        {
            groupKeys.UnionWith(user.Groups.Where(group => !IsDirectoryManagedGroup(group.Name)).Select(group => group.Key));
            var current = user.Groups.Select(group => group.Key).ToHashSet();
            if (!current.SetEquals(groupKeys))
            {
                var updated = await userGroupService.UpdateUserGroupsOnUsersAsync(
                    userKeys: new HashSet<Guid> { user.Key }, userGroupKeys: groupKeys, performingUserKey: Constants.Security.SuperUserKey);
                if (!updated.Success)
                    return ProvisioningResult.Failed($"The backoffice groups for {account.AccountName} could not be synchronized ({updated.Result}).");
                logger.LogInformation("Synchronized directory-managed groups for backoffice user {Email}: {Groups}.", user.Email, string.Join(", ", groups.Select(g => g.Name)));
            }
        }

        // Revocations must persist even when the account no longer has access.
        if (groupKeys.Count == 0)
            return ProvisioningResult.Unauthorized($"{account.AccountName} is not in any directory group that grants backoffice access.");

        await EnsureLinkAsync(user.Key, scheme, account.ProviderKey);
        return ProvisioningResult.Ok;
    }

    /// <param name="account">The person who signed in.</param>
    /// <param name="scheme">The member provider scheme, stored as the external login's provider name.</param>
    public async Task<ProvisioningResult> ProvisionMemberAsync(SsoAccount account, string scheme)
    {
        var match = await FindAccountAsync<IMember>(
            account,
            LinkSchemes(scheme, provider => provider.MemberScheme, LegacyWindowsLoginSchemes.Member),
            key => Task.FromResult(FindMember(key)),
            memberService.GetByEmail,
            memberService.GetByUsername,
            security.Value.MemberDefaultLockoutTimeInMinutes);
        if (match.Refusal is not null) return match.Refusal;
        var member = match.Account;
        var mode = Settings.MembersManagedBy;

        if (mode == AccountSource.Umbraco)
        {
            if (member is null)
                return ProvisioningResult.NotFound(Settings.LinkByUserNameOnFirstSignIn
                    ? $"No member account exists for {account.AccountName}. Ask an administrator to create one."
                    : NotLinkedReason);
            await EnsureLinkAsync(member.Key, scheme, account.ProviderKey);
            return ProvisioningResult.Ok;
        }

        var groups = (await memberGroupService.GetAllAsync())
            .Select(group => group.Name)
            .Where(name => name is not null && IsDirectoryManagedGroup(name) && account.MatchesGroup(name))
            .Select(name => name!)
            .ToArray();
        if (member is null && groups.Length == 0)
            return ProvisioningResult.Unauthorized($"{account.AccountName} is not in any directory group that grants access to this site.");

        // Creation, its local password, group membership and SID link succeed together.
        using var scope = scopeProvider.CreateCoreScope();
        if (member is null)
        {
            member = memberService.CreateMemberWithIdentity(
                account.UserName, account.EffectiveEmail, account.DisplayName, Settings.MemberTypeAlias, isApproved: true);
            memberService.Save(member);
            await ProvisionedMemberPassword.InitializeAsync(member, memberManager);
            logger.LogInformation("Created member {Username} from account {Account} via {Scheme}.", member.Username, account.AccountName, scheme);
        }

        var current = memberService.GetAllRoles(member.Username).ToArray();
        var add = groups.Except(current, StringComparer.OrdinalIgnoreCase).ToArray();
        var remove = current.Where(IsDirectoryManagedGroup).Except(groups, StringComparer.OrdinalIgnoreCase).ToArray();
        if (add.Length > 0) memberService.AssignRoles([member.Username], add);
        if (remove.Length > 0) memberService.DissociateRoles([member.Username], remove);
        if (add.Length + remove.Length > 0)
            logger.LogInformation("Synchronized directory-managed member groups for {Username}: {Groups}.", member.Username, string.Join(", ", groups));

        if (groups.Length == 0 && !current.Any(name => !IsDirectoryManagedGroup(name)))
        {
            // Commit removals before refusing a member whose last group was revoked.
            scope.Complete();
            return ProvisioningResult.Unauthorized($"{account.AccountName} is not in any directory group that grants access to this site.");
        }

        await EnsureLinkAsync(member.Key, scheme, account.ProviderKey);
        scope.Complete();
        return ProvisioningResult.Ok;
    }

    private bool IsDirectoryManagedGroup(string? name)
        => string.IsNullOrEmpty(Settings.ActiveDirectoryGroupPattern)
            || (name is not null && Regex.IsMatch(name, Settings.ActiveDirectoryGroupPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    /// <summary>An account found for a sign-in, none, or the reason the sign-in is refused.</summary>
    private readonly record struct AccountMatch<T>(T? Account, ProvisioningResult? Refusal) where T : class;

    /// <summary>
    /// Finds the account a sign-in belongs to, in this order: the account the SID is linked to
    /// under any of <paramref name="schemes"/>; a refusal when the SID names a retired account; the
    /// pre-created account matched on its email, then on username forms of the account name, which
    /// is refused instead when <see cref="AccountsSettings.LinkByUserNameOnFirstSignIn"/> is off. The
    /// account found must be linked to no other identity, and be neither retired, disabled nor
    /// locked out.
    /// </summary>
    private async Task<AccountMatch<T>> FindAccountAsync<T>(
        SsoAccount account,
        IEnumerable<string> schemes,
        Func<Guid, Task<T?>> byKey,
        Func<string, T?> byEmail,
        Func<string, T?> byUsername,
        int lockoutMinutes)
        where T : class, IMembershipUser
    {
        var found = await FindLinkedAsync(account.ProviderKey, schemes, byKey);
        if (found is null)
        {
            // Retirement renames the account to its SID, so the person it belonged to stays out.
            if (byUsername(account.ProviderKey) is { } retired && AccountRetirement.IsRetired(retired.Username))
                return Refuse("This account has been retired.");

            found = byEmail(account.EffectiveEmail)
                ?? account.UserNameCandidates.Select(byUsername).FirstOrDefault(candidate => candidate is not null);
            if (found is null) return new(null, null);
            // The name fits, but only the SID may open an account: refused rather than adopted, and
            // in Active Directory mode not duplicated by a new account under the same name either.
            if (!Settings.LinkByUserNameOnFirstSignIn)
                return new(null, ProvisioningResult.NotFound(NotLinkedReason));
        }

        if (AccountRetirement.IsRetired(found.Username))
            return Refuse("This account has been retired.");
        if (externalLogins.GetExternalLogins(found.Key).Any(login => !string.Equals(login.ProviderKey, account.ProviderKey, StringComparison.OrdinalIgnoreCase)))
            return Refuse($"The account {found.Username} is linked to a different Windows identity. Ask an administrator to retire that account and create a new one.");
        if (!found.IsApproved)
            return Refuse($"The account {found.Username} is disabled.");
        if (IsLockedOut(found, lockoutMinutes))
            return Refuse($"The account {found.Username} is locked out.");
        return new(found, null);

        static AccountMatch<T> Refuse(string reason) => new(null, ProvisioningResult.Unauthorized(reason));
    }

    /// <summary>The account an external login of one of the schemes links to this provider key, if any.</summary>
    private async Task<T?> FindLinkedAsync<T>(string providerKey, IEnumerable<string> schemes, Func<Guid, Task<T?>> byKey)
        where T : class
    {
        foreach (var scheme in schemes)
        {
            foreach (var login in externalLogins.Find(scheme, providerKey))
            {
                if (Guid.TryParse(login.UserId, out var key) && await byKey(key) is { } linked)
                    return linked;
            }
        }
        return null;
    }

    /// <summary>
    /// The schemes a SID may already be linked under for one audience: the scheme signing in first,
    /// then the configured identity server's, then legacy links from the removed Windows provider.
    /// </summary>
    private IEnumerable<string> LinkSchemes(string current, Func<SsoProvider, string> schemeOf, string windowsScheme)
        => providers.All.Select(schemeOf).Prepend(current).Append(windowsScheme).Distinct(StringComparer.Ordinal);

    private IMember? FindMember(Guid key)
    {
        // The member service has no lookup by key; the entity service maps the key to the integer id.
        var id = entityService.GetId(key, UmbracoObjectTypes.Member);
        return id.Success ? memberService.GetById(id.Result) : null;
    }

    /// <summary>Umbraco's own lockout rule: locked until the last lockout plus the configured minutes, or for good without a date.</summary>
    private static bool IsLockedOut(IMembershipUser account, int lockoutMinutes)
        => account.IsLockedOut
            && (account.LastLockoutDate?.AddMinutes(lockoutMinutes) ?? DateTime.MaxValue).ToUniversalTime() > DateTime.UtcNow;

    /// <summary>Records the provider key as the account's external login so later sign-ins match directly.</summary>
    private Task EnsureLinkAsync(Guid key, string scheme, string providerKey) => LinkAsync(key, providerKey, [scheme]);

    /// <summary>
    /// Records a Windows SID as an account's external login under each of the schemes that does not
    /// have it yet, keeping the account's other logins. A sign-in links the scheme it came through;
    /// an account created from the directory is linked under every scheme at once, so its SID is
    /// stored before the person first signs in.
    /// </summary>
    /// <param name="accountKey">The user or member key.</param>
    /// <param name="providerKey">The Windows SID, which every provider of this platform stores as the provider key.</param>
    /// <param name="schemes">The provider schemes to link under, stored as the external logins' provider names.</param>
    public Task LinkAsync(Guid accountKey, string providerKey, IEnumerable<string> schemes)
    {
        var existing = externalLogins.GetExternalLogins(accountKey).ToList();
        var missing = schemes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(scheme => !existing.Any(login => string.Equals(login.LoginProvider, scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(login.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count == 0) return Task.CompletedTask;
        var logins = existing
            .Select(login => (IExternalLogin)new ExternalLogin(login.LoginProvider, login.ProviderKey, login.UserData))
            .Concat(missing.Select(scheme => new ExternalLogin(scheme, providerKey)))
            .ToList();
        externalLogins.Save(accountKey, logins);
        foreach (var scheme in missing)
            logger.LogInformation("Linked {Scheme} key {ProviderKey} to account {Key}.", scheme, providerKey, accountKey);
        return Task.CompletedTask;
    }
}
