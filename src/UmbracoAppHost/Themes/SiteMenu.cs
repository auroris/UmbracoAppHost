using System.Text.RegularExpressions;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Community.UmbNav.Core.Extensions;
using Umbraco.Community.UmbNav.Core.Models;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Website;

/// <summary>An entry of the rendered site menu: an authored item after its tokens were replaced for the visitor.</summary>
/// <param name="SignsOut">The entry is the application's sign-out, which the shells render as a form: signing out is a POST.</param>
public sealed record SiteMenuEntry(string Label, string? Url, string? Target, bool SignsOut, IReadOnlyList<SiteMenuEntry> Children);

/// <summary>
/// The authored navigation menu as the person looking at the page should see it. Labels and link
/// addresses may carry tokens: <c>{{user.name}}</c>, <c>{{user.username}}</c> and <c>{{user.email}}</c>
/// for the signed-in member or backoffice Administrator, <c>{{user.profile}}</c> for the profile page
/// picked on the nearest Navigation Menu (members only), <c>{{user.backoffice}}</c> for the backoffice
/// (backoffice users only) and <c>{{user.signout}}</c> for the application's sign-out. An entry whose
/// label or address has a token with no value for this visitor is left out with its children, so
/// "Signed in as {{user.name}}" appears only to someone signed in. A token nobody knows stays as
/// typed, so a typo shows. UmbNav's own display settings (hide when logged in or out) are applied by
/// UmbNav when the menu is read, which happens after the visitor is resolved so that a backoffice
/// Administrator counts as signed in there too.
/// </summary>
public sealed partial class SiteMenu(SiteVisitor visitor, IHttpContextAccessor http)
{
    /// <summary>The application's sign-out action, which <see cref="ApplicationAccountController"/> serves.</summary>
    public const string SignOutPath = "/application/account/logout";

    /// <summary>The page's menu for the current visitor; empty when the site has no authored menu.</summary>
    public async Task<IReadOnlyList<SiteMenuEntry>> BuildAsync(IPublishedContent page,
        IDocumentNavigationQueryService navigation, IPublishedContentStatusFilteringService published)
    {
        // Resolving the visitor first puts a backoffice Administrator on the request, which UmbNav's display settings read.
        await visitor.ResolveAsync();
        var items = page.NavigationMenu(navigation, published);
        if (items is null) return [];
        var pathBase = http.HttpContext?.Request.PathBase.Value ?? "";
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user.name"] = visitor.Name,
            ["user.username"] = visitor.UserName,
            ["user.email"] = visitor.Email,
            ["user.profile"] = visitor.IsMember ? ProfileUrl(page, navigation, published) : null,
            ["user.backoffice"] = visitor.IsBackOfficeUser ? pathBase + "/umbraco" : null,
            ["user.signout"] = visitor.IsSignedIn ? pathBase + SignOutPath : null,
        };
        return Build(items, values, pathBase + SignOutPath);
    }

    /// <summary>The profile page the nearest Navigation Menu picked, in the page's culture; null without one.</summary>
    private static string? ProfileUrl(IPublishedContent page, IDocumentNavigationQueryService navigation, IPublishedContentStatusFilteringService published)
    {
        var profile = page.AncestorsOrSelf(navigation, published).OrderByDescending(node => node.Level)
            .Select(node => node.HasProperty("profilePage") ? node.Value<IPublishedContent>("profilePage") : null)
            .FirstOrDefault(picked => picked is not null);
        return profile?.Url() is { Length: > 0 } url && url != "#" ? url : null;
    }

    private static IReadOnlyList<SiteMenuEntry> Build(IEnumerable<UmbNavItem> items, IReadOnlyDictionary<string, string?> values, string signOut)
    {
        var entries = new List<SiteMenuEntry>();
        foreach (var item in items)
        {
            if (Replace(item.Name, values) is not { } label) continue;
            string? url = null;
            // A Title groups children without linking; UmbNav answers "#" for an item without an address.
            if (!string.Equals(item.ItemType?.ToString(), "Title", StringComparison.OrdinalIgnoreCase))
            {
                if (Replace(item.Url(), values) is not { } address) continue;
                url = string.IsNullOrWhiteSpace(address) || address == "#" ? null : address;
            }
            var signsOut = url is not null && string.Equals(url, signOut, StringComparison.OrdinalIgnoreCase);
            entries.Add(new(label, url, url is null ? null : item.Target, signsOut, Build(item.Children ?? [], values, signOut)));
        }
        return entries;
    }

    /// <summary>The text with its tokens replaced; null when a known token has no value for this visitor.</summary>
    public static string? Replace(string? text, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var missing = false;
        var replaced = Token().Replace(text, match =>
        {
            if (!values.TryGetValue(match.Groups["name"].Value, out var value)) return match.Value;
            if (string.IsNullOrEmpty(value)) missing = true;
            return value ?? "";
        });
        return missing ? null : replaced;
    }

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z][A-Za-z0-9_.-]*)\s*\}\}")]
    private static partial Regex Token();
}
