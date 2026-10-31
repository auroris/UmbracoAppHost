namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>
/// Active Directory, queried in process through the local people-picker lookup, for a site that signs
/// people in through its own embedded identity server: the site authenticates them against the
/// domain it is in, so that domain's directory is theirs. Queries run as the site's process account
/// (the application pool identity in IIS), which needs read access to the roots configured in the
/// <c>Directory</c> section.
/// </summary>
public sealed class ActiveDirectoryAccountDirectory(IDirectoryLookup lookup) : IAccountDirectory
{
    /// <remarks>
    /// The local lookup has no telephone-directory listing rules. Exclude disabled accounts in
    /// LDAP before applying the result limit, and also guard results from custom lookup providers.
    /// </remarks>
    public async Task<IReadOnlyList<DirectoryUser>> SearchAsync(string query, int take, CancellationToken cancellationToken = default)
    {
        var people = await lookup.SearchUsersAsync(
            query, Math.Clamp(take, 1, IAccountDirectory.MaxTake), DirectoryScope.Global, includeDisabled: false, cancellationToken);
        return people.Where(person => person.Enabled).ToList();
    }

    public Task<DirectoryUserDetail?> FindAsync(string sid, CancellationToken cancellationToken = default)
    {
        // The lookup also accepts a GUID or DOMAIN\user; this interface promises a SID.
        AccountDirectoryRules.EnsureSid(sid);
        return lookup.FindUserAsync(sid, cancellationToken);
    }
}
