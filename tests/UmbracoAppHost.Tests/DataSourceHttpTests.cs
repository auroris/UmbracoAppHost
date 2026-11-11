using System.Net;
using System.Text.Json;
using InteractiveReport.Core.Definitions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Application;
using AppBlocks.Blocks;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace UmbracoAppHost.Tests;

/// <summary>
/// Data sources in a real host: the saved export's Orders data source behind the Orders form, and a data source and
/// page published through Umbraco's own pipeline, driven through /api/forms as a signed-in member.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class DataSourceHttpTests
{
    [Fact]
    [ResetPublishedContentServices]
    public async Task Members_use_published_data_sources_through_the_form_endpoints()
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions { Application = typeof(Program).Assembly });
        using var scope = site.CreateScope(); var services = scope.ServiceProvider;
        var contents = services.GetRequiredService<IContentService>();
        await site.WaitForPublishedAsync(ApplicationStarter.FormPageKey);
        // The first-boot import publishes the data source before the page bound to it, and both pass validation.
        Assert.True(contents.GetById(ApplicationStarter.DataSourceRootKey)!.Published);
        Assert.True(contents.GetById(ApplicationStarter.OrdersDataSourceKey)!.Published);
        Assert.True(contents.GetById(ApplicationStarter.FormPageKey)!.Published);

        var members = services.GetRequiredService<IMemberService>();
        var member = site.CreateMember("pilot", "pilot@example.invalid", "Test pilot", "Pilots");
        var pilots = (await services.GetRequiredService<IMemberGroupService>().GetByNameAsync("Pilots"))!;
        site.LinkWindowsIdentity(member.Key, "S-1-5-21-77");

        using (var anonymous = site.CreateClient())
            Assert.Equal(HttpStatusCode.Forbidden, (await site.Forms(anonymous, ApplicationStarter.FormPageKey, ApplicationStarter.FormKey).LoadAsync()).Response.StatusCode);
        using var client = await site.SignInMemberAsync(member);
        var orders = site.Forms(client, ApplicationStarter.FormPageKey, ApplicationStarter.FormKey);
        var reports = site.Reports(client);

        PublishedReport ordersReport; ListOfValuesDefinition regionList;
        using (var definitionsScope = site.CreateScope())
        {
            var catalog = definitionsScope.ServiceProvider.GetRequiredService<PublishedReportCatalog>();
            var lists = definitionsScope.ServiceProvider.GetRequiredService<PublishedDataCatalog>();
            await catalog.LoadAsync();
            await lists.LoadAsync();
            Assert.Contains("orders", catalog.ReportNodes.Keys);
            Assert.Contains("umbraco-recent-pages", catalog.ReportNodes.Keys);
            Assert.Equal(2, lists.Lists.Count);
            Assert.NotNull(catalog.ReportNodes["orders"].DataSource);
            (ordersReport, regionList) = (catalog.ReportNodes["orders"], lists.Lists["order-region"]);
        }
        // The next request reuses what was read and compiled for these published versions instead of reading it again.
        using (var nextScope = site.CreateScope())
        {
            var catalog = nextScope.ServiceProvider.GetRequiredService<PublishedReportCatalog>();
            var lists = nextScope.ServiceProvider.GetRequiredService<PublishedDataCatalog>();
            await catalog.LoadAsync();
            await lists.LoadAsync();
            Assert.Same(ordersReport, catalog.ReportNodes["orders"]);
            Assert.Same(regionList, lists.Lists["order-region"]);
        }
        var reportResponse = await reports.SchemaAsync("orders");
        Assert.True(reportResponse.IsSuccessStatusCode, await reportResponse.Content.ReadAsStringAsync());

        // The sample Orders form runs on its registered C# class: defaults, create, update, stale save, delete.
        var start = Ok(await orders.LoadAsync());
        var initial = start.Values;
        Assert.Equal("NORTH", initial.GetProperty("region").GetString());
        Assert.Equal("NEW", initial.GetProperty("status").GetString());
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"), initial.GetProperty("orderDate").GetString());
        Assert.EndsWith(services.GetRequiredService<FormResourceRegistry>().RegisteredResources["sample.orders"].ContractVersion, start.DefinitionVersion);
        var created = Ok(await orders.CreateAsync(new {
            customer = "Member order", region = "WEST", status = "NEW", amount = "42.50", orderDate = "2026-10-23", notes = "Created through the data source" }));
        // A browser can forge an option; membership in the current list is checked again on the server.
        var badChoice = await orders.CreateAsync(new { customer = "Forged choice", region = "NOT-A-REGION", status = "NEW", amount = "1", orderDate = "2026-10-23" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badChoice.Response.StatusCode);
        var key = created.RecordKey!;
        var version = created.RecordChecksum!;
        var updated = Ok(await orders.UpdateAsync(key, version, new { notes = "Updated by the member" }));
        Assert.Equal("2", updated.RecordChecksum);
        Assert.Equal(HttpStatusCode.Conflict, (await orders.UpdateAsync(key, version, new { notes = "Updated by the member" })).Response.StatusCode);
        var injection = await orders.UpdateAsync(key, "2", new { createdBy = "someone" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, injection.Response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await orders.DeleteAsync(key, "2")).StatusCode);
        Assert.Equal(0L, Sample(site, $"SELECT COUNT(*) FROM ORDERS WHERE ORDER_ID = {key}"));
        // A new site keeps the sample's SQLite file beside Umbraco's own SQLite database.
        var umbracoDatabase = new SqliteConnectionStringBuilder(site.App.Configuration.GetConnectionString("umbracoDbDSN")).DataSource;
        var sampleDatabase = site.ApplicationDatabaseFile(SampleReport.DatabaseFile);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(umbracoDatabase)!, SampleReport.DatabaseFile), sampleDatabase);
        Assert.True(File.Exists(sampleDatabase));

        // A data source and a page published through Umbraco: publishing refuses mistakes and binds the page to the columns.
        Sample(site, """
            CREATE TABLE IF NOT EXISTS PILOT_NOTES (NOTE_ID INTEGER PRIMARY KEY, OWNER_SID TEXT NOT NULL, BODY TEXT NOT NULL, TOPIC TEXT NULL,
                MOOD TEXT NULL, ALTITUDE INTEGER NULL, FLOWN_ON TEXT NULL, REMARKS TEXT NULL, VERSION INTEGER NOT NULL DEFAULT 1);
            INSERT INTO PILOT_NOTES (OWNER_SID, BODY) VALUES ('S-1-5-21-99', 'Somebody else''s note');
            """);
        var source = contents.Create("Pilot notes", ApplicationStarter.DataSourceRootKey, DataSourceSchema.SqlAlias);
        foreach (var (alias, value) in new (string, object)[] {
            ("access", "Not public"),
            ("alias", "pilot-notes"), ("connection", SampleReport.Connection), ("source", "PILOT_NOTES"), ("keyColumns", "NOTE_ID"),
            ("concurrencyMode", "Checksum"), ("allowInsert", 1), ("allowUpdate", 1), ("rowRestriction", "OWNER_SID = :USER_SID"),
            ("insertDefaults", "OWNER_SID = :USER_SID\nTOPIC = :PARAM.topic"), ("writeGroups", pilots.Id.ToString()),
            ("mapping", """{ "topic": { "choices": ["Weather", "Fuel"] }, "mood": { "choices": ["Calm", "Busy"] }, "flownOn": { "kind": "date" }, "missing": "NO_SUCH_COLUMN" }"""),
        }) source.SetValue(alias, value);
        contents.Save(source);
        var refused = contents.Publish(source, ["*"]);
        Assert.False(refused.Success);
        Assert.Contains(refused.EventMessages!.GetAll(), m => m.Message.Contains("NO_SUCH_COLUMN"));
        source = contents.GetById(source.Key)!; // A refused publish leaves the instance in its publishing state.
        source.SetValue("mapping", """{ "topic": { "choices": ["Weather", "Fuel"] }, "mood": { "choices": ["Calm", "Busy"] }, "flownOn": { "kind": "date" } }""");
        contents.Save(source);
        var published = contents.Publish(source, ["*"]);
        Assert.True(published.Success, string.Join(" ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));

        // Published SQL data sources now implement the form contract too, including defaults and SID restrictions.
        var pilotBlocks = new AppBlockValues();
        var pilotForm = pilotBlocks.Form("pilot-notes", [pilotBlocks.Field("appTextArea", "body"), pilotBlocks.Field("appSelect", "topic")]);
        pilotBlocks.Place(pilotForm);
        var page = contents.Create("Pilot notes form", contents.GetById(ApplicationStarter.FormPageKey)!.ParentId, ApplicationStarter.BlockPageAlias);
        page.SetValue(ApplicationStarter.BlockPropertyAlias, pilotBlocks.ToJson());
        contents.Save(page);
        var bound = contents.Publish(page, ["*"]);
        Assert.True(bound.Success, string.Join(" ", bound.EventMessages?.GetAll().Select(m => m.Message) ?? []));
        var notes = site.Forms(client, page.Key, pilotForm.Key);
        Ok(await notes.LoadAsync());
        var note = Ok(await notes.CreateAsync(new { body = "Draft flight", topic = "Weather" }));
        Assert.Equal("S-1-5-21-77", Sample(site, $"SELECT OWNER_SID FROM PILOT_NOTES WHERE NOTE_ID = {note.RecordKey}"));
        Ok(await notes.UpdateAsync(note.RecordKey!, note.RecordChecksum, new { body = "Smooth flight" }));
        Assert.Equal(HttpStatusCode.NotFound, (await notes.LoadAsync("1")).Response.StatusCode); // Another person's seeded note.

        // A report source's page filter and member restriction apply to every report request.
        using (var publishScope = site.CreateScope())
        {
            var publisher = publishScope.ServiceProvider.GetRequiredService<IContentService>();
            var reportSource = publisher.Create("Pilot notes report source", ApplicationStarter.DataSourceRootKey, DataSourceSchema.SqlAlias);
            reportSource.SetValue("alias", "pilot-notes-report-source");
            reportSource.SetValue("access", "Not public");
            reportSource.SetValue("connection", SampleReport.Connection);
            reportSource.SetValue("source", "SELECT * FROM PILOT_NOTES WHERE :PARAM.pageSize IS NULL OR TOPIC = :PARAM.pageSize");
            reportSource.SetValue("rowRestriction", "OWNER_SID = :USER_SID");
            publisher.Save(reportSource);
            var sourcePublished = publisher.Publish(reportSource, ["*"]);
            Assert.True(sourcePublished.Success, string.Join(" ", sourcePublished.EventMessages?.GetAll().Select(m => m.Message) ?? []));
            var report = publisher.Create("Pilot notes report", ApplicationStarter.DataSourceRootKey, PublishedReportCatalog.ReportAlias);
            report.SetValue("alias", "pilot-note-report"); report.SetValue("dataSource", reportSource.Key.ToString());
            publisher.Save(report);
            var reportPublished = publisher.Publish(report, ["*"]);
            Assert.True(reportPublished.Success, string.Join(" ", reportPublished.EventMessages?.GetAll().Select(m => m.Message) ?? []));
        }
        var reportRows = await reports.RowsAsync("pilot-note-report");
        Assert.Equal("Smooth flight", Assert.Single(reportRows.EnumerateArray()).GetProperty("BODY").GetString());
        // A page parameter can share a name with an adapter option without being overwritten by it.
        var matching = (await Read(await client.GetAsync("/api/reports/pilot-note-report/default?pageSize=10&appPageQuery=" + Uri.EscapeDataString("?pageSize=Weather")))).GetProperty("result");
        Assert.Equal("Smooth flight", Assert.Single(matching.GetProperty("rows").EnumerateArray()).GetProperty("BODY").GetString());
        var otherRows = await reports.RowsAsync("pilot-note-report", "pageSize=10&appPageQuery=" + Uri.EscapeDataString("?pageSize=Fuel"));
        Assert.Empty(otherRows.EnumerateArray());
        // A download reads the page's parameters from the same envelope, so the file holds the rows the page shows.
        foreach (var (topic, included) in new[] { ("Weather", true), ("Fuel", false) })
        {
            using var download = await reports.DownloadAsync("pilot-note-report", "csv", matching.GetProperty("document"), "appPageQuery=" + Uri.EscapeDataString("?pageSize=" + topic));
            var csv = await download.Content.ReadAsStringAsync();
            Assert.True(download.IsSuccessStatusCode, $"{(int)download.StatusCode}: {csv}");
            Assert.Equal(included, csv.Contains("Smooth flight", StringComparison.Ordinal));
            Assert.DoesNotContain("Somebody else", csv, StringComparison.Ordinal);
        }

        // A changed list invalidates loaded forms, and new forms validate against the current choices.
        using (var publishScope = site.CreateScope())
        {
            var publisher = publishScope.ServiceProvider.GetRequiredService<IContentService>();
            var list = publisher.GetById(ApplicationStarter.Key("definitions:order-region-values"))!;
            list.SetValue("sql", "SELECT 'NORTH' AS VALUE, 'North' AS LABEL"); publisher.Save(list);
            Assert.True(publisher.Publish(list, ["*"]).Success);
        }
        // The Orders form is still the one loaded at the start, so its writes carry the old definition version.
        var staleChoices = await orders.CreateAsync(new { customer = "Old options", region = "WEST", status = "NEW", amount = "1", orderDate = "2026-10-23" });
        Assert.Equal(HttpStatusCode.Conflict, staleChoices.Response.StatusCode);
        Ok(await orders.LoadAsync());
        var removedChoice = await orders.CreateAsync(new { customer = "Removed option", region = "WEST", status = "NEW", amount = "1", orderDate = "2026-10-23" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, removedChoice.Response.StatusCode);

        // Existing retired values stay usable on their own row, but cannot be forged as a new selection.
        var legacyKey = Sample(site, """
            INSERT INTO ORDERS (CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, VERSION)
            VALUES ('Legacy choice', 'WEST', 'NEW', 1, '2026-10-23', 1);
            SELECT last_insert_rowid();
            """)!.ToString()!;
        var legacy = Ok(await orders.LoadAsync(legacyKey));
        var preserved = Ok(await orders.UpdateAsync(legacyKey, legacy.RecordChecksum!, new { region = "WEST", notes = "Changed notes only" }));
        Assert.Equal("WEST", preserved.Values.GetProperty("region").GetString());
        var invalidLegacy = await orders.UpdateAsync(legacyKey, preserved.RecordChecksum!, new { region = "SOUTH" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidLegacy.Response.StatusCode);
        var changedLegacy = Ok(await orders.UpdateAsync(legacyKey, preserved.RecordChecksum!, new { region = "NORTH" }));
        Assert.Equal("NORTH", changedLegacy.Values.GetProperty("region").GetString());

        // An override whose referenced source is unpublished must not fall back to the configured seed.
#pragma warning disable CS0618
        Assert.True(contents.Unpublish(contents.GetById(ApplicationStarter.OrdersDataSourceKey)!).Success);
#pragma warning restore CS0618
        Assert.Equal(HttpStatusCode.NotFound, (await reports.SchemaAsync("orders")).StatusCode);
    }

    private static FormResponse Ok(FormResponse response)
    {
        Assert.True(response.IsSuccess, $"{(int)response.Response.StatusCode}: {response.Body.GetRawText()}");
        return response;
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Runs SQL against the host's sample database, the one the ReportsSampleDb connection opens.</summary>
    private static object? Sample(TestSite site, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={site.ApplicationDatabaseFile(SampleReport.DatabaseFile)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}
