using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>
/// Read-only people searches and single-account lookups for the people pickers.
/// </summary>
/// <remarks>
/// <para>
/// Implementations escape every value they put in a query and bound every result set. A request
/// that cannot be run as asked throws <see cref="DirectoryQueryException"/>; an unreachable
/// directory throws <see cref="DirectoryUnavailableException"/>.
/// </para>
/// <para>
/// This folder is UmbracoAppHost's own fork of an <c>IdentityServer.Directory</c> library, cut down to what the
/// people pickers need. The two evolve separately: nothing keeps them in step, and differences are expected.
/// </para>
/// </remarks>
public interface IDirectoryLookup
{
    /// <summary>
    /// Address-book search for people. The query splits into terms on spaces and commas, and an
    /// account matches when every term matches one of its display name, given name, surname, user
    /// name, email, telephone number or employee ID. Unless <paramref name="includeDisabled"/>,
    /// only enabled accounts are returned. Results are sorted by display name.
    /// </summary>
    /// <param name="query">The search terms. No terms lists the local scope; global scope requires one.</param>
    /// <param name="take">How many results to return; null or less than 1 means <see cref="DirectoryOptions.DefaultTake"/>, at most <see cref="DirectoryOptions.MaxTake"/>.</param>
    /// <param name="scope">The local organizational unit or the whole directory.</param>
    /// <param name="includeDisabled">Include disabled accounts.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the directory.</param>
    /// <param name="filter">A caller's trusted LDAP assertion, additional attributes and result predicate; null keeps the standard account picker.</param>
    /// <param name="mode">AddressBook searches the standard fields; DisplayName matches every term against displayName alone.</param>
    Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string? query, int? take = null, DirectoryScope scope = DirectoryScope.Local, bool includeDisabled = false, CancellationToken cancellationToken = default,
        IDirectoryUserFilter? filter = null, DirectorySearchMode mode = DirectorySearchMode.AddressBook);

    /// <summary>
    /// Looks up one account, regardless of its enabled state, with the
    /// groups it belongs to directly. Searches <see cref="DirectoryOptions.UserRootGlobal"/>.
    /// </summary>
    /// <param name="id">A SID, an objectGUID (36 characters), or <c>DOMAIN\user</c>.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the directory.</param>
    /// <param name="additionalAttributes">Additional directory properties to return in DirectoryUser.Attributes.</param>
    /// <returns>The account, or null when there is none.</returns>
    Task<DirectoryUserDetail?> FindUserAsync(string id, CancellationToken cancellationToken = default, IReadOnlyList<string>? additionalAttributes = null);
}
