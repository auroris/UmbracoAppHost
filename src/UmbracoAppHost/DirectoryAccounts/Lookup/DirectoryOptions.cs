namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>
/// Settings for directory lookups, bound from the <c>Directory</c> configuration section.
/// A root is the starting location of a search in the directory tree. These roots control which
/// records the lookup can find; they do not grant the process account permission to read them.
/// </summary>
public sealed class DirectoryOptions
{
    /// <summary>The configuration section the people-picker lookups bind these options from.</summary>
    public const string SectionName = "Directory";

    /// <summary>
    /// The LDAP path searched for people in local scope,
    /// e.g. <c>LDAP://OU=People,DC=EXAMPLE,DC=COM</c>. Empty searches the whole domain.
    /// </summary>
    public string? UserRoot { get; set; }

    /// <summary>
    /// The LDAP path searched for people in global scope and for single-account lookups
    /// (across organizational units). Empty searches the whole domain.
    /// </summary>
    public string? UserRootGlobal { get; set; }

    /// <summary>
    /// The shortest term a global people search accepts, so a one-letter query cannot sweep the
    /// whole directory. Values below 1 mean 2.
    /// </summary>
    public int GlobalSearchMinLength { get; set; } = 2;

    /// <summary>
    /// How many more entries than requested a people search asks the domain controller for, so
    /// accounts the enabled-account check drops after the query are less likely to leave the page short.
    /// Despite the name, this buffer applies to both local and global searches. It is bounded
    /// over-fetching, not pagination, so enough excluded accounts can still leave fewer results.
    /// </summary>
    public int GlobalSearchSizeLimitBuffer { get; set; } = 20;

    /// <summary>Results a search returns when the caller does not say how many it wants.</summary>
    public int DefaultTake { get; set; } = 20;

    /// <summary>The most results a search returns, whatever the caller asks for.</summary>
    public int MaxTake { get; set; } = 50;
}

/// <summary>
/// Which configured root a people search uses. Local/global describe search coverage, not whether
/// a user is a local Windows account or a domain account; both scopes query Active Directory.
/// </summary>
public enum DirectoryScope
{
    /// <summary>The local organizational unit, <see cref="DirectoryOptions.UserRoot"/>.</summary>
    Local,

    /// <summary>
    /// The broader configured root, <see cref="DirectoryOptions.UserRootGlobal"/>; this need not
    /// cover the whole domain or forest. Every term must meet
    /// <see cref="DirectoryOptions.GlobalSearchMinLength"/>.
    /// </summary>
    Global,
}
