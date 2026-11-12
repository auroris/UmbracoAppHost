using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Strings;
using AppBlocks.Blocks;
using AppBlocks.InteractiveReports.Blocks;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class WebPagesHttpTests
{
    private const string AdministratorName = "Website test administrator";
    private string responses = "";
    private int responseNumber;

    [Fact]
    [ResetPublishedContentServices]
    public async Task Public_websites_render_content_and_keep_nested_forms_reports_and_private_branches_protected()
    {
        await using var host = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            Settings = { ["IdentityServer:Enabled"] = "false" },
            ConfigureBuilder = builder =>
            {
                builder.Services.Configure<MvcOptions>(options => options.Filters.Add(new RenderedTemplateHeader()));
                FirstMiddleware.Add(builder, async (context, next) => {
                    context.Items[AppPageAssets.CspNonceItem] = "website-test-nonce";
                    await next(context);
                });
            },
        });
        responses = Directory.CreateDirectory(Path.Combine(host.Root, "responses")).FullName;
        using var scope = host.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        var contents = services.GetRequiredService<IContentService>();
        var webType = types.Get("webPage");
        Assert.NotNull(webType);
        var customType = new ContentType(services.GetRequiredService<IShortStringHelper>(), Constants.System.Root) {
            Alias = "customWebsitePage", Name = "Custom website page", Variations = ContentVariation.Culture,
            AllowedTemplates = webType.AllowedTemplates,
        };
        customType.SetDefaultTemplate(webType.DefaultTemplate!);
        Assert.True(customType.AddContentType(types.Get("webPageContent")!));
        Assert.True((await types.CreateAsync(customType, Constants.Security.SuperUserKey)).Success);

