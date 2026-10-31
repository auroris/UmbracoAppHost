using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Community.UmbNav.Core.Models;
using AppBlocks.Documents.PaperReports;

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
    /// The nearest authored top navigation menu. A menu an editor emptied is saved as an empty list, which would end
    /// Umbraco's ancestor fallback, so the walk takes the first menu that has items.
    /// </summary>
    public static IReadOnlyList<UmbNavItem>? NavigationMenu(this IPublishedContent page,
        IDocumentNavigationQueryService navigation, IPublishedContentStatusFilteringService published)
        => page.AncestorsOrSelf(navigation, published).OrderByDescending(node => node.Level)
            .Select(node => node.Value<IEnumerable<UmbNavItem>>("topNavigationMenu")?.ToArray())
            .FirstOrDefault(items => items is { Length: > 0 });

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
