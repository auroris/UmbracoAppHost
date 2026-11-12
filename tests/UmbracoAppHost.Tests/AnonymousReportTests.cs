using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using AppBlocks.Blocks;
using AppBlocks.InteractiveReports.Blocks;
using UmbracoAppHost.Application;

namespace UmbracoAppHost.Tests;

/// <summary>
/// <c>Accounts:AllowAnonymousReportBlocks</c>: an anonymous visitor reads a report block on a page Public Access leaves open,
/// and nothing else. With the setting off, as by default, <see cref="BlocksHttpTests"/> and <see cref="WebPagesHttpTests"/>
/// show such blocks refused.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class AnonymousReportTests
{
    [Fact]
    [ResetPublishedContentServices]
    public async Task Anonymous_visitors_read_report_blocks_on_open_pages_when_the_site_allows_it()
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            Settings = { ["IdentityServer:Enabled"] = "false", ["Accounts:AllowAnonymousReportBlocks"] = "true" },
        }, TestContext.Current.CancellationToken);
        using var scope = site.CreateScope(); var services = scope.ServiceProvider;
        await site.WaitForPublishedAsync(ApplicationStarter.ReportPageKey);
        var types = services.GetRequiredService<IContentTypeService>();
        var contents = services.GetRequiredService<IContentService>();

        // The sample's report block, which reads its own table, on a Web Page that nothing protects.
#pragma warning disable CS0618
        var website = contents.Create("Report website", Constants.System.Root, "appHostWebsite");
        website.SetCultureName("Report website", "en");
        website.TemplateId = types.Get("appHostWebsite")!.DefaultTemplate?.Id;
        Publish(contents, website);
        var body = WebPageTestContent.Empty();
        var block = WebPageTestContent.Merge(body, JsonNode.Parse(ApplicationStarter.ReportContent())!)[0];
        WebPageTestContent.Layout(body).Add(WebPageTestContent.Wrap(body, AppBlockSchema.RegionAlias, [block]));
        var page = contents.Create("Anonymous report", website.Id, "webPage");
        page.SetCultureName("Anonymous report", "en");
        page.TemplateId = types.Get("webPage")!.DefaultTemplate?.Id;
        page.SetValue(ApplicationStarter.BlockPropertyAlias, body.ToJsonString(), "en");
        Publish(contents, page);
#pragma warning restore CS0618
        var reportName = ReportBlocks.Name(page.Key, ApplicationStarter.ReportKey);
        using var anonymous = site.CreateClient();
        var reports = site.Reports(anonymous);

        Assert.Contains("data-app-report", await Html(site, anonymous));
        Assert.Equal(25, (await reports.RowsAsync(reportName)).GetArrayLength());
        // A report that is not a block, here the published Orders report definition, still needs an account.
        Assert.False((await reports.SchemaAsync(SampleReport.ReportName)).IsSuccessStatusCode);
        // An application page's template shows anonymous visitors its sign-in layout, whatever its blocks allow.
        using (var application = await site.GetPageAsync(anonymous, "/orders-interactive/"))
            Assert.DoesNotContain("data-app-report", await application.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // Public Access protects the block, its page and its endpoints alike.
        var access = services.GetRequiredService<IPublicAccessService>();
        var form = contents.GetById(ApplicationStarter.FormPageKey)!;
        var entry = new PublicAccessEntry(page, form, form, [new PublicAccessRule { RuleType = Constants.Conventions.PublicAccess.MemberRoleRuleType, RuleValue = "No test members" }]);
        Assert.True(access.Save(entry).Success);
        Assert.False((await reports.SchemaAsync(reportName)).IsSuccessStatusCode);
        using (var protectedPage = await site.GetPageAsync(anonymous, "/anonymous-report/"))
            Assert.DoesNotContain("data-app-report", await protectedPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        access.Delete(entry);
        Assert.Equal(25, (await reports.RowsAsync(reportName)).GetArrayLength());

        // SQL that binds the signed-in person has nothing to show someone with no account, on an open page too.
        WebPageTestContent.SetValue(WebPageTestContent.Blocks(body, "appReport").Single(), "dataSource", JsonSerializer.Serialize(new {
            kind = "sql", connection = SampleReport.Connection, source = "SELECT ORDER_ID, CUSTOMER FROM ORDERS WHERE CREATED_BY = :USER_NAME" }));
        page = contents.GetById(page.Key)!;
        page.SetValue(ApplicationStarter.BlockPropertyAlias, body.ToJsonString(), "en");
        Publish(contents, page);
        Assert.False((await reports.SchemaAsync(reportName)).IsSuccessStatusCode);
        var html = await Html(site, anonymous);
        Assert.DoesNotContain("data-app-report", html);
        Assert.Contains("app-block-signin", html);
    }

    private static void Publish(IContentService contents, IContent content)
    {
        Assert.True(contents.Save(content).Success);
        var published = contents.Publish(content, ["*"]);
        Assert.True(published.Success, string.Join(" ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
    }

    private static async Task<string> Html(TestSite site, HttpClient client)
    {
        using var response = await site.GetPageAsync(client, "/anonymous-report/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {html}");
        return html;
    }
}
