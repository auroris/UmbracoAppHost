using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Account facts for explicit provisioning, which can be read from identity-server claims.
/// The OpenID Connect login flow passes its principal directly to Umbraco instead.
/// </summary>
/// <param name="ProviderKey">The stable key stored as the external login: the Windows SID, which is also the identity server's subject.</param>
/// <param name="AccountName">The Windows account name, <c>DOMAIN\user</c> or <c>MACHINE\user</c>.</param>
/// <param name="UserName">The part of <paramref name="AccountName"/> after the backslash.</param>
public sealed partial record SsoAccount(
    string ProviderKey,
    string AccountName,
    string UserName,
    string DisplayName,
    string? Email,
    string? GivenName,
    string? Surname,
    bool IsDomainAccount,
    IReadOnlyList<string> Groups)
{
    /// <summary>The claim type carrying the Windows account name (<c>DOMAIN\user</c>).</summary>
    public const string AccountNameClaim = ClaimTypes.WindowsAccountName;

    /// <summary>
    /// The email Umbraco stores. Umbraco requires one, and local accounts have none, so those get
    /// a synthetic <c>user@localhost</c> address, which is also what the identity server sends.
    /// </summary>
    public string EffectiveEmail => Email ?? $"{UserName}@localhost";

    /// <summary>
    /// The user name in the form the Umbraco 13 site stored: diacritics folded to ASCII, no
    /// domain prefix, only the characters Identity allows in a user name (O'Brian becomes OBrian).
    /// </summary>
    public string NormalizedUserName => NormalizeUserName(UserName);

    /// <summary>Usernames an administrator may have given a pre-created account for this person.</summary>
    public IEnumerable<string> UserNameCandidates =>
        new[] { EffectiveEmail, AccountName, UserName, NormalizedUserName, $"{UserName}@localhost" }
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an Umbraco group name names one of this account's directory groups, comparing both
    /// the full <c>DOMAIN\Group</c> form and the bare group name, ignoring case.
    /// </summary>
    public bool MatchesGroup(string? umbracoGroupName)
    {
        if (string.IsNullOrWhiteSpace(umbracoGroupName)) return false;
        foreach (var group in Groups)
        {
            if (string.Equals(group, umbracoGroupName, StringComparison.OrdinalIgnoreCase)) return true;
            var backslash = group.IndexOf('\\');
            if (backslash >= 0 && string.Equals(group[(backslash + 1)..], umbracoGroupName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Claims the external-login cookie carries. The provider key is the stable identifier.</summary>
    public IEnumerable<Claim> ToClaims()
    {
        yield return new Claim(ClaimTypes.NameIdentifier, ProviderKey);
        yield return new Claim(ClaimTypes.Name, DisplayName);
        yield return new Claim(AccountNameClaim, AccountName);
        yield return new Claim(ClaimTypes.Email, EffectiveEmail);
        if (GivenName is not null) yield return new Claim(ClaimTypes.GivenName, GivenName);
        if (Surname is not null) yield return new Claim(ClaimTypes.Surname, Surname);
        foreach (var group in Groups) yield return new Claim(ClaimTypes.Role, group);
    }

    /// <summary>
    /// Reads the account from the claims an identity server sent. The identity server issues the
    /// SID as the subject, the display name as the name, the Windows account name, the email
    /// (synthetic for local accounts), and one role per directory group: the same shape
    /// <see cref="ToClaims"/> produces for explicit provisioning.
    /// </summary>
    public static SsoAccount FromPrincipal(ClaimsPrincipal principal)
    {
        var key = First(principal, ClaimTypes.NameIdentifier, "sub")
            ?? throw new InvalidOperationException("The identity server did not send a subject claim.");
        var accountName = First(principal, ClaimTypes.WindowsAccountName, "preferred_username", ClaimTypes.Name, "name") ?? key;
        var (domain, userName) = SplitAccountName(accountName);
        var displayName = First(principal, ClaimTypes.Name, "name") ?? userName;
        var groups = principal.FindAll(claim => claim.Type is ClaimTypes.Role or "role" or "roles")
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SsoAccount(
            key,
            accountName,
            userName,
            displayName,
            NonEmpty(First(principal, ClaimTypes.Email, "email")),
            NonEmpty(First(principal, ClaimTypes.GivenName, "given_name")),
            NonEmpty(First(principal, ClaimTypes.Surname, "family_name")),
            IsDomain(domain),
            groups);
    }

    /// <summary>Folds a user name to the characters Identity's default <c>AllowedUserNameCharacters</c> permits.</summary>
    public static string NormalizeUserName(string userName)
    {
        var builder = new StringBuilder(userName.Length);
        foreach (var character in userName.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }
        var folded = builder.ToString();
        var backslash = folded.IndexOf('\\');
        if (backslash >= 0) folded = folded[(backslash + 1)..];
        return DisallowedUserNameCharacters().Replace(folded, "");
    }

    [GeneratedRegex("[^A-Za-z0-9\\-.+@]")]
    private static partial Regex DisallowedUserNameCharacters();

    private static (string Domain, string UserName) SplitAccountName(string accountName)
    {
        var backslash = accountName.IndexOf('\\');
        return backslash > 0
            ? (accountName[..backslash], accountName[(backslash + 1)..])
            : (Environment.MachineName, accountName);
    }

    private static bool IsDomain(string domain)
        => !string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(domain, "NT AUTHORITY", StringComparison.OrdinalIgnoreCase);

    private static string? First(ClaimsPrincipal principal, params string[] types)
    {
        foreach (var type in types)
        {
            var value = principal.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