#pragma warning disable CS0618
        IContent Create(string name, int parent, string alias, JsonNode? english = null, JsonNode? french = null)
        {
            var page = contents.Create(name, parent, alias);
            page.SetCultureName(name, "en");
            page.SetCultureName(name, "fr");
            page.TemplateId = types.Get(alias)!.DefaultTemplate?.Id;
            if (english is not null) page.SetValue(ApplicationStarter.BlockPropertyAlias, english.ToJsonString(), "en");
            if (french is not null || english is not null) page.SetValue(ApplicationStarter.BlockPropertyAlias, (french ?? english)!.ToJsonString(), "fr");
            Save(page);
            return page;
        }
        void Save(IContent page)
        {
            Assert.True(contents.Save(page).Success);
            var result = contents.Publish(page, ["*"]);
            Assert.True(result.Success, string.Join(" ", result.EventMessages?.GetAll().Select(message => message.Message) ?? []));
        }
        var site = Create("Public website", Constants.System.Root, "appHostWebsite", WebPageTestContent.Static("website-home-marker"));
        site.SetValue("css", "/* ancestor-css-marker */");
        site.SetValue("javascript", "/* ancestor-js-marker */");
        Save(site);
        var domains = await services.GetRequiredService<IDomainService>().UpdateDomainsAsync(site.Key, new DomainsUpdateModel {
            DefaultIsoCode = "en", Domains = [new DomainModel { DomainName = "en.website.test", IsoCode = "en" }, new DomainModel { DomainName = "fr.website.test", IsoCode = "fr" }],
        });
        Assert.True(domains.Success);
        var overviewContent = WebPageTestContent.Static("public-overview-marker");
        WebPageTestContent.Layout(overviewContent).Add(WebPageTestContent.Add(overviewContent, AppBlockSchema.CssAlias, ("css", "/* block-css-marker */")));
        WebPageTestContent.Layout(overviewContent).Add(WebPageTestContent.Add(overviewContent, AppBlockSchema.ScriptAlias, ("script", "/* block-js-marker */")));
        var overview = Create("Overview", site.Id, "webPage", overviewContent);
        overview.SetValue("css", "/* page-css-marker */");
        overview.SetValue("javascript", "/* page-js-marker */");
        Save(overview);
        var menu = JsonSerializer.Serialize(new object[] {
            new {
                key = Guid.NewGuid(), name = "Website resources", itemType = "Title",
                children = new[] { new {
                    key = Guid.NewGuid(), name = "Overview link", itemType = "Document", contentKey = overview.Key,
                    udi = "umb://document/" + overview.Key.ToString("N"), published = true, children = Array.Empty<object>(),
                } },
            },
            // Menu tokens: who is signed in, the profile page picked below (members only) and the sign-out form.
            new { key = Guid.NewGuid(), name = "Signed in as {{user.name}}", itemType = "Title", hideLoggedOut = true, children = Array.Empty<object>() },
            new { key = Guid.NewGuid(), name = "My profile", itemType = "Link", url = "{{user.profile}}", children = Array.Empty<object>() },
            new { key = Guid.NewGuid(), name = "Sign out", itemType = "Link", url = "{{user.signout}}", hideLoggedOut = true, children = Array.Empty<object>() },
        });
        site.SetValue("topNavigationMenu", menu, "en");
        site.SetValue("topNavigationMenu", menu, "fr");
        site.SetValue("profilePage", "umb://document/" + overview.Key.ToString("N"));
        Save(site);
        var login = Create("Member login", site.Id, "webPage", WebPageTestContent.Static("public-login-marker"));
        login.TemplateId = types.Get("webPage")!.AllowedTemplates!.Single(template => template.Alias == "SiteSignIn").Id;
        Save(login);
        var english = WebPageTestContent.Interactive();
        WebPageTestContent.SetValue(WebPageTestContent.Blocks(english, "appReport").Single(), "formPage", "umb://document/" + overview.Key.ToString("N"));
        var french = english.DeepClone();
        WebPageTestContent.SetValue(WebPageTestContent.Blocks(french, "appReport").Single(), "pageSize", "7");
        WebPageTestContent.SetValue(WebPageTestContent.Blocks(french, "appReport").Single(), "heading", "Commandes en français");
        var frenchForm = WebPageTestContent.Blocks(french, "appForm").First();
        WebPageTestContent.SetValue(WebPageTestContent.Value(frenchForm, "formInterface")!["contentData"]![0]!, "keyParameter", "commande");
        var mixed = Create("Mixed content", site.Id, customType.Alias, english, french);
        var nestedSite = Create("Private tools", site.Id, "appHostWebsite", WebPageTestContent.Static("private-site-marker"));
        nestedSite.TemplateId = types.Get("appHostWebsite")!.AllowedTemplates!.Single(template => template.Alias == "ApplicationSite").Id;
        Save(nestedSite);
        var privatePage = Create("Private guide", nestedSite.Id, "webPage", WebPageTestContent.Static("private-guide-marker"));
        var privateReference = Create("Private reference", site.Id, "webPage", WebPageTestContent.Static("private-reference-marker"));
        var deniedPage = Create("Access denied", site.Id, "webPage", WebPageTestContent.Static("access-denied-marker"));
