using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using AppBlocks.Blocks;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The layout blocks end to end: the saved export's Layouts page is imported and published on first boot,
/// a copy beneath a website root is published through the same publish-time validation, and both are read
/// back over HTTP, so the markup each shell's stylesheet styles is checked in the application shell and in
/// the website shell. Umbraco enforces area allowances only in the backoffice, so the nesting rule is
/// checked where it lives: in the grid data type's configuration.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class BlockLayoutsHttpTests
{
    private static readonly Guid[] ColumnLayouts = AppBlockSchema.ColumnLayoutAliases.Select(AppBlockSchema.Key).ToArray();

    [Fact]
    [ResetPublishedContentServices]
    public async Task Layout_blocks_publish_and_render_their_areas_in_both_shells()
    {
        await using var host = await TestSite.StartAsync(new TestSiteOptions { Application = typeof(Program).Assembly }, TestContext.Current.CancellationToken);
        using var scope = host.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        for (var i = 0; i < 100 && types.Get(ApplicationStarter.BlockPageAlias)?.AllowedTemplates?.Any() != true; i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        var contents = services.GetRequiredService<IContentService>();

        // First boot imported the demo page, and publishing it passed the block validation.
        var layouts = contents.GetById(ApplicationStarter.Key("sample:layouts"));
        Assert.NotNull(layouts);
        Assert.True(layouts.Published);

        AssertLayoutsNeverNest(await services.GetRequiredService<IDataTypeService>().GetAsync(ApplicationStarter.Key("datatype:Application blocks")));

        // The same blocks beneath a website root render in the website shell.
#pragma warning disable CS0618
        var website = contents.Create("Layouts website", Constants.System.Root, "appHostWebsite");
        website.SetCultureName("Layouts website", "en");
        website.TemplateId = types.Get("appHostWebsite")!.DefaultTemplate?.Id;
        Assert.True(contents.Save(website).Success);
        Assert.True(contents.Publish(website, ["*"]).Success);
        var copy = contents.Create("Layouts in the website", website.Id, ApplicationStarter.BlockPageAlias);
        copy.TemplateId = types.Get(ApplicationStarter.BlockPageAlias)!.DefaultTemplate?.Id;
        copy.SetValue(ApplicationStarter.BlockPropertyAlias, LayoutsContent());
        Assert.True(contents.Save(copy).Success);
        var published = contents.Publish(copy, ["*"]);
        Assert.True(published.Success, string.Join(" ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
#pragma warning restore CS0618

        // The test login route signs the administrator in to the backoffice without a password.
        using var client = host.CreateClient();
        (await client.GetAsync($"/__tests/login/{Constants.Security.SuperUserKey}?scheme=backoffice&name=Layout%20test", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var application = await Page(client, "/layouts/");
        Assert.Contains("<body class=\"app\">", application);
        Assert.Contains("/dist/css/app.css", application);
        AssertLayouts(application);

        var site = await Page(client, "/layouts-in-the-website/");
        Assert.Contains("id=\"site-header\"", site);
        Assert.Contains("/dist/css/site.css", site);
        AssertLayouts(site);

        // The root's explicit choice overrides either root document type. Switching the shell
        // retains the same blocks, forms, and page access; no content migration is involved.
#pragma warning disable CS0618
        var applicationRoot = contents.GetById(layouts.ParentId)!;
        applicationRoot.SetValue("theme", "website");
        Assert.True(contents.Save(applicationRoot).Success);
        Assert.True(contents.Publish(applicationRoot, ["*"]).Success);
        website.SetValue("theme", "application");
        Assert.True(contents.Save(website).Success);
        Assert.True(contents.Publish(website, ["*"]).Success);
#pragma warning restore CS0618
        application = await Page(client, "/layouts/");
        Assert.Contains("id=\"site-header\"", application);
        Assert.Contains("/dist/css/site.css", application);
        AssertLayouts(application);
        site = await Page(client, "/layouts-in-the-website/");
        Assert.Contains("<body class=\"app\">", site);
        Assert.Contains("/dist/css/app.css", site);
        AssertLayouts(site);

        // The saved website gallery imports alongside the application gallery on first boot.
        var websiteDemo = contents.GetById(ApplicationStarter.Key("sample:theme-website"));
        Assert.NotNull(websiteDemo);
        Assert.True(websiteDemo.Published);
        var websiteLayouts = contents.GetById(ApplicationStarter.Key("sample:website-layouts"));
        Assert.NotNull(websiteLayouts);
        Assert.True(websiteLayouts.Published);

        using var anonymous = host.CreateClient();
        // Select the local form explicitly; a fresh visit now starts the configured identity server.
        var signIn = await Page(anonymous, "/layouts/?sso-local=1");
        Assert.Contains("id=\"site-header\"", signIn);
        Assert.DoesNotContain("data-app-form", signIn);
        Assert.Contains("app-signin", signIn);

        // Both paper-report nodes use the same typed Razor template. Local development gets a
        // self-contained printable HTML download, with the requested key bound to the row source.
        foreach (var (alias, title, language) in new[] {
            ("sample:orders-paper-report", "Orders", "en-CA"),
            ("sample:orders-paper-report-fr", "Commandes", "fr-CA"),
        })
        {
            var paper = contents.GetById(ApplicationStarter.Key(alias));
            Assert.NotNull(paper);
            Assert.True(paper.Published);
            using var downloaded = await host.DownloadPaperReportAsync(client, paper.Key, "?id=1");
            var print = await downloaded.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(downloaded.IsSuccessStatusCode, print);
            Assert.Equal("text/html", downloaded.Content.Headers.ContentType?.MediaType);
            Assert.Contains(".html", downloaded.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.Contains($"<html lang=\"{language}\">", print);
            Assert.Contains("<h1>" + title + "</h1>", print);
            Assert.Equal(2, Regex.Matches(print, "<tr>").Count);
            Assert.Contains("--paper-accent", print);
            Assert.DoesNotContain("@import", print);
            Assert.DoesNotContain("<link", print);
            // A paper report always renders for a person, so an anonymous visitor is told to sign in.
            using var refused = await host.DownloadPaperReportAsync(anonymous, paper.Key, "?id=1");
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Sign in", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        // Each shell's stylesheet carries Umbraco's grid arithmetic and styles the hooks.
        foreach (var stylesheet in new[] { "/dist/css/app.css", "/dist/css/site.css" })
        {
            var css = await client.GetStringAsync(stylesheet, TestContext.Current.CancellationToken);
            Assert.Contains(".umb-block-grid__area-container", css);
            Assert.Contains(".ah-layout>.ah-area", css);
            Assert.Contains(".ah-region__title", css);
            Assert.Contains("details.ah-region", css);
            Assert.Contains("data-label-position=left", css);
            Assert.Contains("@container ah-field", css);
        }
    }

    [Theory]
    [InlineData("4/8", 4, 8)]
    [InlineData("6/6", 6, 6)]
    [InlineData(" 8 / 4 ", 8, 4)]
    public void A_split_that_fills_the_grid_sets_both_spans(string value, int left, int right) =>
        Assert.Equal<(int, int)?>((left, right), AppBlockLayout.Split(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4/4")]
    [InlineData("12/0")]
    [InlineData("4/8/0")]
    [InlineData("left/right")]
    public void Any_other_split_keeps_the_configured_spans(string? value) => Assert.Null(AppBlockLayout.Split(value));

    private static async Task<string> Page(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, html);
        Assert.DoesNotContain("Could not render", html);
        Assert.DoesNotContain("invalid resource", html);
        return html;
    }

    /// <summary>The demo page's layouts in order, each with its areas' spans, its regions and its bound fields.</summary>
    private static void AssertLayouts(string html)
    {
        var starts = Regex.Matches(html, "<div class=\"(ah-layout [^\"]*)\"").ToArray();
        // Layouts never nest, so everything between one layout's start and the next belongs to it.
        var layouts = starts.Select((match, i) => (
            Classes: match.Groups[1].Value.Split(' '),
            Body: html[match.Index..(i + 1 < starts.Length ? starts[i + 1].Index : html.Length)])).ToArray();
        Assert.Equal(5, layouts.Length);
        // The split sets each area's width; the grid inside keeps the configured span, as the backoffice shows it.
        (string Modifier, int[] Spans, int[] Grids)[] expected = [
            ("ah-layout--full-width", [12], [12]),
            ("ah-layout--split-4-8", [4, 8], [6, 6]),
            ("ah-layout--split-6-6", [6, 6], [6, 6]),
            ("ah-layout--split-8-4", [8, 4], [6, 6]),
            ("ah-layout--three-columns", [4, 4, 4], [4, 4, 4]),
        ];
        foreach (var ((classes, body), (modifier, spans, grids)) in layouts.Zip(expected))
        {
            Assert.Contains(modifier, classes);
            Assert.Contains("umb-block-grid__area-container", classes);
            var areas = Regex.Matches(body, "class=\"ah-area [^\"]*\" data-area-alias=\"[a-z]+\" data-area-col-span=\"(\\d+)\"").ToArray();
            Assert.Equal(spans, areas.Select(m => int.Parse(m.Groups[1].Value)).ToArray());
            Assert.Equal(grids, areas.Select(m => int.Parse(Regex.Match(body[m.Index..], "data-grid-columns=\"(\\d+)\"").Groups[1].Value)).ToArray());
        }
        Assert.All(layouts.Take(4).Skip(1), layout => Assert.Contains("ah-layout--two-columns", layout.Classes));

        // Regions: titled, inside a form whose heading is an h2, so the titles are h3.
        Assert.Matches("<section class=\"ah-region\" id=\"region-[0-9a-f]{32}\" aria-labelledby=\"region-[0-9a-f]{32}-title\">\\s*<div class=\"ah-region__header\"><h3 class=\"ah-region__title\" id=\"region-[0-9a-f]{32}-title\">Region</h3></div>", html);
        Assert.Matches("<details class=\"ah-region ah-region--collapsible\" id=\"region-[0-9a-f]{32}\" open>\\s*<summary class=\"ah-region__header\"><h3 class=\"ah-region__title\" id=\"region-[0-9a-f]{32}-title\">Collapsible region</h3></summary>", html);
        Assert.Equal(2, Regex.Matches(html, "class=\"ah-region__body\"").Count);

        // The content inside the layouts still binds to the form, with the resource's labels.
        foreach (var (field, label) in new[] { ("customer", "Customer"), ("region", "Region"), ("status", "Status"), ("amount", "Amount"), ("orderDate", "Order date"), ("notes", "Notes") })
        {
            Assert.Contains($"data-field=\"{field}\"", html);
            Assert.Matches($"<label id=\"field-([0-9a-f]{{32}})-label\" for=\"field-\\1\">{label}\\s*<", html);
        }
        Assert.Matches("<div class=\"ah-grid umb-block-grid__layout-container\" data-grid-columns=\"12\"", html);
    }

    /// <summary>
    /// Every area of a column layout, of a region and of a fieldset accepts content blocks, fieldsets and
    /// regions but no column layout; the form's own areas take the layouts.
    /// </summary>
    private static void AssertLayoutsNeverNest(Umbraco.Cms.Core.Models.IDataType? dataType)
    {
        Assert.NotNull(dataType);
        var configuration = JsonNode.Parse(JsonSerializer.Serialize(dataType.ConfigurationData))!;
        var blocks = configuration["blocks"]!.AsArray().Select(b => b!).ToArray();
        Guid Type(JsonNode block) => Guid.Parse(block["contentElementTypeKey"]!.GetValue<string>());
        JsonNode Block(string alias) => blocks.Single(b => Type(b) == AppBlockSchema.Key(alias));
        HashSet<Guid> Allowed(JsonNode area) => area["specifiedAllowance"]!.AsArray().SelectMany(rule =>
            rule!["groupKey"]?.GetValue<string>() is { } group
                ? blocks.Where(b => b["groupKey"]?.GetValue<string>() == group).Select(Type)
                : [Guid.Parse(rule["elementTypeKey"]!.GetValue<string>())]).ToHashSet();

        foreach (var alias in AppBlockSchema.LayoutAliases)
        {
            var block = Block(alias);
            Assert.True(block["allowAtRoot"]!.GetValue<bool>());
            Assert.True(block["allowInAreas"]!.GetValue<bool>());
        }
        var areas = new[] { "appFullWidth", "appColumns", "appThreeColumns", AppBlockSchema.RegionAlias, "appFieldset" }
            .SelectMany(alias => Block(alias)["areas"]!.AsArray().Select(area => (alias, area: area!))).ToArray();
        Assert.Equal(8, areas.Length);
        foreach (var (alias, area) in areas)
        {
            var allowed = Allowed(area);
            Assert.DoesNotContain(allowed, ColumnLayouts.Contains);
            Assert.Contains(AppBlockSchema.Key(AppBlockSchema.RegionAlias), allowed);
            Assert.Contains(AppBlockSchema.Key("appFieldset"), allowed);
            Assert.All(AppBlockSchema.FieldAliases, field => Assert.Contains(AppBlockSchema.Key(field), allowed));
        }
        // The form's fields area keeps no allowance list, so it takes every block allowed in areas: the layouts too.
        Assert.Null(Block("appForm")["areas"]!.AsArray().Single(a => a!["alias"]!.GetValue<string>() == "fields")!["specifiedAllowance"]);
    }

    private static string LayoutsContent()
        => USyncExport.ContentByKey(ApplicationStarter.Export, ApplicationStarter.Key("sample:layouts")).Property(ApplicationStarter.BlockPropertyAlias)!;
}
