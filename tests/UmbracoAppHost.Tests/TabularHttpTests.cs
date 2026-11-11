using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UmbracoAppHost.Application;
using AppBlocks.Blocks;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The sample Orders form's order lines in the real host. What the block does, its batches, its refusals and its retries, is AppBlocks'
/// to prove, in its own <c>TabularHttpTests</c>; this is that the host's saved export, its sample data and both shells' stylesheets put it together.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class TabularHttpTests
{
    private static readonly Guid WebsiteContentBlocksPage = Guid.Parse("d9a69f9f-f8e4-dc51-76c8-9d1ec178b238");
    private static readonly Guid WebsiteLines = ApplicationStarter.Key("sample:website-content-blocks:lines");
    private static readonly string[] Columns = ["item", "quantity", "unitPrice", "notes"];

    [Fact]
    [ResetPublishedContentServices]
    public async Task The_sample_orders_lines_block_reads_the_sample_data_and_both_shells_style_it()
    {
        await using var site = await TestSite.StartAsync(new() { Application = typeof(Program).Assembly });
        await site.WaitForPublishedAsync(ApplicationStarter.FormPageKey);
        await site.WaitForPublishedAsync(WebsiteContentBlocksPage);
        var member = site.CreateMember("lines", "lines@example.invalid", "Lines member");
        using var client = await site.SignInMemberAsync(member);
        var lines = site.Tabular(client, ApplicationStarter.FormPageKey, ApplicationStarter.FormLinesBlockKey);

        // The sample's class refuses someone with no account; a record has to exist.
        using (var anonymous = site.CreateClient())
            Assert.Equal(HttpStatusCode.Forbidden, (await site.Tabular(anonymous, ApplicationStarter.FormPageKey, ApplicationStarter.FormLinesBlockKey).ListAsync("id=4")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lines.ListAsync("id=999999")).Response.StatusCode);

        // The seeded lines of order 4, with the export's columns and the saved data source's rules and defaults.
        var seeded = Keys(site, 4);
        Assert.Equal(2, seeded.Length);
        var list = await lines.ListAsync("id=4");
        Assert.True(list.IsSuccess, list.Problem);
        Assert.Equal(seeded, list.Rows.Select(r => r.GetProperty("key").GetString()).ToArray());
        Assert.True(list.CanAdd && list.CanUpdate && list.CanDelete);
        Assert.Equal("sample.orders", list.Body.GetProperty("owner").GetProperty("alias").GetString());
        Assert.Equal("orders.lines", list.Body.GetProperty("dataSource").GetString());
        Assert.Equal(50, list.Body.GetProperty("maxRows").GetInt32());
        Assert.Equal(Columns, list.Fields.Select(f => f.GetProperty("key").GetString()!).ToArray());
        Assert.Equal(200, list.Fields[0].GetProperty("maxLength").GetInt32());
        Assert.Equal(Sample(site, $"SELECT ITEM FROM ORDER_LINES WHERE LINE_ID = {seeded[0]}"), list.Rows[0].GetProperty("values").GetProperty("item").GetString());

        // A line saved through the host lands in the sample database, linked to the order and with who wrote it.
        // Its name is not one of the seed's, so nothing but this line answers to it.
        const string saving = "Line saved through the host";
        var saved = await lines.SaveAsync([new("create", Values: new { item = saving, quantity = "3", unitPrice = "0.50", notes = (string?)null })], "id=4");
        Assert.True(saved.IsSuccess, saved.Body.ToString());
        Assert.Equal(4L, Sample(site, $"SELECT ORDER_ID FROM ORDER_LINES WHERE ITEM = '{saving}'"));
        Assert.Equal("lines", Sample(site, $"SELECT CREATED_BY FROM ORDER_LINES WHERE ITEM = '{saving}'"));

        // The website page's block, inside its own Orders form, lists the same lines.
        var website = await site.Tabular(client, WebsiteContentBlocksPage, WebsiteLines).ListAsync("id=4");
        Assert.True(website.IsSuccess, website.Problem);
        Assert.Equal(Keys(site, 4), website.Rows.Select(r => r.GetProperty("key").GetString()!).ToArray());

        // The test client must carry the current checksum on updates and deletes, as the browser does.
        var row = Assert.Single(saved.Rows, row => row.GetProperty("values").GetProperty("item").GetString() == saving);
        var key = row.GetProperty("key").GetString();
        var checksum = row.GetProperty("checksum").GetString();
        var updated = await lines.SaveAsync([new("update", key, checksum, new { quantity = "4" })], "id=4");
        Assert.True(updated.IsSuccess, updated.Body.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await lines.SaveAsync([new("update", key, checksum, new { quantity = "5" })], "id=4")).Response.StatusCode);
        var current = Assert.Single(updated.Rows, row => row.GetProperty("key").GetString() == key);
        var removed = await lines.SaveAsync([new("delete", key, current.GetProperty("checksum").GetString())], "id=4");
        Assert.True(removed.IsSuccess, removed.Body.ToString());
        Assert.Equal(seeded, Keys(site, 4));

        // The page renders in the host's own template; both shells' stylesheets carry the block.
        using (var page = await site.GetPageAsync(client, "/orders-form/?id=4"))
        {
            var html = await page.Content.ReadAsStringAsync();
            Assert.True(page.IsSuccessStatusCode, html);
            Assert.Contains("data-app-tabular", html);
            Assert.Contains($"data-api=\"{site.PathBase}/api/tabular/{ApplicationStarter.FormPageKey}/{ApplicationStarter.FormLinesBlockKey}\"", html);
        }
        foreach (var stylesheet in new[] { "/dist/css/app.css", "/dist/css/site.css" })
            Assert.Contains(".ah-tabular__scroll", await client.GetStringAsync(site.PathBase + stylesheet));
    }

    /// <summary>The keys of an order's lines in the sample database, in key order.</summary>
    private static string[] Keys(TestSite site, int order)
    {
        using var connection = Open(site);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT LINE_ID FROM ORDER_LINES WHERE ORDER_ID = {order} ORDER BY LINE_ID";
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return [.. keys];
    }

    /// <summary>Runs SQL against the host's sample database, the one the ReportsSampleDb connection opens.</summary>
    private static object? Sample(TestSite site, string sql)
    {
        using var connection = Open(site);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static SqliteConnection Open(TestSite site)
    {
        var connection = new SqliteConnection($"Data Source={site.ApplicationDatabaseFile(SampleReport.DatabaseFile)};Pooling=False");
        connection.Open();
        return connection;
    }
}