#pragma warning restore CS0618

        var address = host.Address;
        using var anonymous = Client(address, "en.website.test");
        using var administrator = Client(address, "en.website.test", useCookies: false);
        var signIn = await administrator.GetAsync($"/__tests/login/{Constants.Security.SuperUserKey}?scheme=backoffice&name={Uri.EscapeDataString(AdministratorName)}");
        signIn.EnsureSuccessStatusCode();
        // Like the existing block HTTP tests, carry the issued backoffice cookie explicitly instead of
        // relying on cookie-container policy for a loopback HTTP endpoint with a simulated site hostname.
        administrator.DefaultRequestHeaders.Add("Cookie", Cookies(signIn));

        var home = await Page(anonymous, "/");
        Assert.Contains("website-home-marker", home.Html);
        var publicPage = await Page(anonymous, "/overview/");
        Assert.Contains("public-overview-marker", publicPage.Html);
        Assert.Contains("id=\"site-header\"", publicPage.Html);
        Assert.DoesNotContain("data-app-form", publicPage.Html);
        Assert.DoesNotContain("app-block-signin", publicPage.Html);
        Assert.Contains("WebsiteHome", publicPage.Response.Headers.GetValues("X-Test-Template").Single());
        AssertAuthoredMenu(publicPage.Html);
        AssertMenuTokens(publicPage.Html, signedInAs: null, profile: false);
        foreach (var asset in new[] { "css", "js" })
        {
            var markers = new[] { "ancestor-" + asset + "-marker", "page-" + asset + "-marker", "block-" + asset + "-marker" };
            Assert.All(markers, marker => Assert.Single(Regex.Matches(publicPage.Html, marker)));
            Assert.True(publicPage.Html.IndexOf(markers[0], StringComparison.Ordinal) < publicPage.Html.IndexOf(markers[1], StringComparison.Ordinal));
            Assert.True(publicPage.Html.IndexOf(markers[1], StringComparison.Ordinal) < publicPage.Html.IndexOf(markers[2], StringComparison.Ordinal));
        }
        Assert.Matches("<style nonce=\"website-test-nonce\">\\s*/\\* ancestor-css-marker", publicPage.Html);
        Assert.Matches("<script nonce=\"website-test-nonce\">\\s*/\\* ancestor-js-marker", publicPage.Html);

        var publicMixed = await Page(anonymous, "/mixed-content/");
        Assert.Contains("public-introduction-marker", publicMixed.Html);
        Assert.Equal(3, Regex.Matches(publicMixed.Html, "class=\"app-block-signin\"").Count);
        Assert.DoesNotContain("data-app-form", publicMixed.Html);
        Assert.DoesNotContain("data-app-report", publicMixed.Html);
        Assert.True(publicMixed.Response.Headers.CacheControl?.NoStore);
        Assert.Null(publicMixed.Response.Headers.ETag);

        // A conditional request must still render the admitted person's interactive blocks.
        administrator.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", publicPage.Response.Headers.ETag?.ToString());
        var admitted = await Page(administrator, "/mixed-content/");
        administrator.DefaultRequestHeaders.Remove("If-None-Match");
        Assert.Equal(2, Regex.Matches(admitted.Html, " data-app-form ").Count);
        Assert.Contains("data-app-report", admitted.Html);
        // A backoffice Administrator is signed in to the menu too, but has no member profile page.
        AssertMenuTokens(admitted.Html, AdministratorName, profile: false);
        Assert.DoesNotContain("invalid resource", admitted.Html);
        Assert.Contains("?appCulture=en", admitted.Html);
        Assert.True(admitted.Response.Headers.CacheControl?.NoStore);
        Assert.Null(admitted.Response.Headers.ETag);

        var formKeys = WebPageTestContent.Blocks(english, "appForm").Select(block => Guid.Parse(block["key"]!.GetValue<string>())).ToArray();
        var reportKey = Guid.Parse(WebPageTestContent.Blocks(english, "appReport").Single()["key"]!.GetValue<string>());
        var reportUrl = "/api/reports/" + ReportBlocks.Name(mixed.Key, reportKey) + "/default";
        Assert.False((await anonymous.GetAsync(reportUrl + "?appCulture=en")).IsSuccessStatusCode);
        foreach (var formKey in formKeys)
        {
            var formUrl = $"/api/forms/{mixed.Key}/{formKey}?appCulture=en&id=4&otherOrder=5";
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(formUrl)).StatusCode);
            var loaded = await Json(administrator, formUrl);
            Assert.Equal(formKey == formKeys[0] ? "4" : "5", loaded.GetProperty("record").GetProperty("key").GetString());
        }
        var englishRows = await Json(administrator, reportUrl + "?appCulture=en&appPageQuery=");
        Assert.Equal(25, englishRows.GetProperty("result").GetProperty("rows").GetArrayLength());
        var frenchRows = await Json(administrator, reportUrl + "?appCulture=fr&appPageQuery=");
        Assert.Equal(7, frenchRows.GetProperty("result").GetProperty("rows").GetArrayLength());
        var frenchSchema = await Json(administrator, reportUrl.Replace("/default", "/schema", StringComparison.Ordinal) + "?appCulture=fr&appPageQuery=");
        Assert.Contains("fr.website.test/overview/", frenchSchema.GetProperty("editLink").GetProperty("urlTemplate").GetString());
        Assert.Contains("fr.website.test/overview/", frenchSchema.GetProperty("createLink").GetProperty("url").GetString());
        var formBase = $"/api/forms/{mixed.Key}/{formKeys[0]}";
        var loadedEnglish = await Json(administrator, formBase + "?appCulture=en&id=4");
        var loadedFrench = await Json(administrator, formBase + "?appCulture=fr&commande=5");
        Assert.Equal("5", loadedFrench.GetProperty("record").GetProperty("key").GetString());
        Assert.NotEqual(loadedEnglish.GetProperty("definitionVersion").GetString(), loadedFrench.GetProperty("definitionVersion").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await administrator.GetAsync(formBase + "?appCulture=de")).StatusCode);
        administrator.DefaultRequestHeaders.Host = "fr.website.test";
        var frenchPage = await Page(administrator, "/mixed-content/");
        Assert.Contains("?appCulture=fr", frenchPage.Html);
        Assert.Contains("Commandes en fran", frenchPage.Html);
        Assert.Contains("data-key-parameter=\"commande\"", frenchPage.Html);
        var englishStateKey = Regex.Match(admitted.Html, "data-state-key=\"([^\"]+)\"").Groups[1].Value;
        var frenchStateKey = Regex.Match(frenchPage.Html, "data-state-key=\"([^\"]+)\"").Groups[1].Value;
        Assert.EndsWith(":en", englishStateKey);
        Assert.EndsWith(":fr", frenchStateKey);
        Assert.NotEqual(englishStateKey, frenchStateKey);
        administrator.DefaultRequestHeaders.Host = "en.website.test";

        // The nested site's theme changes its shell, while native Public Access controls its branch.
        var applicationPage = await Page(anonymous, "/private-tools/private-guide/");
        Assert.Contains("private-guide-marker", applicationPage.Html);
        Assert.Contains("<body class=\"app\">", applicationPage.Html);
        AssertAuthoredMenu(applicationPage.Html);
        var unprotectedReference = await Page(anonymous, "/private-reference/");
        var previousReferenceTag = unprotectedReference.Response.Headers.ETag;
        Assert.NotNull(previousReferenceTag);
        var members = services.GetRequiredService<IMemberService>();
