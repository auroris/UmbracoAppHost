using System.Text.RegularExpressions;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Retires a backoffice user or a member instead of deleting it. Umbraco refuses to delete a user
/// who has signed in, because audit trails and content versions refer to it, and deleting a member
/// would take its name off whatever it did; but an account kept as it is stays usable and holds on
/// to its username and email, which a new person given the same name in Active Directory needs.
/// Retirement keeps the row with its name, groups, start nodes and history, so the person stays
/// named as the author of what they did, and changes only what lets anyone in.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The username becomes the Windows SID the account was linked to (<c>retired-</c> and the
/// account key when it had none). Its external links stay attached so Umbraco finds the disabled
/// account and refuses sign-in instead of auto-creating another account for the same identity.</item>
/// <item>The email becomes <c>&lt;username&gt;@retired.invalid</c>, a reserved domain that never
/// delivers. It keeps Umbraco's unique-email rule satisfied and frees the old address.</item>
/// <item>The account is disabled, its security stamp rotated and its external logins retained. The
/// save raises Umbraco's saved notification, which revokes a user's backoffice tokens, and the new
/// stamp ends its cookie sessions.</item>
/// </list>
/// <para>The built-in administrator is never retired.</para>
/// </remarks>
public sealed partial class AccountRetirement(
    ICoreScopeProvider scopeProvider,
    IServiceScopeFactory scopeFactory,
    IMemberService memberService,
    IExternalLoginWithKeyService externalLogins,
    IUserIdKeyResolver userIds,
    IAuditService auditService,
    ILogger<AccountRetirement> logger)
{
    /// <summary>Username prefix of a retired account that was not linked to a Windows SID.</summary>
    public const string RetiredUserNamePrefix = "retired-";

    /// <summary>Email domain of retired accounts. RFC 2606 reserves <c>.invalid</c>, so nothing is ever delivered.</summary>
    public const string RetiredEmailDomain = "retired.invalid";

    /// <summary>
    /// Whether a username is a retired account's: a Windows SID or the <see cref="RetiredUserNamePrefix"/>
    /// form. No sign-in produces either, so an account carrying one is treated as retired.
    /// </summary>
    public static bool IsRetired(string? username)
        => username is not null
            && (IsWindowsSid(username) || username.StartsWith(RetiredUserNamePrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a value has the form of a Windows security identifier, such as <c>S-1-5-21-1004336348-1177238915-682003330-1001</c>.</summary>
    public static bool IsWindowsSid(string? value) => value is not null && SidPattern().IsMatch(value);

    /// <summary>
    /// Whether deleting the account must retire it: it has signed in (Umbraco's own test for a user
    /// that history may refer to), or it is linked to an external login, so a person owns it.
    /// </summary>
    public bool HasSignedIn(IMembershipUser account)
        => (account.LastLoginDate is { } lastLogin && lastLogin != default)
            || externalLogins.GetExternalLogins(account.Key).Any();

    /// <summary>Retires a backoffice user.</summary>
    /// <param name="user">The user to retire.</param>
    /// <param name="performingUserKey">The user deleting it, for the audit log; the administrator when unknown.</param>
    /// <returns><see cref="UserOperationStatus.CannotDelete"/> for the built-in administrator; otherwise the status of the save.</returns>
    public async Task<UserOperationStatus> RetireUserAsync(IUser user, Guid? performingUserKey = null)
    {
        if (user.Key == Constants.Security.SuperUserKey)
            return UserOperationStatus.CannotDelete;

        // One transaction for the rename, the unlinking and the audit entry. Umbraco's user service
        // saves through the backoffice user store in a service scope of its own; doing the same keeps
        // the status, so a save that a notification handler cancels is not reported as done.
        using var transaction = scopeProvider.CreateCoreScope();
        using var services = scopeFactory.CreateScope();
        var store = services.ServiceProvider.GetRequiredService<IBackOfficeUserStore>();
        var previous = (user.Username, user.Email);
        var username = await RetiredUserNameAsync(user, async name => (await store.GetByUserNameAsync(name))?.Key);
        user.Username = username;
        user.Email = RetiredEmail(username);
        // As Umbraco's own disable and delete do, so the backoffice shows the user as disabled.
        user.IsApproved = false;
        user.InvitedDate = null;
        user.SecurityStamp = Guid.NewGuid().ToString();
        var status = await store.SaveAsync(user);
        if (status != UserOperationStatus.Success)
        {
            logger.LogWarning("Backoffice user {Username} could not be retired: {Status}.", previous.Username, status);
            return status;
        }

        await AuditAsync(performingUserKey, user.Id, "User", previous, username);
        transaction.Complete();
        logger.LogInformation("Retired backoffice user {Username} ({Email}) as {RetiredUsername} instead of deleting it.",
            previous.Username, previous.Email, username);
        return UserOperationStatus.Success;
    }

    /// <summary>Retires a website member.</summary>
    /// <param name="member">The member to retire.</param>
    /// <param name="performingUserKey">The backoffice user deleting it, recorded as the one who saved it.</param>
    /// <returns>Whether the member was retired; false when a notification handler cancelled the save.</returns>
    public async Task<bool> RetireMemberAsync(IMember member, Guid? performingUserKey = null)
    {
        using var transaction = scopeProvider.CreateCoreScope();
        var previous = (member.Username, member.Email);
        var username = await RetiredUserNameAsync(member, name => Task.FromResult(memberService.GetByUsername(name)?.Key));
        member.Username = username;
        member.Email = RetiredEmail(username);
        member.IsApproved = false;
        member.SecurityStamp = Guid.NewGuid().ToString();
        var saved = memberService.Save(member, await UserIdAsync(performingUserKey));
        if (!saved.Success)
        {
            logger.LogWarning("Member {Username} could not be retired: {Result}.", previous.Username, saved.Result?.Result);
            return false;
        }

        await AuditAsync(performingUserKey, member.Id, "Member", previous, username);
        transaction.Complete();
        logger.LogInformation("Retired member {Username} ({Email}) as {RetiredUsername} instead of deleting it.",
            previous.Username, previous.Email, username);
        return true;
    }

    /// <summary>
    /// The account's Windows SID: the provider key of its oldest external login that is one, as every
    /// provider of this platform stores the SID. An account that is already retired keeps its
    /// username; one without a SID, or whose SID another account already carries, gets
    /// <c>retired-&lt;key&gt;</c>, which is unique.
    /// </summary>
    private async Task<string> RetiredUserNameAsync(IMembershipUser account, Func<string, Task<Guid?>> ownerOf)
    {
        if (IsRetired(account.Username)) return account.Username;
        var sid = externalLogins.GetExternalLogins(account.Key)
            .OrderBy(login => login.CreateDate)
            .Select(login => login.ProviderKey)
            .FirstOrDefault(IsWindowsSid);
        if (sid is not null)
        {
            var owner = await ownerOf(sid);
            if (owner is null || owner == account.Key) return sid;
        }
        return RetiredUserNamePrefix + account.Key.ToString("N");
    }

    private static string RetiredEmail(string username) => $"{username}@{RetiredEmailDomain}";

    /// <summary>Records the username and email the account had, which the retirement replaces.</summary>
    private async Task AuditAsync(Guid? performingUserKey, int id, string entityType, (string Username, string Email) previous, string username)
    {
        var result = await auditService.AddAsync(AuditType.Custom, performingUserKey ?? Constants.Security.SuperUserKey, id, entityType,
            $"Retired instead of deleted: username {previous.Username} became {username}, email {previous.Email} became {RetiredEmail(username)}.");
        if (!result.Success)
            logger.LogWarning("The retirement of {Username} was not recorded in the audit log: {Status}.", previous.Username, result.Result);
    }

    /// <summary>The id the member service records as the one who saved: the deleting user's, else the administrator's.</summary>
    private async Task<int> UserIdAsync(Guid? userKey)
    {
        var id = await userIds.TryGetAsync(userKey ?? Constants.Security.SuperUserKey);
        return id.Success ? id.Result : await userIds.GetAsync(Constants.Security.SuperUserKey);
    }

    [GeneratedRegex("^S-1(-[0-9]+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();
}
