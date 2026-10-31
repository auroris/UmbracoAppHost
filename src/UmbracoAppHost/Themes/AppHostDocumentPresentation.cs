using AppBlocks.Documents;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;

namespace UmbracoAppHost.Themes;

/// <summary>Supplies the site's branding to the optional document engine.</summary>
public sealed class AppHostDocumentPresentation(SiteThemeCatalog themes, IDocumentNavigationQueryService navigation,
    IPublishedContentStatusFilteringService published, ITemplateService templates) : IDocumentPresentationProvider
{
    public async Task<DocumentPresentation> ResolveAsync(IPublishedContent page, CancellationToken cancellationToken)
    {
        var theme = await themes.ResolveForAsync(page, navigation, published, templates);
        return new(theme.Alias, theme.PrintStylesheet);
    }
}
