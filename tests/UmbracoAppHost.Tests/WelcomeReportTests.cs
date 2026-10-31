using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;
using AppBlocks.InteractiveReports.Reports;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class WelcomeReportTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/wing")]
    [ResetPublishedContentServices]
    public async Task Welcome_lists_latest_versions_with_paging_paths_links_and_content_permissions(string prefix)
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            EnvironmentName = "Production",
            VirtualPath = prefix,
            // This report fixture creates and counts its own content tree.
            FirstBootGroup = "Settings",
        });
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var content = services.GetRequiredService<IContentService>();
        var users = services.GetRequiredService<IUserService>();
        var groups = services.GetRequiredService<IUserGroupService>();
        var pageType = new ContentType(services.GetRequiredService<IShortStringHelper>(), Constants.System.Root)
        {
            Name = "Welcome test page", Alias = "welcomeTestPage", AllowedAsRoot = true,
        };
        Assert.True((await services.GetRequiredService<IContentTypeService>().CreateAsync(pageType, Constants.Security.SuperUserKey)).Success);
        var group = new UserGroup(services.GetRequiredService<IShortStringHelper>()) { Name = "Welcome editors", Alias = "welcome-editors", Permissions = new HashSet<string> { ActionBrowse.ActionLetter, ActionUpdate.ActionLetter } };
        group.AddAllowedSection(Constants.Applications.Content);
        Assert.True((await groups.CreateAsync(group, Constants.Security.SuperUserKey)).Success);
        var editor = users.CreateUserWithIdentity("welcome-editor", "welcome-editor@example.invalid");
        editor.Name = "Welcome Editor";
        editor.IsApproved = true;
        editor.AddGroup(group.ToReadOnlyGroup());
        users.Save(editor);

#pragma warning disable CS0618
        IContent Create(string name, int parent = Constants.System.Root)
        {
            var page = content.Create(name, parent, pageType.Alias);
            Assert.True(content.Save(page).Success);
            return page;
        }
        var home = Create("Welcome test home");
        Assert.True(content.Publish(home, ["*"]).Success);
        var otherRoot = Create("Outside editor start node");
        for (var i = 1; i <= 14; i++) Create($"Page {i:00}", home.Id);
        var deep = home;
        var path = home.Name!;
        for (var i = 1; i <= 12; i++)
        {
            deep = Create($"Level {i}", deep.Id);
            path += " / " + deep.Name;
        }
        var trashed = Create("Recycled page", home.Id);
        Assert.True(content.MoveToRecycleBin(trashed).Success);
        var latest = Create("Earlier title", home.Id);
        Assert.True(content.Publish(latest, ["*"]).Success);
        latest.Name = "Latest draft";
        Assert.True(content.Save(latest, editor.Id).Success);
