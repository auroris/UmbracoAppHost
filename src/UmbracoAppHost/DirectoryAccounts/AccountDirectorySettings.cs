using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>Where <see cref="IAccountDirectory"/> finds people.</summary>
public enum AccountDirectorySource
{
    /// <summary>Active Directory, queried by the site itself as its process account.</summary>
    ActiveDirectory,

    /// <summary>The directory API of the external identity server in <see cref="IdentityServerSettings.Authority"/>.</summary>
    IdentityServer,

    /// <summary>The local accounts of the computer the site runs on.</summary>
    LocalAccounts,
}

/// <summary>
/// This site's own setting in the <c>Directory</c> section. The section also holds the local
/// lookup's <see cref="DirectoryOptions"/> (LDAP roots and result limits), which are bound from it
/// as well, so a site configures its directory in one place.
/// </summary>
public sealed class AccountDirectorySettings
{
    public const string SectionName = DirectoryOptions.SectionName;

    /// <summary>
    /// Search the local accounts of the computer the site runs on instead of a directory. True
    /// suits a development machine that is not in a domain; false never does it. Unset decides by
    /// itself: local accounts when the site signs in through its embedded identity server on a
    /// computer that is not in a domain, where the people who sign in are local accounts too.
    /// </summary>
    public bool? UseLocalAccounts { get; set; }

    /// <summary>
    /// Chooses the source from configuration alone, without touching any directory, so startup can
    /// say which one is in use and a directory that is down only shows when someone searches it.
    /// </summary>
    /// <remarks>
    /// A site that signs in through an external identity server searches that server's directory
    /// even on a computer outside the domain: the people who sign in are the server's, and their
    /// SIDs are what the accounts must carry. Only an explicit <see cref="UseLocalAccounts"/> sends
    /// it to local accounts.
    /// </remarks>
    public static AccountDirectorySource Choose(AccountDirectorySettings directory, IdentityServerSettings identityServer)
    {
        if (directory.UseLocalAccounts == true) return AccountDirectorySource.LocalAccounts;
        if (!identityServer.UsesEmbeddedIdentityServer) return AccountDirectorySource.IdentityServer;
        return directory.UseLocalAccounts is null && IsOutsideDomain
            ? AccountDirectorySource.LocalAccounts
            : AccountDirectorySource.ActiveDirectory;
    }

    /// <summary>
    /// Whether the site's process account belongs to the computer rather than a domain, as on a
    /// computer that is not domain-joined: Windows then reports the computer's name as its domain.
    /// </summary>
    public static bool IsOutsideDomain
        => string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
}
