using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Application;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using UmbracoAppHost.Hosting;
using AppBlocks.Documents.PaperReports;

namespace UmbracoAppHost.Tests;

/// <summary>
/// A downstream host end to end: the samples switched off, its own saved export (a Web Site root, a template with its
/// view, a SQL data source and a paper report) imported beside the library's on first boot, and on the next start the
/// views the library ships rewritten while the host's own view keeps its change.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class DownstreamHostTests
{
    private const string AdminPassword = "Local-test-only-Downstream91!";
    internal static readonly Guid HomeKey = new("565fceff-64d4-4bc6-a1fb-4701f9f92081");
    private static readonly Guid MenuItemKey = new("fa3af211-b040-4630-91ad-a8753de1cc32");
    private static readonly Guid TemplateKey = new("8e70d073-2d01-4c58-94cc-edf844f9a1c7");
    internal static readonly Guid RowsKey =new("5b6310d9-bdb1-407d-8bac-22d6c40d8442");
    internal static readonly Guid PaperKey = new("73d4f0f6-8804-4670-898c-93c4258d5627");
    private static readonly Guid DataSourcesKey = new("dc3f9857-8628-c80c-fe4a-0e6d4a249318");

    internal const string ReportView = """
        @using AppBlocks.Documents.PaperReports
        @model PaperReportModel
        @{
            Layout = null;
        }
        <!doctype html>
        <html lang="@Model.Culture.Name">
        <head>
            <meta charset="utf-8" />
            <title>Downstream report</title>
        </head>
        <body class="paper-report">
            <h1>Downstream report</h1>
            @foreach (var row in Model.Rows)
            {
                <p class="one">@row["ONE"]</p>
            }
        </body>
        </html>

        """;

    [Fact]
    [ResetPublishedContentServices]
    public async Task A_host_without_samples_imports_its_own_export_and_keeps_the_shipped_views_current()
    {
        var root = Directory.CreateTempSubdirectory("downstream-").FullName;
        var export = Directory.CreateTempSubdirectory("downstream-export-").FullName;
        WriteHostExport(export);
        var instance = Path.Combine(root, "instance");
        var reportView = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Views")).FullName, "DownstreamReport.cshtml");
        await File.WriteAllTextAsync(reportView, ReportView);

        await using (var app = await StartAsync(root, export))
        {
            try
            {
                var runtime = app.Services.GetRequiredService<IRuntimeState>();
                var readiness = app.Services.GetRequiredService<IContentRoutingReadiness>();
                for (var i = 0; i < 600 && (runtime.Level != RuntimeLevel.Run || !readiness.IsReady); i++) await Task.Delay(100);
                // The startup handlers rename the installed administrator, and run after the first boot's import.
                var users = app.Services.GetRequiredService<IUserService>();
                for (var i = 0; i < 300 && (await users.GetAsync(Constants.Security.SuperUserKey))?.Username != "admin"; i++) await Task.Delay(100);
                using var scope = app.Services.CreateScope();
                var services = scope.ServiceProvider;
                var contents = services.GetRequiredService<IContentService>();
                for (var i = 0; i < 300 && contents.GetById(PaperKey)?.Published != true; i++) await Task.Delay(100);

                // The working folder holds the library's export without the samples, and the host's files beside it.
                Assert.False(File.Exists(Path.Combine(instance, "uSync", "Content", "sample-application.config")));
                Assert.True(File.Exists(Path.Combine(instance, "uSync", "Content", "data-sources.config")));
                Assert.True(File.Exists(Path.Combine(instance, "uSync", "Templates", "downstreamreport.config")));
                Assert.All(SampleContent.Keys, key => Assert.Null(contents.GetById(key)));
                Assert.True(contents.GetById(DataSourcesKey)?.Published);
                Assert.True(contents.GetById(HomeKey)?.Published);
                Assert.True(contents.GetById(RowsKey)?.Published);
                Assert.True(contents.GetById(PaperKey)?.Published);
                Assert.Null(services.GetRequiredService<FormResourceRegistry>().Find("sample.orders"));
                Assert.DoesNotContain(SampleReport.ReportName, services.GetRequiredService<PublishedReportCatalog>().Reports.Keys);

                // Every installed template is allowed on a paper report, and the default stays the default.
                var types = services.GetRequiredService<IContentTypeService>();
                async Task<string[]> AllowedAsync(Func<string[], bool> done)
                {
                    string[] allowed = [];
                    for (var i = 0; i < 100; i++)
                    {
                        allowed = types.Get(PaperReportDefinition.Alias)!.AllowedTemplates!.Select(template => template.Alias).ToArray();
                        if (done(allowed)) break;
                        await Task.Delay(100);
                    }
                    return allowed;
                }
                var allowed = await AllowedAsync(aliases => aliases.Contains("DownstreamReport"));
                Assert.Contains("DownstreamReport", allowed);
                Assert.Contains("AppBlocks", allowed);
                Assert.Equal("OrdersPaperReport", types.Get(PaperReportDefinition.Alias)!.DefaultTemplate?.Alias);

                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
                var signedIn = await client.PostAsJsonAsync("/umbraco/management/api/v1/security/back-office/login", new { username = "admin", password = AdminPassword });
                Assert.True(signedIn.IsSuccessStatusCode, $"Login returned {signedIn.StatusCode}: {await signedIn.Content.ReadAsStringAsync()}");
                var home = await client.GetAsync("/");
                var homeHtml = await home.Content.ReadAsStringAsync();
                Assert.True(home.IsSuccessStatusCode, homeHtml);
                Assert.Contains("Downstream home", homeHtml);
                var paper = await client.GetAsync($"/api/paper-reports/{PaperKey}?format=html");
                var print = await paper.Content.ReadAsStringAsync();
                Assert.True(paper.IsSuccessStatusCode, print);
                Assert.Equal("text/html", paper.Content.Headers.ContentType?.MediaType);
                Assert.Contains("<h1>Downstream report</h1>", print);
                Assert.Contains("<p class=\"one\">1</p>", print);

                Assert.Empty(Directory.EnumerateFiles(Path.Combine(instance, "data"), SampleReport.DatabaseFile, SearchOption.AllDirectories));
                using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(instance, ".apphost", "runtime.json")));
                Assert.False(state.RootElement.GetProperty("samples").GetBoolean());
                Assert.Equal(export, state.RootElement.GetProperty("hostExport").GetString());

                // A template registered later joins the paper report's templates, and leaves them when deleted.
                var templates = services.GetRequiredService<ITemplateService>();
                Assert.True((await templates.CreateAsync("Downstream extra", "DownstreamExtra", "@* A second report template. *@", Constants.Security.SuperUserKey)).Success);
                Assert.Contains("DownstreamExtra", await AllowedAsync(aliases => aliases.Contains("DownstreamExtra")));
                Assert.True((await templates.DeleteAsync("DownstreamExtra", Constants.Security.SuperUserKey)).Success);
                Assert.DoesNotContain("DownstreamExtra", await AllowedAsync(aliases => !aliases.Contains("DownstreamExtra")));
            }
            finally { await app.StopAsync(); }
        }

        const string change = "\n@* Changed in the host. *@\n";
        var shippedView = Path.Combine(root, "Views", "ReportPage.cshtml");
        await File.AppendAllTextAsync(shippedView, change);
        await File.AppendAllTextAsync(reportView, change);
        await using (var app = await StartAsync(root, export))
        {
            try
            {
                Assert.Equal(Text(Shipped("Views/ReportPage.cshtml")), Text(await File.ReadAllTextAsync(shippedView)));
                Assert.EndsWith(change, await File.ReadAllTextAsync(reportView));
            }
            finally { await app.StopAsync(); }
        }
    }

    [Fact]
    [ResetPublishedContentServices]
    public async Task A_configured_member_group_named_by_an_exported_data_source_survives_the_first_import()
    {
        // The host's data source admits writers from its one configured group only.
        const string editors = "Downstream editors";
        var full = Directory.CreateTempSubdirectory("downstream-full-").FullName;
        WriteHostExport(full);
        var export = Directory.CreateTempSubdirectory("downstream-export-").FullName;
        Directory.CreateDirectory(Path.Combine(export, "Content"));
        var rows = XElement.Load(Path.Combine(full, "Content", "downstream-rows.config"));
        rows.Element("Properties")!.Add(new XElement("writeGroups", new XElement("Value", new XCData(editors))));
        rows.Save(Path.Combine(export, "Content", "downstream-rows.config"));

        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            Settings =
            {
                [SampleContent.Setting] = "false",
                ["UmbracoAppHost:HostExport"] = export,
                ["Accounts:EnsureMemberGroups:0"] = editors,
            },
        });
        await site.WaitForPublishedAsync(RowsKey);

        // An import that ran before the group existed would drop it from the picker, and every reader could write.
        using var scope = site.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<PublishedDataCatalog>().ResolveSource("downstream.rows");
        Assert.NotNull(source);
        Assert.Equal([editors], source.WriteGroups);
    }

    private static async Task<WebApplication> StartAsync(string root, string export)
    {
        // As in AccountsTestHost: Umbraco caches the first site's backoffice path for the whole process.
        typeof(Umbraco.Cms.Core.Hosting.HostingEnvironmentExtensions).GetField("_backOfficePath", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root, ApplicationName = typeof(Program).Assembly.GetName().Name, EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InstanceRoot"] = Path.Combine(root, "instance"),
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = AdminPassword,
            ["Umbraco:CMS:Hosting:Debug"] = "false",
            ["uSync:Settings:ExportOnSave"] = "None",
            [SampleContent.Setting] = "false",
            ["UmbracoAppHost:HostExport"] = export,
        });
        builder.AddUmbracoAppHost();
        UmbracoTestSupport.UseSingleServer(builder.Services);
        var app = builder.Build();
        try
        {
            await app.UseUmbracoAppHostAsync();
            await app.StartAsync();
            await UmbracoTestSupport.WaitForInstallAsync(app.Services, TimeSpan.FromMinutes(3));
            return app;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    /// <summary>A downstream project's committed export: its own root, report template, row source and paper report.</summary>
    internal static void WriteHostExport(string export)
    {
        Directory.CreateDirectory(Path.Combine(export, "Content"));
        Directory.CreateDirectory(Path.Combine(export, "Templates"));

        // The Website theme demo's root, renamed; its menu links the demo's pages, so this one links itself only.
        var home = XElement.Load(Path.Combine(AppContext.BaseDirectory, "uSync", "Content", "theme-website.config"));
        home.SetAttributeValue("Key", HomeKey);
        home.SetAttributeValue("Alias", "Downstream home");
        var info = home.Element("Info")!;
        info.Element("Path")!.Value = "/DownstreamHome";
        // Before the Data sources root, so / serves it.
        info.Element("SortOrder")!.Value = "0";
        info.Element("NodeName")!.SetAttributeValue("Default", "Downstream home");
        info.Element("NodeName")!.Element("Name")!.Value = "Downstream home";
        var properties = home.Element("Properties")!;
        properties.Element("siteTitle")!.Element("Value")!.Value = "Downstream home";
        properties.Element("title")!.Element("Value")!.Value = "Downstream home";
        properties.Element("introduction")!.Element("Value")!.Value = "<p>A downstream host's own root page.</p>";
        properties.Element("topNavigationMenu")!.Element("Value")!.Value = JsonSerializer.Serialize(new[] {
            new { key = MenuItemKey, name = "Downstream home", itemType = "Document", contentKey = HomeKey, published = true, children = Array.Empty<object>() },
        });
        home.Save(Path.Combine(export, "Content", "downstream-home.config"));

        File.WriteAllText(Path.Combine(export, "Templates", "downstreamreport.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Template Key="{TemplateKey}" Alias="DownstreamReport" Level="1"><Name>Downstream report</Name><Parent /></Template>
            """);

        File.WriteAllText(Path.Combine(export, "Content", "downstream-rows.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Content Key="{RowsKey}" Alias="Downstream rows" Level="2">
              <Info>
                <Parent Key="{DataSourcesKey}">Data sources</Parent>
                <Path>/DataSources/DownstreamRows</Path>
                <Trashed>false</Trashed>
                <ContentType>sqlDataSource</ContentType>
                <CreateDate>2026-10-29T12:00:00</CreateDate>
                <NodeName Default="Downstream rows" />
                <SortOrder>10</SortOrder>
                <Published Default="true" />
                <Schedule />
                <Template />
              </Info>
              <Properties>
                <alias>
                  <Value><![CDATA[downstream.rows]]></Value>
                </alias>
                <connection>
                  <Value><![CDATA[UmbracoDb]]></Value>
                </connection>
                <keyColumns>
                  <Value><![CDATA[ONE]]></Value>
                </keyColumns>
                <source>
                  <Value><![CDATA[SELECT 1 AS ONE]]></Value>
                </source>
              </Properties>
            </Content>
            """);

        File.WriteAllText(Path.Combine(export, "Content", "downstream-paper-report.config"), $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Content Key="{{PaperKey}}" Alias="Downstream paper report" Level="2">
              <Info>
                <Parent Key="{{HomeKey}}">Downstream home</Parent>
                <Path>/DownstreamHome/downstream-paper-report</Path>
                <Trashed>false</Trashed>
                <ContentType>paperReport</ContentType>
                <CreateDate>2026-10-29T12:00:00</CreateDate>
                <NodeName Default="Downstream paper report" />
                <SortOrder>20</SortOrder>
                <Published Default="true" />
                <Schedule />
                <Template Key="{{TemplateKey}}">DownstreamReport</Template>
              </Info>
              <Properties>
                <dataSource>
                  <Value><![CDATA[{{RowsKey}}]]></Value>
                </dataSource>
                <fileName>
                  <Value><![CDATA[downstream-{date}.pdf]]></Value>
                </fileName>
                <culture>
                  <Value><![CDATA[en-CA]]></Value>
                </culture>
              </Properties>
            </Content>
            """);
    }

    private static string Shipped(string path)
    {
        using var reader = new StreamReader(new ManifestEmbeddedFileProvider(typeof(UmbracoAppHostExtensions).Assembly).GetFileInfo(path).CreateReadStream());
        return reader.ReadToEnd();
    }

    /// <summary>A view's text as <c>Get-TextHash</c> in <c>deployment/SiteTools.ps1</c> compares it.</summary>
    private static string Text(string view) => view.TrimStart('﻿').Replace("\r\n", "\n").TrimEnd('\n');
}