#pragma warning disable CS0618
        var member = members.CreateMemberWithIdentity("website-member", "website-member@example.invalid", "Website member", "Member", isApproved: true);
        members.Save(member);
#pragma warning restore CS0618
        var memberManager = services.GetRequiredService<IMemberManager>();
        var identity = (await memberManager.FindByNameAsync(member.Username))!;
        const string password = "Local-test-only-WebsiteMember91!";
        Assert.True((await memberManager.AddPasswordAsync(identity, password)).Succeeded);
        var protection = new PublicAccessEntry(nestedSite, login, login, [new PublicAccessRule { RuleType = Constants.Conventions.PublicAccess.MemberUsernameRuleType, RuleValue = member.Username }]);
        var access = services.GetRequiredService<IPublicAccessService>();
        Assert.True(access.Save(protection).Success);
        var referenceProtection = new PublicAccessEntry(privateReference, login, deniedPage, [new PublicAccessRule { RuleType = Constants.Conventions.PublicAccess.MemberUsernameRuleType, RuleValue = member.Username }]);
        Assert.True(access.Save(referenceProtection).Success);
        // Public Access admits a backoffice user who may edit the page, including the administrator.
        var administratorResponse = await administrator.GetAsync("/private-tools/private-guide/");
        var administratorGuide = await administratorResponse.Content.ReadAsStringAsync();
        AssertNoStore(administratorResponse);
        if (administratorResponse.Headers.Location is { } administratorRedirect) administratorGuide = (await Page(administrator, administratorRedirect.ToString())).Html;
        Assert.Contains("private-guide-marker", administratorGuide);
        Assert.DoesNotContain("public-login-marker", administratorGuide);
        var administratorReference = (await Page(administrator, "/private-reference/")).Html;
        Assert.Contains("private-reference-marker", administratorReference);
        Assert.DoesNotContain("public-login-marker", administratorReference);
        var users = services.GetRequiredService<IUserService>();
        var groups = services.GetRequiredService<IUserGroupService>();
        var websiteEditor = users.CreateUserWithIdentity("website-editor", "website-editor@example.invalid");
        websiteEditor.Name = "Website editor";
        websiteEditor.IsApproved = true;
        websiteEditor.AddGroup((await groups.GetAsync(Constants.Security.EditorGroupKey))!.ToReadOnlyGroup());
        users.Save(websiteEditor);
        using var editor = Client(address, "en.website.test", useCookies: false);
        var editorSignIn = await editor.GetAsync($"/__tests/login/{websiteEditor.Key}?scheme=backoffice&name={Uri.EscapeDataString(AdministratorName)}");
        editorSignIn.EnsureSuccessStatusCode();
        editor.DefaultRequestHeaders.Add("Cookie", Cookies(editorSignIn));
        var editorResponse = await editor.GetAsync("/private-tools/private-guide/");
        var editorHtml = await editorResponse.Content.ReadAsStringAsync();
        if (editorResponse.Headers.Location is { } editorRedirect) editorHtml = (await Page(editor, editorRedirect.ToString())).Html;
        Assert.Contains("private-guide-marker", editorHtml);
        Assert.DoesNotContain("public-login-marker", editorHtml);
        anonymous.DefaultRequestHeaders.IfNoneMatch.Add(previousReferenceTag);
        var referenceSignIn = await Page(anonymous, "/private-reference/");
        anonymous.DefaultRequestHeaders.IfNoneMatch.Clear();
        Assert.Contains("public-login-marker", referenceSignIn.Html);
        Assert.DoesNotContain("private-reference-marker", referenceSignIn.Html);
        AssertNoStore(referenceSignIn.Response);
        var protectedResponse = await anonymous.GetAsync("/private-tools/private-guide/");
        AssertNoStore(protectedResponse);
        var protectedHtml = await protectedResponse.Content.ReadAsStringAsync();
        if (protectedResponse.Headers.Location is { } redirect) protectedHtml = (await Page(anonymous, redirect.ToString())).Html;
        Assert.DoesNotContain("private-guide-marker", protectedHtml);
        Assert.Contains("public-login-marker", protectedHtml);
        Assert.Contains("loginModel.Username", protectedHtml);
        Assert.Contains("/private-tools/private-guide/", WebUtility.HtmlDecode(protectedHtml));
        Assert.DoesNotContain("app-signin-note", protectedHtml);
        Assert.Contains("public-overview-marker", (await Page(anonymous, "/overview/")).Html);
        var loginForm = ReadLogin(protectedHtml);
        loginForm.Fields["loginModel.Username"] = member.Username;
        loginForm.Fields["loginModel.Password"] = password;
        var memberSignIn = await anonymous.PostAsync(loginForm.Action, new FormUrlEncodedContent(loginForm.Fields));
        Assert.Equal(HttpStatusCode.Redirect, memberSignIn.StatusCode);
        Assert.Equal("/private-tools/private-guide/", memberSignIn.Headers.Location?.OriginalString);
        Assert.Contains("private-guide-marker", (await Page(anonymous, "/private-tools/private-guide/")).Html);

        // Static protected content in the website shell has no form/antiforgery token to disable caching.
        anonymous.DefaultRequestHeaders.IfNoneMatch.Add(previousReferenceTag);
        var memberReference = await Page(anonymous, "/private-reference/");
        Assert.Contains("private-reference-marker", memberReference.Html);
        AssertMenuTokens(memberReference.Html, "Website member", profile: true);
        Assert.Contains("id=\"site-header\"", memberReference.Html);
        AssertNoStore(memberReference.Response);
        referenceProtection.RemoveRule(referenceProtection.Rules.Single());
        referenceProtection.AddRule("another-member", Constants.Conventions.PublicAccess.MemberUsernameRuleType);
        Assert.True(access.Save(referenceProtection).Success);
        var deniedReference = await Page(anonymous, "/private-reference/");
        anonymous.DefaultRequestHeaders.IfNoneMatch.Clear();
        Assert.Contains("access-denied-marker", deniedReference.Html);
        Assert.DoesNotContain("private-reference-marker", deniedReference.Html);
        AssertNoStore(deniedReference.Response);
        // With a member session present, Umbraco's own decision for that member stands: a refused member is never
        // replaced by a concurrent backoffice session, so the host's editor rule is not even asked.
        using var both = Client(address, "en.website.test", useCookies: false);
        both.DefaultRequestHeaders.Add("Cookie", Cookies(memberSignIn) + "; " + Cookies(signIn));
        var bothReference = await Page(both, "/private-reference/");
        Assert.Contains("access-denied-marker", bothReference.Html);
        Assert.DoesNotContain("private-reference-marker", bothReference.Html);
        AssertNoStore(bothReference.Response);

        // An ancestor's edit refreshes a page's cache validators, while its visible date reports the page's own change.
        using var visitor = Client(address, "en.website.test");
        static string Modified(string html) => Regex.Match(html, "<meta name=\"dcterms.modified\" content=\"([^\"]+)\"").Groups[1].Value;
        var unchanged = await Page(visitor, "/overview/");
        site.SetValue("css", "/* ancestor-css-marker */ /* edited later */");
        Save(site);
        var inherited = await Page(visitor, "/overview/");
        Assert.NotEmpty(Modified(unchanged.Html));
        Assert.Equal(Modified(unchanged.Html), Modified(inherited.Html));
        Assert.NotEqual(unchanged.Response.Headers.ETag, inherited.Response.Headers.ETag);

        // The saved Report Page loads the report module with the page's CSP nonce, as the block runtime does.
        administrator.DefaultRequestHeaders.Host = address.Authority;
        var reportPage = await Page(administrator, "/orders/");
        administrator.DefaultRequestHeaders.Host = "en.website.test";
        Assert.Contains("/api/reports/ui/ir.js\" nonce=\"website-test-nonce\"", reportPage.Html);

        // A report's Print needs a Paper report node, and its row action a record key column the report has.
        var misprinted = WebPageTestContent.Interactive();
        var printing = WebPageTestContent.Blocks(misprinted, "appReport").Single();
        WebPageTestContent.SetValue(printing, "keyColumn", "NO_SUCH_COLUMN");
        WebPageTestContent.SetValue(printing, "paperReport", "umb://document/" + overview.Key.ToString("N"));
