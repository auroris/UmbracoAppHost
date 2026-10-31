using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InteractiveReport.Core.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using AppBlocks.Connections;
using AppBlocks.Databases;
using UmbracoAppHost.Hosting;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The getting-started fixture against a downstream project in a temporary folder: its settings, its committed export,
/// its view, its schema scripts and its <see cref="IUmbracoAppHost"/> registrations, with no more options than a
/// fixture gives when its project is found for it.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class DownstreamProjectTests
{
    private const string ProjectPassword = "Project-only-password-91!";

    [Fact]
    [ResetPublishedContentServices]
    public async Task A_test_site_boots_a_downstream_project_as_its_program_does()
    {
        var project = Directory.CreateTempSubdirectory("downstream-project-").FullName;
        WriteProject(project);

        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            // The reference launcher's assembly carries the views' compilation context; the project and the host are this test's.
            Application = typeof(Program).Assembly,
            ProjectFolder = project,
            Host = typeof(DownstreamProgram),
        });

        Assert.Equal(project, site.ProjectFolder);
        // The project's InstanceRoot gives way to the site's temporary one.
        Assert.Equal(Path.Combine(site.Root, "instance"), site.InstanceRoot);

        // appsettings.json and appsettings.Development.json, and the host's endpoint that reads them.
        using var browser = site.CreateClient();
        Assert.Equal("from appsettings.json|Development", await (await site.GetPageAsync(browser, "/downstream-settings")).Content.ReadAsStringAsync());

        // UmbracoAppHost:Samples is false in the project, and its committed export is imported on first boot.
        await site.WaitForPublishedAsync(DownstreamHostTests.PaperKey);
        await site.WaitForPublishedAsync(DownstreamHostTests.HomeKey);
        using (var scope = site.CreateScope())
            Assert.All(SampleContent.Keys, key => Assert.Null(scope.ServiceProvider.GetRequiredService<IContentService>().GetById(key)));
        using (var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(site.InstanceRoot, ".apphost", "runtime.json"))))
        {
            Assert.Equal(project, state.RootElement.GetProperty("projectRoot").GetString());
            Assert.Equal(Path.Combine(project, "uSync"), state.RootElement.GetProperty("hostExport").GetString());
        }

        // The host's services, with the project's path, and its database with the project's schema.
        Assert.Equal(project, site.Services.GetRequiredService<DownstreamProjectRoot>().Path);
        await using (var connection = site.Services.GetRequiredService<IReportConnectionFactory>().CreateConnection("DownstreamDb"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'DOWNSTREAM_ROWS'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }

        // Settings -> AppBlocks -> Connections and the report source picker offer the host's database, read-only, and read its tables.
        using (var settings = await site.SignInAsAdministratorAsync())
        {
            const string api = "/umbraco/management/api/v1/appblocks";
            var listed = await settings.GetStringAsync(api + "/connections");
            using (var connections = JsonDocument.Parse(listed))
            {
                var downstream = connections.RootElement.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "DownstreamDb");
                Assert.True(downstream.GetProperty("readOnly").GetBoolean());
                Assert.Equal("sqlite", downstream.GetProperty("provider").GetString());
            }
            Assert.DoesNotContain("sqlite.db", listed, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ReportsSampleDb", listed); // UmbracoAppHost:Samples is false.
            Assert.Contains("DOWNSTREAM_ROWS", await settings.GetStringAsync(api + "/tables?connection=DownstreamDb"));
            var selection = JsonSerializer.Serialize(new { kind = "table", connection = "DownstreamDb", source = "DOWNSTREAM_ROWS" });
            using var preview = await settings.PostAsync(api + "/report-preview", JsonContent.Create(new { dataSource = selection }));
            Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
            var unknown = JsonSerializer.Serialize(new { kind = "table", connection = "NotRegistered", source = "DOWNSTREAM_ROWS" });
            using var refused = await settings.PostAsync(api + "/report-preview", JsonContent.Create(new { dataSource = unknown }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        }

        // The project's view renders its imported paper report, and the administrator keeps the site's password.
        Assert.True(File.Exists(Path.Combine(site.Root, "Views", "DownstreamReport.cshtml")));
        using var admin = await site.SignInBackOfficeAsync(site.AdminUserName, site.AdminPassword);
        var home = await (await site.GetPageAsync(admin, "/")).Content.ReadAsStringAsync();
        Assert.Contains("Downstream home", home);
        var paper = await site.DownloadPaperReportAsync(admin, DownstreamHostTests.PaperKey, "?format=html");
        var print = await paper.Content.ReadAsStringAsync();
        Assert.True(paper.IsSuccessStatusCode, print);
        Assert.Contains("<h1>Downstream report</h1>", print);
    }

    [Fact]
    public void The_project_is_found_beside_its_tests_and_the_host_in_its_assembly()
    {
        var repository = Directory.CreateTempSubdirectory("downstream-repository-").FullName;
        string Make(params string[] parts)
        {
            var file = Path.Combine([repository, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "");
            return Path.GetDirectoryName(file)!;
        }
        Make("Shop.slnx");
        var web = Make("src", "Shop.Web", "Shop.Web.csproj");
        Make("tests", "Shop.Tests", "Shop.Tests.csproj");
        // Copies in build output, packages and other checkouts are not the project.
        Make("src", "Shop.Web", "bin", "Release", "Shop.Web.csproj");
        Make("node_modules", "shop", "Shop.Web.csproj");
        Make(".claude", "worktrees", "other", "src", "Shop.Web", "Shop.Web.csproj");
        var testOutput = Directory.CreateDirectory(Path.Combine(repository, "tests", "Shop.Tests", "bin", "Debug", "net10.0")).FullName;
        var hostOutput = Directory.CreateDirectory(Path.Combine(web, "bin", "Debug", "net10.0")).FullName;

        Assert.Equal(web, TestSite.FindProjectFolder("Shop.Web", testOutput));
        Assert.Equal(web, TestSite.FindProjectFolder("Shop.Web", hostOutput));
        Assert.Null(TestSite.FindProjectFolder("Shop.Other", testOutput));
        Make("samples", "Shop.Web", "Shop.Web.csproj");
        Assert.Throws<TestSiteException>(() => TestSite.FindProjectFolder("Shop.Web", testOutput));

        // This repository's reference launcher, from this test's output folder.
        Assert.Equal(Path.Combine(Repository(), "src", "UmbracoAppHost.Web"), TestSite.FindProjectFolder(typeof(Program).Assembly));
        Assert.Null(TestSite.FindHost(typeof(Program).Assembly));
        Assert.Equal(typeof(DownstreamProgram), TestSite.FindHost(typeof(DownstreamProgram).Assembly));
    }

    /// <summary>A downstream project's files, as its repository commits them.</summary>
    private static void WriteProject(string project)
    {
        File.WriteAllText(Path.Combine(project, "appsettings.json"), JsonSerializer.Serialize(new
        {
            InstanceRoot = "instance-not-for-tests",
            UmbracoAppHost = new { Samples = false, AdminPassword = ProjectPassword },
            Downstream = new { Greeting = "from appsettings.json", Environment = "Production" },
        }));
        File.WriteAllText(Path.Combine(project, "appsettings.Development.json"), """{ "Downstream": { "Environment": "Development" } }""");
        DownstreamHostTests.WriteHostExport(Path.Combine(project, "uSync"));
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(project, "Views")).FullName, "DownstreamReport.cshtml"), DownstreamHostTests.ReportView);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(project, "Schema", "sqlite")).FullName, "001-rows.sql"),
            "CREATE TABLE IF NOT EXISTS DOWNSTREAM_ROWS (ID INTEGER PRIMARY KEY);");
    }

    private static string Repository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "UmbracoAppHost.slnx"))) return folder.FullName;
        throw new InvalidOperationException($"UmbracoAppHost.slnx was not found above {AppContext.BaseDirectory}.");
    }
}

/// <summary>A downstream host's <c>Program</c> class, as the getting-started guide writes one.</summary>
public sealed class DownstreamProgram : IUmbracoAppHost
{
    public static void ConfigureServices(WebApplicationBuilder builder)
        => builder.Services.AddSingleton(new DownstreamProjectRoot(builder.ProjectRoot()));

    public static void ConfigureConnections(WebApplicationBuilder builder, AppBlocksBuilder connections)
        => connections.AddApplicationDatabase("DownstreamDb", ApplicationSchema.FromDirectory(Path.Combine(builder.ProjectRoot(), "Schema")));

    public static void ConfigureEndpoints(IUmbracoEndpointBuilderContext umbraco)
        => umbraco.EndpointRouteBuilder.MapGet("/downstream-settings", (IConfiguration configuration)
            => $"{configuration["Downstream:Greeting"]}|{configuration["Downstream:Environment"]}");
}

public sealed record DownstreamProjectRoot(string Path);
