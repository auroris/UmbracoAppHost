using System.Collections;
using System.DirectoryServices;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>
/// The local accounts of the computer the site runs on, standing in for a directory on a
/// development machine that is not in a domain. There the embedded identity server signs people in
/// with these accounts, so an account created from this list carries the SID its owner signs in
/// with, and the whole flow can be tried without a domain controller.
/// </summary>
/// <remarks>
/// Local accounts have no email, department or title. The display name is the account's full
/// name, or <c>MACHINE\user</c> when it has none, and the detail's groups are its local groups.
/// Uses the WinNT provider in System.DirectoryServices, already supplied by IdentityServer's
/// ActiveDirectory dependency; the library's LDAP wrappers apply to domain accounts only.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LocalAccountDirectory : IAccountDirectory
{
    private const int AccountDisabledFlag = 0x2;
    private static readonly StringComparer NameComparer = StringComparer.InvariantCultureIgnoreCase;

    /// <summary>
    /// Every term must appear, ignoring case, in the account's name, full name or description, as
    /// a directory search matches each term against several attributes.
    /// </summary>
    public Task<IReadOnlyList<DirectoryUser>> SearchAsync(string query, int take, CancellationToken cancellationToken = default)
    {
        var terms = AccountDirectoryRules.SearchTerms(query);
        cancellationToken.ThrowIfCancellationRequested();
        var limit = Math.Clamp(take, 1, IAccountDirectory.MaxTake);
        return Task.FromResult(Run<IReadOnlyList<DirectoryUser>>(() =>
        {
            using var computer = new DirectoryEntry($"WinNT://{Environment.MachineName},computer");
            var users = computer.Children;
            users.SchemaFilter.Add("user");
            var people = new List<DirectoryUser>();
            foreach (DirectoryEntry entry in users)
            {
                using (entry)
                {
                    if (ToDirectoryUser(entry) is not { Enabled: true } user) continue;
                    var fullName = entry.Properties["FullName"].Value as string;
                    var description = entry.Properties["Description"].Value as string;
                    if (terms.All(term => Contains(user.UserName, term) || Contains(fullName, term) || Contains(description, term)))
                        people.Add(user);
                }
            }
            return people.OrderBy(person => person.DisplayName, NameComparer).Take(limit).ToList();
        }));
    }

    public Task<DirectoryUserDetail?> FindAsync(string sid, CancellationToken cancellationToken = default)
    {
        AccountDirectoryRules.EnsureSid(sid);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Run<DirectoryUserDetail?>(() =>
        {
            var identifier = new SecurityIdentifier(sid).Value;
            using var computer = new DirectoryEntry($"WinNT://{Environment.MachineName},computer");
            var users = computer.Children;
            users.SchemaFilter.Add("user");
            // Compare local SIDs directly, without translating an unknown SID against a domain.
            foreach (DirectoryEntry entry in users)
            {
                using (entry)
                {
                    if (ToDirectoryUser(entry) is { } user && user.Sid == identifier)
                        return new DirectoryUserDetail(user, GroupNames(entry));
                }
            }
            return null;
        }));
    }

    private static DirectoryUser? ToDirectoryUser(DirectoryEntry user)
    {
        if (user.Properties["ObjectSID"].Value is not byte[] sid) return null;
        var userName = user.Name;
        var accountName = $"{Environment.MachineName}\\{userName}";
        var fullName = user.Properties["FullName"].Value as string;
        return new DirectoryUser
        {
            Sid = new SecurityIdentifier(sid, 0).Value,
            // Local accounts have no objectGUID; the directory record leaves it empty in that case too.
            Guid = "",
            AccountName = accountName,
            UserName = userName,
            DisplayName = string.IsNullOrWhiteSpace(fullName) ? accountName : fullName.Trim(),
            Enabled = (Convert.ToInt32(user.Properties["UserFlags"].Value) & AccountDisabledFlag) == 0,
        };
    }

    private static IReadOnlyList<string> GroupNames(DirectoryEntry user)
    {
        var memberships = user.Invoke("Groups");
        try
        {
            if (memberships is not IEnumerable entries)
                throw new InvalidOperationException("The local account's groups could not be enumerated.");
            var groups = new List<string>();
            foreach (var entry in entries)
            {
                using var group = new DirectoryEntry(entry);
                if (!string.IsNullOrWhiteSpace(group.Name)) groups.Add(group.Name);
            }
            groups.Sort(NameComparer);
            return groups;
        }
        finally
        {
            if (memberships is not null && Marshal.IsComObject(memberships))
                Marshal.ReleaseComObject(memberships);
        }
    }

    private static bool Contains(string? value, string term) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Reports a failure of the local account store as an unavailable directory, as the local lookup does for Active Directory.</summary>
    private static T Run<T>(Func<T> query)
    {
        try
        {
            return query();
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or InvalidOperationException
            or TargetInvocationException { InnerException: COMException or UnauthorizedAccessException or InvalidOperationException })
        {
            var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            throw new DirectoryUnavailableException("The local accounts of this computer could not be read: " + cause.Message, exception);
        }
    }
}
