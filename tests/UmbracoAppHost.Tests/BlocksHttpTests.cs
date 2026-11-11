using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;
using AppBlocks.Blocks;
using AppBlocks.Connections;
using AppBlocks.Forms;
using UmbracoAppHost.Application;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class BlocksHttpTests
{
    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "/wing")]
    [InlineData(true, "/wing")]
    [ResetPublishedContentServices]
    public async Task Published_instances_enforce_page_access_antiforgery_and_unpublishing(bool administrator, string prefix)
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            VirtualPath = prefix,
            // Members sign in with the local password form: no identity server to start, and Umbraco keeps the accounts.
            Settings = { ["IdentityServer:Enabled"] = "false", ["Accounts:MembersManagedBy"] = "Umbraco" },
        });
        Assert.Equal(prefix, site.PathBase);
        using var scope = site.CreateScope(); var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        for (var i = 0; i < 100 && types.Get(ApplicationStarter.BlockPageAlias)?.AllowedTemplates?.Any() != true; i++) await Task.Delay(100);
        Assert.NotNull(types.Get(ApplicationStarter.BlockPageAlias));
        var contents = services.GetRequiredService<IContentService>();
        var form = contents.GetById(ApplicationStarter.FormPageKey)!;
        var report = contents.GetById(ApplicationStarter.ReportPageKey)!;
        Assert.True(form.Published);
        Assert.True(report.Published);
        // The sample's connection, which that report's source names, is in the list its picker offers.
        var sample = Assert.Single(services.GetRequiredService<ConnectionCatalog>().List(), connection => connection.Name == SampleReport.Connection);
        Assert.True(sample.ReadOnly);
        Assert.Equal("sqlite", sample.Provider);
        var member = site.CreateMember("block-test", "member@example.invalid", "Block test member");
        const string password = "Local-test-only-Member91!";
        await site.SetPasswordAsync(member, password);
        using var client = site.CreateClient();
        var pageUrl = site.PathBase + "/orders-form/?id=4";
        var forms = site.Forms(client, form.Key, ApplicationStarter.FormKey);
        var reports = site.Reports(client);
        var formUrl = $"{site.PathBase}/api/forms/{form.Key}/{ApplicationStarter.FormKey}";
        var reportName = AppBlockContent.ReportName(report.Key, ApplicationStarter.ReportKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await forms.LoadAsync()).Response.StatusCode);
        Assert.False((await reports.SchemaAsync(reportName)).IsSuccessStatusCode);
        var loginHtml = await client.GetStringAsync(pageUrl);
        Assert.Contains("loginModel.Username", loginHtml);
        Assert.DoesNotContain("No sign-in provider", loginHtml);
        var login = ReadForm(loginHtml, "app-login-form");
        login.Fields["loginModel.Username"] = member.Username;
        login.Fields["loginModel.Password"] = password;
        var noToken = new Dictionary<string, string>(login.Fields);
        noToken.Remove("__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(login.Action, new FormUrlEncodedContent(noToken))).StatusCode);
        login.Fields["loginModel.Password"] = "Wrong-password91!";
        var invalidLogin = await client.PostAsync(login.Action, new FormUrlEncodedContent(login.Fields));
        Assert.Contains("Invalid username or password", await invalidLogin.Content.ReadAsStringAsync());
        if (administrator)
        {
            // The same browser signs in to the backoffice, so it later carries both cookies.
            var signedIn = await client.PostAsJsonAsync(site.PathBase + "/umbraco/management/api/v1/security/back-office/login",
                new { username = site.AdminUserName, password = site.AdminPassword });
            Assert.True(signedIn.IsSuccessStatusCode, await signedIn.Content.ReadAsStringAsync());
        }
        else
        {
            login.Fields["loginModel.Password"] = password;
            var signedIn = await client.PostAsync(login.Action, new FormUrlEncodedContent(login.Fields));
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            Assert.Equal(pageUrl, signedIn.Headers.Location?.OriginalString);
        }
        var rendered = await client.GetAsync(pageUrl);
        Assert.True(rendered.IsSuccessStatusCode, await rendered.Content.ReadAsStringAsync());
        var html = await rendered.Content.ReadAsStringAsync();
        Assert.Contains("data-app-form", html);
        Assert.Contains("data-field=\"customer\"", html);
        Assert.DoesNotContain("invalid resource", html);
        Assert.Contains("<details class=\"app-account\">", html);
        Assert.Contains(administrator ? "Backoffice account" : "Member account", html);
        Assert.Contains("Sign out", html);
        var loaded = await forms.LoadAsync("4");
        Assert.True(loaded.IsSuccess, loaded.Body.GetRawText());
        Assert.Equal("4", loaded.RecordKey);
        Assert.Equal(25, (await reports.RowsAsync(reportName)).GetArrayLength());
        // Without the antiforgery header the browser runtime sends, a write is refused.
        var submission = new { definitionVersion = loaded.DefinitionVersion, checksum = loaded.RecordChecksum, values = new { notes = "Changed through form endpoint" }, submissionKey = Guid.NewGuid().ToString() };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(formUrl + "/4", submission)).StatusCode);
        var injection = await forms.UpdateAsync("4", loaded.RecordChecksum!, new { orderId = "999" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, injection.Response.StatusCode);
        var update = await forms.UpdateAsync("4", loaded.RecordChecksum!, submission.values);
        Assert.True(update.IsSuccess, update.Body.GetRawText());
        Assert.Equal(HttpStatusCode.Conflict, (await forms.UpdateAsync("4", loaded.RecordChecksum!, submission.values)).Response.StatusCode);

        var access = services.GetRequiredService<IPublicAccessService>();
        var entry = new PublicAccessEntry(report, form, form, [new PublicAccessRule { RuleType = Constants.Conventions.PublicAccess.MemberRoleRuleType, RuleValue = "No test members" }]);
        Assert.True(access.Save(entry).Success);
        // Public Access is the baseline: a member it does not name is refused, while a backoffice user who
        // may edit the page passes it, as the administrator may edit every page.
        Assert.Equal(administrator, (await client.GetAsync($"{site.PathBase}/api/reports/{reportName}/default")).IsSuccessStatusCode);
        access.Delete(entry);
#pragma warning disable CS0618
        Assert.True(contents.Unpublish(report).Success);
#pragma warning restore CS0618
        Assert.False((await reports.SchemaAsync(reportName)).IsSuccessStatusCode);
        if (administrator)
            (await client.GetAsync($"{site.PathBase}/__tests/login/{member.Id}?scheme=member")).EnsureSuccessStatusCode();
        html = await client.GetStringAsync(pageUrl);
        Assert.Contains("Member account", html); // A member takes precedence when both accounts are signed in.
        var logout = ReadForm(html, "app-inline-form");
        Assert.Equal(site.PathBase + "/application/account/logout", logout.Action);
        var noLogoutToken = new Dictionary<string, string>(logout.Fields);
        noLogoutToken.Remove("__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(logout.Action, new FormUrlEncodedContent(noLogoutToken))).StatusCode);
        var signedOut = await client.PostAsync(logout.Action, new FormUrlEncodedContent(logout.Fields));
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal(pageUrl, signedOut.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Forbidden, (await forms.LoadAsync()).Response.StatusCode);
        Assert.Contains("loginModel.Username", await client.GetStringAsync(pageUrl));

        if (administrator)
        {
            var users = services.GetRequiredService<IUserService>();
            var groups = services.GetRequiredService<IUserGroupService>();
            var editor = await site.CreateUserAsync("application-editor", "application-editor@example.invalid", "Application editor", ConfiguredAccounts.ReportAdministratorsAlias);
            await site.SetPasswordAsync(editor, password);
            (await client.PostAsJsonAsync(site.PathBase + "/umbraco/management/api/v1/security/back-office/login",
                new { username = editor.Username, password })).EnsureSuccessStatusCode();
            // Any backoffice user is a signed-in visitor on an open page; the sample's Orders form still wants Administrators.
            Assert.Contains("Backoffice account", await client.GetStringAsync(pageUrl));
            Assert.Equal(HttpStatusCode.Forbidden, (await forms.LoadAsync()).Response.StatusCode);

            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, editor.Key.ToString()), new(ClaimTypes.Role, Constants.Security.AdminGroupAlias)
            ], Constants.Security.BackOfficeExposedAuthenticationType));
            var context = new FormExecutionContext(principal, form.Key, ApplicationStarter.FormKey, "1", Guid.NewGuid().ToString());
            var resource = services.GetRequiredService<FormResourceRegistry>().RegisteredResources["sample.orders"];
            var input = JsonSerializer.SerializeToElement(new { customer = "Test", region = "NORTH", status = "NEW", amount = "1.00", orderDate = "2026-09-16" });
            Assert.Equal(403, (await resource.InitializeAsync(context, default)).Status);
            Assert.Equal(403, (await resource.LoadAsync("4", context, default)).Status);
            Assert.Equal(403, (await resource.CreateAsync(input, context, default)).Status);
            Assert.Equal(403, (await resource.UpdateAsync("4", input, "1", context, default)).Status);
            Assert.Equal(403, (await resource.DeleteAsync("4", "1", context, default)).Status);

            // Current group membership controls access, not stale or forged role claims in a ticket.
            editor.AddGroup((await groups.GetAsync(Constants.Security.AdminGroupAlias))!.ToReadOnlyGroup());
            users.Save(editor);
            Assert.Contains("Backoffice account", await client.GetStringAsync(pageUrl));
            Assert.Equal(HttpStatusCode.OK, (await forms.LoadAsync()).Response.StatusCode);
            Assert.Equal(200, (await resource.LoadAsync("4", context, default)).Status);
            var created = await resource.CreateAsync(input, context, default);
            Assert.Equal(200, created.Status);
            var updated = await resource.UpdateAsync(created.Record!.Key!, input, created.Record.Checksum!, context, default);
            Assert.Equal(200, updated.Status);
            Assert.Equal(204, (await resource.DeleteAsync(updated.Record!.Key!, updated.Record.Checksum!, context, default)).Status);
            editor.RemoveGroup(Constants.Security.AdminGroupAlias);
            users.Save(editor);
            Assert.Equal(HttpStatusCode.Forbidden, (await forms.LoadAsync()).Response.StatusCode);
            Assert.Equal(403, (await resource.LoadAsync("4", context, default)).Status);
        }
    }

    private static (string Action, Dictionary<string, string> Fields) ReadForm(string html, string cssClass)
    {
        var form = Regex.Matches(html, "<form\\b[^>]*>.*?</form>", RegexOptions.Singleline).Cast<Match>()
            .Single(match => match.Value.Contains($"class=\"{cssClass}\"", StringComparison.Ordinal)).Value;
        var fields = new Dictionary<string, string>();
        foreach (Match input in Regex.Matches(form, "<input\\b[^>]*>"))
        {
            var name = Regex.Match(input.Value, "\\bname=\"([^\"]*)\"");
            if (name.Success) fields[WebUtility.HtmlDecode(name.Groups[1].Value)] = WebUtility.HtmlDecode(Regex.Match(input.Value, "\\bvalue=\"([^\"]*)\"").Groups[1].Value);
        }
        return (WebUtility.HtmlDecode(Regex.Match(form, "\\baction=\"([^\"]*)\"").Groups[1].Value), fields);
    }
}
