using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;
using AppBlocks.Blocks;
using AppBlocks.Schedules;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The sample Orders schedule in a real host: first boot imports and publishes its pages and sources; a member reads
/// the table in both shells with the default window, today marked, overlapping bars in their own lanes, an open-ended
/// bar, the legend and the links; the address moves the window; a copy of the page whose bars source lacks columns is
/// refused when published.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class ScheduleHttpTests
{
    private static readonly Guid SchedulePage = AppBlockSchema.Key("sample:orders-schedule");
    private static readonly Guid WebsiteSchedulePage = AppBlockSchema.Key("sample:orders-schedule-website");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    [Fact]
    [ResetPublishedContentServices]
    public async Task The_sample_schedule_renders_in_both_shells_with_its_window_lanes_open_bars_and_legend()
    {
        await using var site = await TestSite.StartAsync(new() { Application = typeof(Program).Assembly });
        await site.WaitForPublishedAsync(SchedulePage);
        await site.WaitForPublishedAsync(WebsiteSchedulePage);
        var member = site.CreateMember("schedule", "schedule@example.invalid", "Schedule member");

        // Nobody signed in: the page asks for a sign-in and shows no schedule.
        using (var anonymous = site.CreateClient())
        {
            using var response = await anonymous.GetAsync(site.PathBase + "/orders-schedule/");
            Assert.DoesNotContain("data-app-schedule", await response.Content.ReadAsStringAsync());
        }

        using var client = await site.SignInMemberAsync(member);
        var html = await Page(client, site.PathBase + "/orders-schedule/");
        Assert.Contains("<body class=\"app\">", html);
        // The default window: 7 days before today to 14 after, so today is the eighth column and the header marks it.
        var today = DateTime.Today;
        Assert.Contains($"{Date(today.AddDays(-7))} to {Date(today.AddDays(14))}", html);
        Assert.Contains("data-today-column=\"7\"", html);
        Assert.Matches("<th scope=\"col\" class=\"app-schedule-col[^\"]*app-schedule-col--today\" aria-current=\"date\">", html);
        Assert.Equal(22, Regex.Matches(html, "<th scope=\"col\" class=\"app-schedule-col").Count);
        // Rows are the customers under their region (23 cells across); two overlapping orders take two lanes; a pending order is open-ended.
        Assert.Contains("<tr class=\"app-schedule-group-title\"><th scope=\"rowgroup\" colspan=\"23\">", html);
        Assert.Contains("--ah-lane: 1", html);
        Assert.Contains("app-schedule-bar--open", html);
        Assert.Matches("<a class=\"app-schedule-bar app-schedule-kind-\\d[^\"]*\" href=\"/orders-form/\\?id=\\d+\" style=\"--ah-span: \\d+; --ah-lane: \\d+\" data-key=\"\\d+\" data-kind=\"[A-Z]+\">Order \\d+<span class=\"app-schedule-bar__details\">: ", html);
        Assert.Contains(" onward, PENDING</span>", html);
        Assert.Contains("<ul class=\"app-schedule-legend ah-schedule__legend\" aria-label=\"Legend\">", html);
        Assert.Contains("PENDING</li>", html);
        Assert.Contains("<h2 class=\"ah-schedule__title\" id=\"schedule-", html);

        // The address sets the window; the Earlier and Later links move it by its own length; a window without orders says so.
        var asked = await Page(client, site.PathBase + "/orders-schedule/?from=2026-06-01&to=2026-06-07");
        Assert.Contains("1 June 2026 to 7 June 2026", asked);
        Assert.Contains($"href=\"{site.PathBase}/orders-schedule/?from=2026-05-25&amp;to=2026-05-31\" rel=\"prev\"", asked);
        Assert.Contains($"href=\"{site.PathBase}/orders-schedule/?from=2026-06-08&amp;to=2026-06-14\" rel=\"next\"", asked);
        Assert.Contains("No bars between 1 June 2026 and 7 June 2026.", asked);
        Assert.DoesNotContain("app-schedule-col--today", asked);
        Assert.DoesNotContain("app-schedule-bar ", asked);

        // The website shell renders the same block, with its own form as the bars' link.
        var website = await Page(client, site.PathBase + "/website-orders-schedule/");
        Assert.Contains("id=\"site-header\"", website);
        Assert.Matches("href=\"/website-content-blocks/\\?id=\\d+\"", website);
        Assert.Contains("--ah-lane: 1", website);
        Assert.Contains("app-schedule-bar--open", website);

        // Both shells' stylesheets style the table, and the runtime mounts it.
        foreach (var stylesheet in new[] { "/dist/css/app.css", "/dist/css/site.css" })
            Assert.Contains(".app-schedule-scroll", await client.GetStringAsync(site.PathBase + stylesheet));
        Assert.Contains("[data-app-schedule]", await client.GetStringAsync(site.PathBase + "/App_Plugins/ApplicationBlocks/runtime.js"));

        // Publishing checks the sources against the catalogue: a copy whose bars come from the rows source lacks the bar columns.
        using var scope = site.CreateScope();
        var contents = scope.ServiceProvider.GetRequiredService<IContentService>();
        var original = contents.GetById(SchedulePage)!;
        var blocks = JsonNode.Parse(original.GetValue<string>(AppBlockSchema.BodyAlias)!)!;
        foreach (var value in blocks["contentData"]![0]!["values"]!.AsArray())
            if (value!["alias"]!.GetValue<string>() == ScheduleSettings.BarsSourceAlias) value["value"] = "orders.schedule-rows";
        var copy = contents.Create("Broken schedule", original.ParentId, AppBlockSchema.PageAlias);
        copy.TemplateId = original.TemplateId;
        copy.SetValue(AppBlockSchema.BodyAlias, blocks.ToJsonString());
        Assert.True(contents.Save(copy).Success);
#pragma warning disable CS0618
        var published = contents.Publish(copy, ["*"]);
#pragma warning restore CS0618
        Assert.False(published.Success);
        var messages = string.Join(" ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []);
        Assert.Contains("The schedule's bars source 'orders.schedule-rows' has no column row, start, end, kind, link", messages);
    }

    private static string Date(DateTime date) => date.ToString("d MMMM yyyy", English);

    private static async Task<string> Page(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, html);
        Assert.Contains("data-app-schedule", html);
        Assert.DoesNotContain("app-schedule-problem", html);
        Assert.DoesNotContain("Could not render", html);
        return html;
    }
}
