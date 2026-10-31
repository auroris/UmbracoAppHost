using System.Text.RegularExpressions;

namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>
/// The request rules every <see cref="IAccountDirectory"/> applies the same way, whether the
/// directory checks them itself or not, so a search refused in one mode is refused in all of them
/// with the same message.
/// </summary>
public static partial class AccountDirectoryRules
{
    /// <summary>Whether a value is a Windows security identifier in its string form, <c>S-1-5-21-...</c>.</summary>
    public static bool IsSid(string? value) => value is not null && SidPattern().IsMatch(value);

    /// <summary>Refuses anything but a SID, before it gets anywhere near a directory query.</summary>
    /// <exception cref="DirectoryQueryException">The value is not a SID.</exception>
    public static void EnsureSid(string? sid)
    {
        if (!IsSid(sid))
            throw new DirectoryQueryException("The person must be identified by the SID of their Windows account (S-1-5-21-...).");
    }

    /// <summary>
    /// Splits a search into its terms, as the local lookup does (spaces and commas, at most eight
    /// terms and 256 characters), and requires at least one term, each of at least
    /// <see cref="IAccountDirectory.MinTermLength"/> characters.
    /// </summary>
    /// <exception cref="DirectoryQueryException">The search is empty, too long, or has a term that is too short.</exception>
    public static IReadOnlyList<string> SearchTerms(string? query)
    {
        var terms = DirectoryQuery.Tokenize(query);
        DirectoryQuery.EnsureGlobalTerms(terms, IAccountDirectory.MinTermLength);
        return terms;
    }

    /// <summary>The same pattern the local lookup accepts as a SID.</summary>
    [GeneratedRegex(@"^S-1-\d{1,15}(-\d{1,10}){1,15}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();
}
