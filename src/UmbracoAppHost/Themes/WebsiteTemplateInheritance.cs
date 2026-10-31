using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;

namespace UmbracoAppHost.Website;

/// <summary>Ordinary website pages use their nearest site's selected Umbraco template.</summary>
public sealed class WebsiteTemplateInheritance(ITemplateService templates, IDocumentNavigationQueryService navigation,
    IPublishedContentStatusFilteringService published) : INotificationAsyncHandler<RoutingRequestNotification>
{
    public async Task HandleAsync(RoutingRequestNotification notification, CancellationToken cancellationToken)
    {
        var request = notification.RequestBuilder;
        if (request.PublishedContent is not { } page || SiteContent.IsSite(page)
            || (page.ContentType.Alias != "webPage" && !page.ContentType.CompositionAliases.Contains("webPageContent"))) return;
        // Umbraco has resolved alternate templates; this also runs for Public Access replacement pages.
        // Preserve an explicit alternative selected by routing.
        if (request.Template is { } selected && selected.Id != page.TemplateId) return;
        // WebPage is the inheriting placeholder. A page may choose a native service/login template of its own.
        if (page.TemplateId is { } ownId && await templates.GetAsync(ownId) is { } own
            && !string.Equals(own.Alias, "WebPage", StringComparison.OrdinalIgnoreCase)) return;
        var site = page.Ancestors(navigation, published).OrderByDescending(node => node.Level).FirstOrDefault(SiteContent.IsWebsite);
        if (site?.TemplateId is { } templateId && await templates.GetAsync(templateId) is { } template)
            request.SetTemplate(template);
    }
}