#pragma warning disable CS0618
        var refusedPage = contents.Create("Misprinted", site.Id, "webPage");
        refusedPage.SetCultureName("Misprinted", "en");
        refusedPage.SetCultureName("Misprinted", "fr");
        refusedPage.SetValue(ApplicationStarter.BlockPropertyAlias, misprinted.ToJsonString(), "en");
        refusedPage.SetValue(ApplicationStarter.BlockPropertyAlias, misprinted.ToJsonString(), "fr");
        Assert.True(contents.Save(refusedPage).Success);
        var printRefusal = contents.Publish(refusedPage, ["*"]);
#pragma warning restore CS0618
        Assert.False(printRefusal.Success);
        var printMessages = string.Join(" ", printRefusal.EventMessages?.GetAll().Select(message => message.Message) ?? []);
        Assert.Contains("NO_SUCH_COLUMN is not a column of the report", printMessages);
        Assert.Contains("Select a Paper report node", printMessages);

        // An unfinished translation must not prevent publishing the valid language.
        var invalidFrench = WebPageTestContent.Empty();
        WebPageTestContent.Layout(invalidFrench).Add(WebPageTestContent.Add(invalidFrench, "appText", ("field", "customer")));
        overview.SetValue(ApplicationStarter.BlockPropertyAlias, invalidFrench.ToJsonString(), "fr");