#pragma warning restore CS0618
        editor.StartContentIds = [home.Id];
        users.Save(editor);

        // Each browser carries one identity: anonymous, a member, then a backoffice user.
        var api = site.PathBase + InteractiveReportsSite.BackOfficeReportsRoute + "/" + RecentPagesReport.Name;
        using (var anonymous = site.CreateClient())
            Assert.False((await anonymous.GetAsync(api + "/default")).IsSuccessStatusCode);
        using (var member = await site.SignInMemberAsync(editor.Id, "Report test"))
        {
            Assert.False((await member.GetAsync(api + "/default")).IsSuccessStatusCode);
            // Interactive Reports conceals definitions that the caller is not authorized to view.
            Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync(site.PathBase + "/api/reports/" + RecentPagesReport.Name + "/default")).StatusCode);
        }
        using var administrator = await SignIn("backoffice", Constants.Security.SuperUserKey.ToString());
        var client = administrator;

        var loaded = await Get(api + "/default");
        var result = loaded["result"]!;
        var rows = result["rows"]!.AsArray();
        Assert.Equal(10, rows.Count);
        Assert.Equal(10, result["page"]!["size"]!.GetValue<int>());
        Assert.Equal("29", result["totalRows"]!.ToString());
        Assert.Equal("Latest draft", rows[0]!["NAME"]!.GetValue<string>());
        Assert.Equal("Welcome Editor", rows[0]!["EDITED_BY"]!.GetValue<string>());
        Assert.Equal(home.Name + " / Latest draft", rows[0]!["PATH"]!.GetValue<string>());
        Assert.NotEmpty(rows[0]!["EDITED_AT"]!.GetValue<string>());
        Assert.Equal(latest.Key.ToString(), rows[0]!["PAGE_KEY"]!.GetValue<string>());
        var schema = await Get(api + "/schema");
        Assert.Equal(prefix + "/umbraco/section/content/workspace/document/edit/{PAGE_KEY}", schema["editLink"]!["urlTemplate"]!.GetValue<string>());
        Assert.Equal("_self", schema["editLink"]!["target"]!.GetValue<string>());

        var state = result["document"]!.DeepClone();
        state["page"]!["index"] = 2;
        var second = await Query(state);
        Assert.Equal(10, second["rows"]!.AsArray().Count);
        Assert.Empty(rows.Select(r => r!["NAME"]!.ToString()).Intersect(second["rows"]!.AsArray().Select(r => r!["NAME"]!.ToString())));
        state["page"]!["index"] = 1;
        state["search"] = "Level 12";
        var nested = Assert.Single((await Query(state))["rows"]!.AsArray())!;
        Assert.Equal(path, nested["PATH"]!.GetValue<string>());
        state["search"] = "Recycled page";
        Assert.Empty((await Query(state))["rows"]!.AsArray());
        state["search"] = "No such page";
        Assert.Empty((await Query(state))["rows"]!.AsArray());

        // Explicit permissions are inherited, and a deeper grant can restore access.
        var denied = content.GetById(deep.ParentId)!;
        users.ReplaceUserGroupPermissions(group.Id, new HashSet<string> { ActionUpdate.ActionLetter }, [denied.Id]);
        using var editorBrowser = await SignIn("backoffice", editor.Key.ToString());
        client = editorBrowser;
        state["search"] = "Outside editor";
        Assert.Empty((await Query(state))["rows"]!.AsArray());
        state["search"] = "Level 12";
        Assert.Empty((await Query(state))["rows"]!.AsArray());
        users.ReplaceUserGroupPermissions(group.Id, new HashSet<string> { ActionBrowse.ActionLetter }, [deep.Id]);
        Assert.Single((await Query(state))["rows"]!.AsArray());
        state["search"] = "Latest draft";
        Assert.Single((await Query(state))["rows"]!.AsArray());
        // A website member signed in beside the backoffice user does not change what the backoffice report shows.
        (await client.GetAsync($"{site.PathBase}/__tests/login/different-website-member?scheme=member&name=Report%20test")).EnsureSuccessStatusCode();
        Assert.Single((await Query(state))["rows"]!.AsArray());
        state["search"] = null;
        var download = await client.PostAsJsonAsync(site.PathBase + InteractiveReportsSite.BackOfficeDownloadRoute + "/" + RecentPagesReport.Name + "/csv", state);
        var csv = await download.Content.ReadAsStringAsync();
        Assert.True(download.IsSuccessStatusCode, csv);
        Assert.Contains("Latest draft", csv);
        Assert.DoesNotContain("Outside editor start node", csv);
        Assert.DoesNotContain("Recycled page", csv);

        // A new browser signed in by the test login route without a password.
        async Task<HttpClient> SignIn(string scheme, string id)
        {
            var browser = site.CreateClient();
            (await browser.GetAsync($"{site.PathBase}/__tests/login/{id}?scheme={scheme}&name=Report%20test")).EnsureSuccessStatusCode();
            return browser;
        }
        async Task<JsonNode> Get(string url)
        {
            var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            return JsonNode.Parse(body)!;
        }
        async Task<JsonNode> Query(JsonNode state)
        {
            var response = await client.PostAsJsonAsync(api + "/query", state);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            return JsonNode.Parse(body)!;
        }
    }
}
