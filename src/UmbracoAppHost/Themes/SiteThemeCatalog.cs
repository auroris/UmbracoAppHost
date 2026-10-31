using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Website;

namespace UmbracoAppHost.Themes;

/// <summary>A theme's layouts. Each layout supplies its own stylesheet and styles the shared block hooks.</summary>
public sealed record SiteTheme(string Alias, string PageLayout, string SignInLayout, string? PrintStylesheet = null)
{
    public string LayoutFor(bool signedIn) => signedIn ? PageLayout : SignInLayout;
}

/// <summary>
/// Resolves the explicit theme on a site root. Register another <see cref="SiteTheme"/> in DI to add a
/// theme without adding a root document type. Existing sites retain their document type's default.
/// </summary>
public sealed class SiteThemeCatalog
{
    public static readonly SiteTheme Application = new("application", "AppMaster.cshtml", "AppSignIn.cshtml", "/App_Plugins/PaperReports/application-print.css");
    public static readonly SiteTheme Website = new("website", "WebsiteLayout.cshtml", "WebsiteSignIn.cshtml", "/App_Plugins/PaperReports/website-print.css");
    private readonly Dictionary<string, SiteTheme> _themes = new(StringComparer.OrdinalIgnoreCase) {
        [Application.Alias] = Application, [Website.Alias] = Website,
    };

    public SiteThemeCatalog(IEnumerable<SiteTheme> themes)
    {
        foreach (var theme in themes)
            _themes[theme.Alias] = theme;
    }

    public SiteTheme Resolve(IPublishedContent root) => Resolve(root.Value<string>("theme"), root.ContentType.Alias);

    public async Task<SiteTheme> ResolveForAsync(IPublishedContent page, IDocumentNavigationQueryService navigation,
        IPublishedContentStatusFilteringService published, ITemplateService templates, SiteTheme? defaultTheme = null)
    {
        var site = page.SiteRoot(navigation, published);
        var alias = SiteContent.Text(site, "theme");
        // The site's template is read only when neither an explicit theme nor the view's default decides.
        var template = Find(alias) is null && defaultTheme is null && site.TemplateId is { } templateId ? (await templates.GetAsync(templateId))?.Alias : null;
        return Resolve(alias, site.ContentType.Alias, defaultTheme, template);
    }

    /// <summary>
    /// A registered explicit choice wins, then the view's own default (Website home and Application site pass
    /// theirs), then the Application site template on the root, then the root's document type.
    /// </summary>
    public SiteTheme Resolve(string? alias, string rootType, SiteTheme? defaultTheme, string? siteTemplate) =>
        Find(alias) ?? (defaultTheme is not null ? _themes[defaultTheme.Alias]
            : siteTemplate == "ApplicationSite" ? _themes[Application.Alias] : Resolve(null, rootType));

    public SiteTheme Resolve(string? alias, string rootType) =>
        Find(alias) ?? (rootType == "application" ? _themes[Application.Alias] : _themes[Website.Alias]);

    private SiteTheme? Find(string? alias) => alias is not null && _themes.TryGetValue(alias.Trim(), out var selected) ? selected : null;
}