#pragma warning disable CS0618
        Assert.True(contents.Save(overview).Success);
        Assert.True(contents.Publish(overview, ["en"]).Success);
        Assert.False(contents.Publish(overview, ["fr"]).Success);
#pragma warning restore CS0618
    }

    private static HttpClient Client(Uri address, string host, bool useCookies = true)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = useCookies }) { BaseAddress = address };
        client.DefaultRequestHeaders.Host = host;
        return client;
    }
    // The cookies a response issues, as a request's Cookie header; a cleared cookie has no value to carry.
    private static string Cookies(HttpResponseMessage response) => string.Join("; ", response.Headers.GetValues("Set-Cookie")
        .Select(cookie => cookie.Split(';')[0]).Where(cookie => cookie.Split('=', 2) is [_, { Length: > 0 }]));
    private async Task<(HttpResponseMessage Response, string Html)> Page(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        var name = Regex.Replace(client.DefaultRequestHeaders.Host + path, "[^a-zA-Z0-9.-]", "-");
        await System.IO.File.WriteAllTextAsync(Path.Combine(responses, $"{++responseNumber:D2}-{name}.html"), html);
        Assert.True(response.IsSuccessStatusCode, $"GET {path} returned {response.StatusCode}: {html}");
        Assert.DoesNotContain("Could not render", html);
        return (response, html);
    }
    private static async Task<JsonElement> Json(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static (string Action, Dictionary<string, string> Fields) ReadLogin(string html)
    {
        var form = Regex.Matches(html, "<form\\b[^>]*>.*?</form>", RegexOptions.Singleline).Cast<Match>()
            .Single(match => match.Value.Contains("class=\"app-login-form\"", StringComparison.Ordinal)).Value;
        var fields = new Dictionary<string, string>();
        foreach (Match input in Regex.Matches(form, "<input\\b[^>]*>"))
        {
            var name = Regex.Match(input.Value, "\\bname=\"([^\"]*)\"");
            if (name.Success) fields[WebUtility.HtmlDecode(name.Groups[1].Value)] = WebUtility.HtmlDecode(Regex.Match(input.Value, "\\bvalue=\"([^\"]*)\"").Groups[1].Value);
        }
        return (WebUtility.HtmlDecode(Regex.Match(form, "\\baction=\"([^\"]*)\"").Groups[1].Value), fields);
    }
    private static void AssertAuthoredMenu(string html)
    {
        var title = Regex.Match(html, "<a(?<attributes>[^>]*)>Website resources</a>");
        Assert.True(title.Success, "The authored navigation group was not rendered.");
        Assert.DoesNotContain("href=", title.Groups["attributes"].Value);
        Assert.Matches("<a[^>]*href=\"(?:https?://en\\.website\\.test)?/overview/\"[^>]*>Overview link</a>", html);
    }
    /// <summary>Menu tokens follow the visitor: nothing for an anonymous visitor, no member profile for a backoffice Administrator, everything for a member.</summary>
    private static void AssertMenuTokens(string html, string? signedInAs, bool profile)
    {
        var signOut = Regex.Match(html, "<form method=\"post\" action=\"/application/account/logout\">.*?<button[^>]*>Sign out</button></form>", RegexOptions.Singleline);
        if (signedInAs is null)
        {
            Assert.DoesNotContain("Signed in as", html);
            Assert.False(signOut.Success, "An anonymous visitor was offered sign-out.");
        }
        else
        {
            Assert.Contains("<a>Signed in as " + signedInAs + "</a>", html);
            Assert.True(signOut.Success, "The sign-out entry was not rendered as a form.");
            Assert.Contains("__RequestVerificationToken", signOut.Value);
            Assert.Contains("name=\"returnUrl\"", signOut.Value);
        }
        Assert.Equal(profile, Regex.IsMatch(html, "<a[^>]*href=\"(?:https?://en\\.website\\.test)?/overview/\"[^>]*>My profile</a>"));
        Assert.DoesNotContain("{{user.", html);
    }
    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
    }
    private sealed class RenderedTemplateHeader : IResultFilter
    {
        public void OnResultExecuting(ResultExecutingContext context)
        {
            if (context.Result is ViewResult { ViewName: { } name }) context.HttpContext.Response.Headers["X-Test-Template"] = name;
        }
        public void OnResultExecuted(ResultExecutedContext context) { }
    }
}
