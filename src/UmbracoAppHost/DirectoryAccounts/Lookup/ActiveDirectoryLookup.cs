using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using ActiveDirectory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ActiveDirectory.ADObject;

namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>
/// <see cref="IDirectoryLookup"/> over Active Directory, through the shared <c>ActiveDirectory</c>
/// library. Queries run as the process account (the application pool identity under IIS), which
/// needs read access to the configured roots, on a domain-joined server.
/// </summary>
/// <remarks>
/// System.DirectoryServices is synchronous, so each method has done its work by the time it
/// returns; the methods are asynchronous in shape so that callers and test doubles do not depend
/// on that. Register it as a singleton: it caches domain names.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ActiveDirectoryLookup : IDirectoryLookup
{
    private static readonly StringComparer NameComparer = StringComparer.InvariantCultureIgnoreCase;

    // Fetch the fields needed to build DirectoryUser in one search. Reading a cached field that
    // was not requested looks like a missing value; binding every result to fetch it later would
    // add a network connection per entry.
    private static readonly string[] UserProperties =
    [
        ADProperties.ObjectSid,
        ADProperties.CommonName,
        ADProperties.DistinguishedName,
        ADProperties.UserAccountControl,
        ADProperties.User.SamAccountName,
        ADProperties.User.DisplayName,
        ADProperties.User.GivenName,
        ADProperties.User.Surname,
        ADProperties.User.Email,
        ADProperties.User.TelephoneNumber,
        ADProperties.User.MobilePhone,
        ADProperties.User.FaxNumber,
        ADProperties.User.Office,
        ADProperties.User.Title,
        ADProperties.User.Department,
        ADProperties.User.EmployeeID,
    ];

    // An unpaged search stops at the domain controller's MaxPageSize, 1000 unless changed.
    private const int ServerPageSize = 1000;

    private readonly IOptionsMonitor<DirectoryOptions> _options;
    private readonly ILogger<ActiveDirectoryLookup> _logger;

    // NetBIOS domain names by domain SID; see DomainOf.
    private readonly ConcurrentDictionary<string, string> _domainNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the lookup.</summary>
    /// <param name="options">The <c>Directory</c> settings; changes apply to the next query.</param>
    /// <param name="logger">Receives query counts/timings at Debug level and directory failures at Error level.</param>
    public ActiveDirectoryLookup(IOptionsMonitor<DirectoryOptions> options, ILogger<ActiveDirectoryLookup>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<ActiveDirectoryLookup>.Instance;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string? query, int? take = null, DirectoryScope scope = DirectoryScope.Local, bool includeDisabled = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryOptions options = _options.CurrentValue;
        IReadOnlyList<string> terms = DirectoryQuery.Tokenize(query);
        if (scope == DirectoryScope.Global)
            DirectoryQuery.EnsureGlobalTerms(terms, options.GlobalSearchMinLength);
        int limit = DirectoryQuery.ClampTake(take, options.DefaultTake, options.MaxTake);

        return Task.FromResult(Run(() => SearchPeople(options, terms, limit, scope, includeDisabled)));
    }

    /// <inheritdoc/>
    public Task<DirectoryUserDetail?> FindUserAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryId key = DirectoryId.Parse(id, allowAccountName: true);
        DirectoryOptions options = _options.CurrentValue;

        return Task.FromResult(Run(() =>
        {
            using ADUser? user = FindPerson(options, key);
            return user == null ? null : new DirectoryUserDetail(ToUser(user), GroupNames(user.Groups));
        }));
    }

    internal DirectorySearch CreatePeopleSearch(DirectoryOptions options, IReadOnlyList<string> terms, DirectoryScope scope, bool includeDisabled)
    {
        string? root = Root(scope == DirectoryScope.Global ? options.UserRootGlobal : options.UserRoot);
        var search = new DirectorySearch(root, ObjectClass.User)
        {
            AdditionalFilter = DirectoryQuery.PeopleFilter(terms, includeDisabled),
        };
        search.PropertiesToLoad.AddRange(UserProperties);
        return search;
    }

    private IReadOnlyList<DirectoryUser> SearchPeople(DirectoryOptions options, IReadOnlyList<string> terms, int limit, DirectoryScope scope, bool includeDisabled)
    {
        var search = CreatePeopleSearch(options, terms, scope, includeDisabled);

        // Fetch a bounded buffer before enabled-account checks and result limiting.
        // This is not pagination: if many entries are excluded, fewer than limit may still remain.
        int sizeLimit = limit + Math.Max(0, options.GlobalSearchSizeLimitBuffer);
        if (sizeLimit > ServerPageSize)
            search.PageSize = ServerPageSize;

        List<ADUser> found = Find<ADUser>(search, sizeLimit, scope == DirectoryScope.Global ? "global people search" : "local people search");
        try
        {
            var users = found
                .Where(user => includeDisabled || !user.IsDisabled)
                .Select(ToUser)
                .OrderBy(user => user.DisplayName, NameComparer)
                .Take(limit)
                .ToList();
            _logger.LogDebug("Directory people search in {Scope} scope returned {Count} users from {Fetched} fetched entries after filtering and applying the result limit {Limit} (includeDisabled: {IncludeDisabled}). Scope selects Directory:UserRoot or UserRootGlobal; excluded accounts and result limits can shorten the list.",
                scope, users.Count, found.Count, limit, includeDisabled);
            return users;
        }
        finally
        {
            DisposeAll(found);
        }
    }

    private ADUser? FindPerson(DirectoryOptions options, DirectoryId key)
    {
        // A picker may be limited to one organizational unit, but resolving a saved identity must
        // also find an account that has moved outside that unit. Use the broader configured root.
        var search = new DirectorySearch(Root(options.UserRootGlobal), ObjectClass.User)
        {
            AdditionalFilter = DirectoryQuery.All(["(" + ADProperties.ObjectCategory + "=person)", key.Assertion()]),
        };
        search.PropertiesToLoad.AddRange(UserProperties);
        search.PropertiesToLoad.Add(ADProperties.MemberOf);

        ADUser? user = FindOne<ADUser>(search, "user lookup");
        if (user != null && key.Kind == DirectoryIdKind.AccountName
            && !string.Equals(DomainOf(user), key.Domain, StringComparison.OrdinalIgnoreCase))
        {
            // The same user name in another domain is another account.
            user.Dispose();
            return null;
        }
        return user;
    }

    private DirectoryUser ToUser(ADUser user)
    {
        string userName = user.Username;
        string? domain = DomainOf(user);
        return new DirectoryUser
        {
            Sid = user.Sid,
            Guid = GuidOf(user),
            AccountName = domain != null ? domain + "\\" + userName : userName,
            UserName = userName,
            DisplayName = FirstNonEmpty(user.DisplayName, user.Name, userName),
            GivenName = NonEmpty(user.GivenName),
            Surname = NonEmpty(user.Surname),
            Email = NonEmpty(user.Email),
            Telephone = NonEmpty(user.TelephoneNumber),
            Mobile = NonEmpty(user.MobilePhone),
            Fax = NonEmpty(user.FaxNumber),
            Office = NonEmpty(user.Office),
            Title = NonEmpty(user.Title),
            Department = NonEmpty(user.Department),
            EmployeeId = NonEmpty(user.EmployeeID),
            Enabled = !user.IsDisabled,
        };
    }

    /// <summary>
    /// The NetBIOS name of an account's domain, the <c>DOMAIN</c> of <c>DOMAIN\user</c> in a logon
    /// token. The directory does not store it on the account, so Windows translates the SID of the
    /// first account seen from each domain and the answer is cached by domain SID. When that fails,
    /// the first <c>DC=</c> label of the distinguished name stands in; in most domains it is the
    /// same name.
    /// </summary>
    private string? DomainOf(ADObject account)
    {
        string? fallback = DirectoryQuery.DomainLabel(account.QualifiedName);
        string sid = account.Sid;
        if (sid.Length == 0) return fallback;

        string? domainSid;
        SecurityIdentifier identifier;
        try
        {
            identifier = new SecurityIdentifier(sid);
            domainSid = identifier.AccountDomainSid?.Value;
        }
        catch (ArgumentException)
        {
            return fallback;
        }
        if (domainSid == null) return fallback;
        if (_domainNames.TryGetValue(domainSid, out string? cached)) return cached;

        string? name = null;
        try
        {
            string accountName = identifier.Translate(typeof(NTAccount)).Value;
            int separator = accountName.IndexOf('\\');
            if (separator > 0) name = accountName[..separator];
        }
        catch (SystemException ex)
        {
            _logger.LogDebug(ex, "Could not translate Windows security identifier {Sid} to an account name; using the directory's domain label instead. This fallback can differ from the DOMAIN part of a Windows logon name, so check domain connectivity if account names appear incorrect.", sid);
        }

        name ??= fallback;
        if (name != null) _domainNames.TryAdd(domainSid, name);
        return name;
    }

    private static IReadOnlyList<string> GroupNames(IEnumerable<string> distinguishedNames) =>
        distinguishedNames
            .Select(DirectoryQuery.CommonName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, NameComparer)
            .ToList();

    private List<T> Find<T>(DirectorySearch search, int sizeLimit, string purpose) where T : ADObject, new()
    {
        var stopwatch = Stopwatch.StartNew();
        List<T> found = search.Find<T>(sizeLimit);
        // Counts and timings explain query cost without recording people's search text or LDAP
        // filters, which can include names, email addresses and employee identifiers.
        _logger.LogDebug("Directory {Purpose} returned {Count} entries with size limit {SizeLimit} in {Elapsed} ms. This count is before application filtering; reaching the limit does not mean the entire directory was returned.",
            purpose, found.Count, sizeLimit, stopwatch.ElapsedMilliseconds);
        if (found.Count == 0)
            _logger.LogDebug("Directory {Purpose} completed with no matches. Check the search scope, configured Directory roots and filters; an empty result is not a connection failure.", purpose);
        return found;
    }

    private T? FindOne<T>(DirectorySearch search, string purpose) where T : ADObject, new()
    {
        List<T> found = Find<T>(search, 1, purpose);
        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>
    /// Runs a directory query, turning the failures System.DirectoryServices and the
    /// ActiveDirectory library report into <see cref="DirectoryUnavailableException"/>.
    /// </summary>
    private T Run<T>(Func<T> query)
    {
        try
        {
            return query();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // COMException includes DirectoryServicesCOMException: no domain controller, a bad
            // LDAP root, access denied. InvalidOperationException comes from the ActiveDirectory
            // library when the directory returns something it cannot type.
            // Keep a failed lookup distinct from a successful empty result: returning an empty
            // list here would make a directory outage look as though all matching people disappeared.
            _logger.LogError(ex, "Active Directory lookup failed. Queries use the server process account, not the signed-in browser user. Check the exception, Windows/domain connectivity, Directory:UserRoot/UserRootGlobal and that account's read permissions.");
            throw new DirectoryUnavailableException("Active Directory could not be queried: " + ex.Message, ex);
        }
    }

    private static string GuidOf(ADObject entry) =>
        entry.ADGuid.Bytes is { Length: 16 } bytes ? new Guid(bytes).ToString("D") : "";

    private static string? Root(string? ldapPath) =>
        string.IsNullOrWhiteSpace(ldapPath) ? null : ldapPath.Trim();

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static void DisposeAll<T>(IEnumerable<T> entries) where T : IDisposable
    {
        foreach (T entry in entries)
            entry.Dispose();
    }
}
