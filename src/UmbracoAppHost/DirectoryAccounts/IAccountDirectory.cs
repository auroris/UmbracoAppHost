namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>
/// The people an administrator can create accounts for: the directory the site's sign-ins come
/// from, behind one small interface so the backoffice does not depend on where it is. The "Add
/// from directory" dialog searches it, and account creation reads the chosen person from it again
/// by SID, so nothing about the person is ever taken from the browser.
/// </summary>
/// <remarks>
/// One implementation is chosen at startup by <see cref="AccountDirectoryExtensions.AddAccountDirectory"/>:
/// Active Directory queried in process (<see cref="ActiveDirectoryAccountDirectory"/>), an external
/// identity server's directory API (<see cref="IdentityServerAccountDirectory"/>), or the local
/// accounts of a computer that is not in a domain (<see cref="LocalAccountDirectory"/>). All of
/// them throw the directory exceptions: <see cref="DirectoryQueryException"/> for a request
/// that cannot be run as asked, with a message for the person who asked, and
/// <see cref="DirectoryUnavailableException"/> when the directory cannot be reached.
/// </remarks>
public interface IAccountDirectory
{
    /// <summary>The most people a search returns, whatever the caller asks for.</summary>
    const int MaxTake = 50;

    /// <summary>The shortest search term accepted, so a single letter cannot list the whole directory.</summary>
    const int MinTermLength = 2;

    /// <summary>
    /// Searches the whole directory for enabled accounts matching every term of the query, sorted
    /// by display name. Disabled accounts are left out: nobody can sign in with them.
    /// </summary>
    /// <param name="query">Search terms separated by spaces or commas, each at least <see cref="MinTermLength"/> characters.</param>
    /// <param name="take">How many people to return, between 1 and <see cref="MaxTake"/>.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the directory.</param>
    Task<IReadOnlyList<DirectoryUser>> SearchAsync(string query, int take, CancellationToken cancellationToken = default);

    /// <summary>Looks up one account by its SID, enabled or not, with the groups it belongs to directly.</summary>
    /// <param name="sid">The account's security identifier, <c>S-1-5-21-...</c>.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the directory.</param>
    /// <returns>The account, or null when the directory has none with this SID.</returns>
    Task<DirectoryUserDetail?> FindAsync(string sid, CancellationToken cancellationToken = default);
}
