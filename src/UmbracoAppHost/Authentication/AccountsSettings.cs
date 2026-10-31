using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace UmbracoAppHost.Authentication;

/// <summary>Who decides that a signed-in account may have a backoffice user or a member.</summary>
public enum AccountSource
{
    /// <summary>
    /// Manual. An administrator creates the account in the backoffice first; single sign-on
    /// links to it by email or account name and never creates one. Groups are managed in Umbraco.
    /// </summary>
    Umbraco,

    /// <summary>
    /// Active Directory. An account is created on first sign-in when one of its directory groups
    /// has a managed Umbraco group of the same name. Managed memberships follow the directory
    /// at every sign-in; other memberships stay as set in Umbraco. No new account is created
    /// without a matching managed group.
    /// </summary>
    ActiveDirectory,
}

/// <summary>A backoffice user startup provisions when missing.</summary>
public sealed class BackOfficeUserSeed
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] UserGroups { get; set; } = [];
}

/// <summary>A member startup provisions when missing.</summary>
public sealed class MemberSeed
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] MemberGroups { get; set; } = [];
}

/// <summary>
/// Account administration and explicit provisioning options, bound from the <c>Accounts</c> section.
/// OpenID Connect uses <see cref="IdentityServerSettings.BackOfficeAutoLink"/> and
/// <see cref="IdentityServerSettings.MemberAutoLink"/> instead of the legacy provisioning policy.
/// Array properties have no code defaults on purpose: the configuration binder appends
/// configured elements to a pre-populated array.
/// </summary>
public sealed class AccountsSettings
{
    public const string SectionName = "Accounts";

    /// <summary>Policy for explicit calls to AccountProvisioning; unused by OpenID Connect sign-in.</summary>
    public AccountSource BackOfficeUsersManagedBy { get; set; } = AccountSource.Umbraco;

    /// <summary>Member policy for explicit calls to AccountProvisioning; unused by OpenID Connect sign-in.</summary>
    public AccountSource MembersManagedBy { get; set; } = AccountSource.Umbraco;

    /// <summary>
    /// Case-insensitive .NET regular expression selecting the Umbraco group names managed in
    /// Active Directory mode, for both backoffice users and members. For example, <c>^Test Team .*$</c>
    /// manages <c>Test Team Admin</c> while leaving <c>Administrators</c> alone. Unset or empty
    /// manages every group, including Administrators. Ignored in Umbraco mode.
    /// </summary>
    public string? ActiveDirectoryGroupPattern { get; set; }

    /// <summary>Member type alias used for members created in Active Directory mode and by <see cref="EnsureMembers"/>.</summary>
    public string MemberTypeAlias { get; set; } = "Member";

    /// <summary>
    /// Umbraco user group aliases whose backoffice members are Interactive Reports administrators.
    /// A member is an administrator when a backoffice user with the same sign-in identity belongs
    /// to one of these groups.
    /// </summary>
    public string[] ReportAdministratorUserGroups { get; set; } = [];

    /// <summary>
    /// Member groups the first run creates when missing. In Active Directory mode these are the
    /// directory group names that admit members, so list the AD groups here.
    /// </summary>
    public string[] EnsureMemberGroups { get; set; } = [];

    /// <summary>
    /// Backoffice user groups the first run creates when missing, with access to the Content
    /// section only. In Active Directory mode these are the directory group names that admit
    /// backoffice users; administrators refine each group's permissions in the backoffice.
    /// </summary>
    public string[] EnsureUserGroups { get; set; } = [];

    /// <summary>Backoffice users the first run creates when missing; the manual-mode roster seed.</summary>
    public BackOfficeUserSeed[] EnsureBackOfficeUsers { get; set; } = [];

    /// <summary>Members the first run creates when missing; the manual-mode roster seed for the website.</summary>
    public MemberSeed[] EnsureMembers { get; set; } = [];

    /// <summary>
    /// Development only: create a backoffice administrator for the Windows account running the
    /// site (<c>&lt;user&gt;@localhost</c>), so single sign-on to the backoffice works on any
    /// developer machine without editing configuration.
    /// </summary>
    public bool SeedCurrentWindowsUserAsAdministrator { get; set; }

    /// <summary>
    /// Turns the backoffice Delete of a user who has signed in, or is linked to a Windows identity,
    /// into retirement (<see cref="AccountRetirement"/>): the row and its name stay on everything
    /// the person authored, nobody can sign in as it again, and its username is free for a new
    /// account. Umbraco itself refuses to delete such a user. Users who never signed in are still
    /// deleted. Read at every delete, so a change applies without a restart.
    /// </summary>
    public bool RetireDeletedUsers { get; set; } = true;

    /// <summary>The same as <see cref="RetireDeletedUsers"/> for website members deleted in the backoffice.</summary>
    public bool RetireDeletedMembers { get; set; } = true;

    /// <summary>
    /// Lets the first sign-in of a Windows identity that no account is linked to claim a pre-created
    /// account by its email or username. Accounts created from the directory carry the person's SID
    /// from the start and need no such match; turning this off makes the SID the only way into an
    /// account, so a username or email that happens to fit opens nothing, and an account created by
    /// hand stays unusable until it is linked. Active Directory mode still creates accounts at the
    /// first sign-in; it only stops adopting one by name.
    /// </summary>
    public bool LinkByUserNameOnFirstSignIn { get; set; } = true;
}

/// <summary>Rejects an invalid group pattern at startup when Active Directory manages either audience.</summary>
public sealed class AccountsSettingsValidator : IValidateOptions<AccountsSettings>
{
    public ValidateOptionsResult Validate(string? name, AccountsSettings settings)
    {
        if ((settings.BackOfficeUsersManagedBy != AccountSource.ActiveDirectory && settings.MembersManagedBy != AccountSource.ActiveDirectory)
            || string.IsNullOrEmpty(settings.ActiveDirectoryGroupPattern))
            return ValidateOptionsResult.Success;

        try
        {
            _ = new Regex(settings.ActiveDirectoryGroupPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException exception)
        {
            return ValidateOptionsResult.Fail($"Accounts:ActiveDirectoryGroupPattern is not a valid regular expression: {exception.Message}");
        }
    }
}
