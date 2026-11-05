using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;
using AppBlocks.Forms;
using InteractiveReport.Core.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Application;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The composed site, which has the managed factories that <see cref="ApplicationDatabaseTests"/>'s fixtures leave out:
/// a configured connection string reaches them first, and the application database's schema still has to come with it.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class ApplicationDatabaseSiteTests
{
    private static readonly Guid OrdersPaperReport = Guid.Parse("02e367e2-5764-7de6-4563-77cf40dff99d");

    [Fact]
    [ResetPublishedContentServices]
    public async Task A_configured_application_database_is_ensured_for_reports_and_forms()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("app-db-site-test-").FullName, "configured-sample.db");
        var options = new TestSiteOptions { Application = typeof(Program).Assembly };
        options.Settings[$"ConnectionStrings:{SampleReport.Connection}"] = $"Data Source={file};Pooling=False";
        options.Settings[$"ConnectionStrings:{SampleReport.Connection}_ProviderName"] = "Microsoft.Data.Sqlite";
        await using var site = await TestSite.StartAsync(options);
        using var scope = site.CreateScope();

        using (var reports = scope.ServiceProvider.GetRequiredService<IReportConnectionFactory>().CreateConnection(SampleReport.Connection))
        {
            Assert.IsType<SqliteConnection>(reports);
            reports.Open();
            using var command = reports.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ORDERS";
            Assert.Equal(500L, Convert.ToInt64(command.ExecuteScalar()));
        }

        using var forms = scope.ServiceProvider.GetRequiredService<AppBlocks.Connections.IApplicationConnectionFactory>().CreateConnection(SampleReport.Connection);
        forms.Open();
        using var read = forms.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM ORDERS";
        Assert.Equal(500L, Convert.ToInt64(read.ExecuteScalar()));
        Assert.Equal(48L, Convert.ToInt64(await Scalar(forms, "SELECT COUNT(*) FROM ORDERS_SCHEDULE_BARS")));

        await TheFormResourceWritesWhereTheReportReads(scope.ServiceProvider);
        // Not the instance's own sample file, which nothing asked for.
        Assert.False(File.Exists(site.ApplicationDatabaseFile(SampleReport.DatabaseFile)));
    }

    [LocalDbFact]
    [ResetPublishedContentServices]
    public async Task An_application_database_configured_on_sql_server_is_ensured()
    {
        const string master = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Connect Timeout=90;TrustServerCertificate=true";
        var database = "app_db_site_test_" + Guid.NewGuid().ToString("N")[..12];
        Execute(master, $"CREATE DATABASE [{database}]");
        var connectionString = master + $";Initial Catalog={database};Pooling=false";
        try
        {
            var options = new TestSiteOptions { Application = typeof(Program).Assembly };
            options.Settings[$"ConnectionStrings:{SampleReport.Connection}"] = connectionString;
            await using var site = await TestSite.StartAsync(options);
            using var scope = site.CreateScope();
            await using var connection = scope.ServiceProvider.GetRequiredService<IReportConnectionFactory>().CreateConnection(SampleReport.Connection);
            Assert.IsType<SqlConnection>(connection);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM dbo.ORDERS";
            Assert.Equal(500, Convert.ToInt32(await command.ExecuteScalarAsync()));
            Assert.Equal(48, Convert.ToInt32(await Scalar(connection, "SELECT COUNT(*) FROM dbo.ORDERS_SCHEDULE_BARS")));

            // First boot published the whole sample on this database, the sources that publishing reads included.
            var content = scope.ServiceProvider.GetRequiredService<IContentService>();
            var refused = USyncExport.Content(Path.Combine(AppContext.BaseDirectory, "uSync"))
                .Where(item => XElement.Load(item.File).Element("Info")?.Element("Published")?.Attribute("Default")?.Value == "true")
                .Where(item => content.GetById(item.Key)?.Published != true).Select(item => item.Path).ToArray();
            Assert.True(refused.Length == 0, "Published in the export but not here: " + string.Join(", ", refused));

            await TheFormResourceWritesWhereTheReportReads(scope.ServiceProvider);

            // And it works for a person: the report and the schedule read this database, and no page shows a failure.
            var member = site.CreateMember("sql-server-sample", "sql-server-sample@example.invalid", "SQL Server sample member");
            using var client = await site.SignInMemberAsync(member);
            Assert.True((await site.Reports(client).RowsAsync(SampleReport.ReportName)).GetArrayLength() > 0);
            Assert.True((await site.Reports(client).RowsAsync("orders-schedule")).GetArrayLength() > 0);
            foreach (var path in new[] { "/orders-schedule/", "/orders-form/?id=4", "/orders-interactive/" })
            {
                using var page = await site.GetPageAsync(client, path);
                var html = await page.Content.ReadAsStringAsync();
                Assert.True(page.StatusCode == HttpStatusCode.OK, $"{path}: {page.StatusCode} {html}");
                Assert.DoesNotContain("Could not render", html);
                Assert.DoesNotContain("could not be read", html);
            }
            using var schedule = await site.GetPageAsync(client, "/orders-schedule/");
            var scheduleHtml = await schedule.Content.ReadAsStringAsync();
            Assert.Contains("data-app-schedule", scheduleHtml);
            Assert.Contains("app-schedule-bar--open", scheduleHtml);
            using var paper = await site.DownloadPaperReportAsync(client, OrdersPaperReport, "?id=1");
            var print = await paper.Content.ReadAsStringAsync();
            Assert.True(paper.IsSuccessStatusCode, print);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(print, "<tr>").Count);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            Execute(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    /// <summary>Creates an order through the sample's form resource and finds it through the report's connection, then deletes it.</summary>
    private static async Task TheFormResourceWritesWhereTheReportReads(IServiceProvider services)
    {
        IFormResource resource = services.GetServices<IFormResource>().OfType<OrdersFormResource>().Single();
        var context = new FormExecutionContext(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "sample-member")], IdentityConstants.ApplicationScheme)),
            Guid.NewGuid(), Guid.NewGuid(), "1", Guid.NewGuid().ToString());
        var input = JsonSerializer.SerializeToElement(new { customer = "Form resource order", region = "NORTH", status = "NEW", amount = "125.34", orderDate = "2026-09-11", notes = "Written by the form" });

        var created = await resource.CreateAsync(input, context, default);
        Assert.Equal(200, created.Status);
        Assert.Equal("125.34", created.Record!.Values["amount"]);
        Assert.Equal("2026-09-11", created.Record.Values["orderDate"]);
        Assert.Equal(created.Record.Key, (await resource.CreateAsync(input, context, default)).Record!.Key);
        Assert.Equal("Form resource order", (await resource.LoadAsync(created.Record.Key!, context, default)).Record!.Values["customer"]);

        using var report = services.GetRequiredService<IReportConnectionFactory>().CreateConnection(SampleReport.Connection);
        report.Open();
        Assert.Equal("Written by the form", await Scalar(report, $"SELECT NOTES FROM ORDERS WHERE ORDER_ID = {created.Record.Key}"));

        var updated = await resource.UpdateAsync(created.Record.Key!, input, created.Record.Version!, context, default);
        Assert.Equal(200, updated.Status);
        Assert.NotEqual(created.Record.Version, updated.Record!.Version);
        Assert.Equal(409, (await resource.DeleteAsync(created.Record.Key!, created.Record.Version!, context, default)).Status);
        Assert.Equal(204, (await resource.DeleteAsync(created.Record.Key!, updated.Record.Version!, context, default)).Status);
        Assert.Equal(0L, Convert.ToInt64(await Scalar(report, $"SELECT COUNT(*) FROM ORDERS WHERE ORDER_ID = {created.Record.Key}")));
    }

    private static async Task<object?> Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static void Execute(string connection, string sql)
    {
        using var database = new SqlConnection(connection);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 90;
        command.ExecuteNonQuery();
    }
}
