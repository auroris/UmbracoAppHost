using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Community.UmbNav.Core.Models;
using AppBlocks.Documents.PaperReports;
using System.Text.Json;

namespace UmbracoAppHost.Website;

/// <summary>The site containing a page, and the labels shared by its menus and layouts.</summary>
public static class SiteContent
{
    public static bool IsWebsite(IPublishedContent page) => page.ContentType.Alias is "appHostWebsite" or "webSite" or "blockWebSite"
        || page.HasProperty("siteTitle");
    public static bool IsSite(IPublishedContent page) => IsWebsite(page) || page.ContentType.Alias == "application" || page.HasProperty("theme");

    public static IPublishedContent SiteRoot(this IPublishedContent page, IDocumentNavigationQueryService navigation,
        IPublishedContentStatusFilteringService published)
        => FindSite(page.AncestorsOrSelf(navigation, published));

    public static IPublishedContent FindSite(IEnumerable<IPublishedContent> ancestorsAndSelf)
    {
        var branch = ancestorsAndSelf.OrderByDescending(node => node.Level).ToArray();
        return branch.FirstOrDefault(IsSite) ?? branch.Last();
    }

    public static string PageTitle(this IPublishedContent page) => Text(page, "title") ?? page.Name;
    public static string NavigationTitle(this IPublishedContent page) => Text(page, "siteTitle") ?? page.PageTitle();
    public static string NavigationUrl(this IPublishedContent page) => Text(page, "link") ?? page.Url();

    public static string? Text(IPublishedContent page, string alias)
        => page.Value<string>(alias) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>The pages beneath a page that menus and cards list. A paper report is a download, not a page.</summary>
    public static IEnumerable<IPublishedContent> VisibleChildren(this IPublishedContent page,
        IDocumentNavigationQueryService navigation, IPublishedContentStatusFilteringService published)
        => page.Children(navigation, published).Where(node => node.IsVisible() && node.ContentType.Alias != PaperReportDefinition.Alias)
            .OrderBy(node => node.SortOrder);

    /// <summary>
    /// Select the nearest authored menu before visitor filtering. Empty lists inherit; a menu whose entries are
    /// hidden from this visitor stays empty and must not expose a different ancestor's menu.
    /// </summary>
    public static IReadOnlyList<UmbNavItem>? NavigationMenu(this IPublishedContent page,
        IDocumentNavigationQueryService navigation, IPublishedContentStatusFilteringService published)
    {
        foreach (var node in page.AncestorsOrSelf(navigation, published).OrderByDescending(node => node.Level))
        {
            var property = node.GetProperty("topNavigationMenu");
            if (!HasAuthoredMenu(property?.GetSourceValue())) continue;
            return node.Value<IEnumerable<UmbNavItem>>("topNavigationMenu")?.ToArray() ?? [];
        }
        return null;
    }

    public static bool HasAuthoredMenu(object? source)
    {
        if (string.IsNullOrWhiteSpace(source?.ToString())) return false;
        try
        {
            using var json = JsonDocument.Parse(source.ToString()!);
            return json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0;
        }
        // An invalid local value must not accidentally reveal another owner's menu either.
        catch (JsonException) { return true; }
    }

    public static IReadOnlyList<SiteNavigationItem> LocalNavigation(this IPublishedContent page,
        IDocumentNavigationQueryService navigation, IPublishedContentStatusFilteringService published)
    {
        var entries = new List<SiteNavigationItem>();
        var parent = page.Parent<IPublishedContent>(navigation, published);
        if (IsSite(page) || parent is null)
        {
            entries.Add(new(page, "self"));
            entries.AddRange(page.VisibleChildren(navigation, published).Select(node => new SiteNavigationItem(node, "child")));
            var group = Text(page, "group");
            if (parent is not null)
                entries.AddRange(parent.VisibleChildren(navigation, published)
                    .Where(node => node.Key != page.Key && (group is null || string.Equals(Text(node, "group"), group, StringComparison.OrdinalIgnoreCase)))
                    .Select(node => new SiteNavigationItem(node, "sibling")));
        }
        else
        {
            entries.Add(new(parent, "parent"));
            foreach (var sibling in parent.VisibleChildren(navigation, published))
            {
                entries.Add(new(sibling, sibling.Key == page.Key ? "self" : "sibling"));
                if (sibling.Key == page.Key)
                    entries.AddRange(page.VisibleChildren(navigation, published).Select(node => new SiteNavigationItem(node, "child")));
            }
        }
        return entries;
    }
}

public sealed record SiteNavigationItem(IPublishedContent Page, string Relation);
