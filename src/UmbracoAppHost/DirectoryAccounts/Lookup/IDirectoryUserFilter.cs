using ActiveDirectory;

namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>Trusted per-caller search rules; the backoffice's unfiltered lookup remains unchanged.</summary>
public interface IDirectoryUserFilter
{
    IReadOnlyList<string> AdditionalProperties { get; }
    /// <summary>Parenthesized LDAP assertions from application code, never raw browser input.</summary>
    string LdapFilter { get; }
    bool IsIncluded(ADUser user);
}

public enum DirectorySearchMode { AddressBook, DisplayName }
