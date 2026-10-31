using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Authentication;
using AppBlocks.Blocks;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The content blocks and field properties end to end. First boot imports and publishes the saved export's
/// Content blocks page; a copy beneath a website root and a page whose fields default to every bind variable
/// are published through the same validation; a member linked to a Windows SID reads them over HTTP through a
/// site that sets a Content Security Policy nonce. The markup is checked where each block puts it: rich text
/// and HTML in place, CSS once in the head, JavaScript at the end of the body, hidden fields as hidden inputs
/// without a grid cell, widths as grid spans, defaults resolved for the member and the address. A hidden
/// field is then submitted through the form endpoint like any other.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class ContentBlocksHttpTests
{
    private const string Sid = "S-1-5-21-3623811015-3361044348-30300820-1013";
    private const string Nonce = "r4nd0m-n0nce";
    private const string NonceHeader = "X-Test-Csp-Nonce";

    [Fact]
    [ResetPublishedContentServices]
    public async Task Content_blocks_and_field_properties_render_in_both_shells_and_hidden_fields_submit()
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            // Stands in for a site's Content Security Policy middleware, which keeps the request's nonce where the blocks read it.
            ConfigureBuilder = builder => FirstMiddleware.Add(builder, async (context, next) =>
            {
                if (context.Request.Headers.TryGetValue(NonceHeader, out var nonce)) context.Items[AppPageAssets.CspNonceItem] = nonce.ToString();
                await next(context);
            }),
        });
        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        for (var i = 0; i < 100 && types.Get(AppBlockSchema.PageAlias)?.AllowedTemplates?.Any() != true; i++) await Task.Delay(100);
        var contents = services.GetRequiredService<IContentService>();

        // First boot imported the demo page, and publishing it passed the block validation.
        var demo = contents.GetById(AppBlockSchema.Key("sample:content-blocks"));
        Assert.NotNull(demo);
        Assert.True(demo.Published);

#pragma warning disable CS0618
        // A member linked to a Windows SID, as an account created from the directory is.
        var member = site.CreateMember("content-test", "content-test@example.invalid", "Content test");
        await services.GetRequiredService<AccountProvisioning>().LinkAsync(member.Key, Sid, ["UmbracoMembers.Domain"]);

        // The demo page's blocks again, with every field defaulting to one of the bind variables.
        var template = types.Get(AppBlockSchema.PageAlias)!.DefaultTemplate?.Id;
        var variables = contents.Create("Bind variables", demo.ParentId, AppBlockSchema.PageAlias);
        variables.TemplateId = template;
        variables.SetValue(AppBlockSchema.BodyAlias, WithDefaults(ContentBlocksTests.DemoContent(), new Dictionary<string, string>
        {
            ["customer"] = ":USER_NAME", ["notes"] = ":USER_EMAIL", ["status"] = ":USER_SID",
            ["amount"] = ":USER_KEY", ["orderDate"] = ":NOW", ["region"] = ":param.region",
        }));
        Assert.True(contents.Save(variables).Success);
        var published = contents.Publish(variables, ["*"]);
        Assert.True(published.Success, string.Join(" ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));

        // And the demo page beneath a website root, for the website shell.
        var website = contents.Create("Content website", Constants.System.Root, "appHostWebsite");
        website.SetCultureName("Content website", "en");
        website.TemplateId = types.Get("appHostWebsite")!.DefaultTemplate?.Id;
        Assert.True(contents.Save(website).Success);
        Assert.True(contents.Publish(website, ["*"]).Success);
        var copy = contents.Create("Content blocks in the website", website.Id, AppBlockSchema.PageAlias);
        copy.TemplateId = template;
        copy.SetValue(AppBlockSchema.BodyAlias, ContentBlocksTests.DemoContent());
        Assert.True(contents.Save(copy).Success);
        published = contents.Publish(copy, ["*"]);
        Assert.True(published.Success, string.Join(" ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
#pragma warning restore CS0618

        using var client = await site.SignInMemberAsync(member);

        // The application shell, for a site that sets a nonce, with a parameter for :PARAM.note.
        var html = await Page(client, "/content-blocks/?note=Rush", Nonce);
        var (head, body) = Split(html);
        Assert.Contains("<body class=\"app\">", html);
        // Rich text and raw HTML render where they sit, inside the form.
        Assert.Contains("<div class=\"ah-rich-text\"><p>Content blocks sit in a form beside its fields.", body);
        Assert.Equal(2, Count(body, "<div class=\"content-demo-callout\">"));
        // CSS: once, in the head, though both regions carry it; after the shell's and the runtime's stylesheets; with the nonce.
        Assert.Equal(1, Count(html, ".content-demo-callout {"));
        Assert.Matches($"<style nonce=\"{Nonce}\">\\s*\\.content-demo-callout \\{{", head);
        Assert.Contains($"<link href=\"/App_Plugins/ApplicationBlocks/runtime.css\" nonce=\"{Nonce}\" rel=\"stylesheet\" />", head);
        Assert.True(head.IndexOf("/dist/css/app.css", StringComparison.Ordinal) < head.IndexOf("runtime.css", StringComparison.Ordinal)
            && head.IndexOf("runtime.css", StringComparison.Ordinal) < head.IndexOf("<style", StringComparison.Ordinal), head);
        // JavaScript: at the end of the body, after the shell's own script, with the nonce, as is the runtime's.
        Assert.Matches($"<script nonce=\"{Nonce}\">\\s*document\\.querySelectorAll\\(\"\\[data-content-demo-clock\\]\"\\)[^<]*</script>\\s*</body>", body);
        Assert.True(body.IndexOf("/dist/scripts/app.js", StringComparison.Ordinal) < body.IndexOf($"<script nonce=\"{Nonce}\">", StringComparison.Ordinal));
        Assert.Contains($"src=\"/App_Plugins/ApplicationBlocks/runtime.js\" nonce=\"{Nonce}\"", body);
        // CSS and JavaScript blocks and the hidden field take no cell of the grid.
        Assert.DoesNotContain("data-content-element-type-alias=\"appCss\"", body);
        Assert.DoesNotContain("data-content-element-type-alias=\"appScript\"", body);
        Assert.Equal(1, Count(body, "data-content-element-type-alias=\"appSelect\""));
        // The hidden field is a hidden input bound like any other, without a label.
        Assert.Matches("<input type=\"hidden\" id=\"field-[0-9a-f]{32}\" name=\"status\" data-field=\"status\" data-kind=\"text\" data-readonly=\"false\"\\s+data-label=\"Status\"\\s*/>", body);
        Assert.DoesNotMatch("<label for=\"field-[0-9a-f]{32}\">Status", body);
        // Defaults resolved for the member and the address; a field without one carries none.
        Assert.Matches(Field("customer") + "[^>]*data-default=\"content-test\"", body);
        Assert.Matches(Field("notes") + "[^>]*data-default=\"Rush\"", body);
        Assert.DoesNotMatch(Field("amount") + "[^>]*data-default", body);
        // Widths: each field's grid span, with a hook naming the width.
        Assert.Equal(new[] { "appText:6:6:6", "appSelect:3:3:3", "appNumber:3:3:3", "appDate:4:4:4", "appTextArea:8:8:8" },
            Regex.Matches(body, "class=\"ah-grid__item ah-grid__item--width-(\\d+) umb-block-grid__layout-item\" data-content-element-type-alias=\"(\\w+)\"\\s+data-col-span=\"(\\d+)\"\\s+data-row-span=\"1\"\\s+style=\"--umb-block-grid--item-column-span: (\\d+);")
                .Select(m => $"{m.Groups[2].Value}:{m.Groups[1].Value}:{m.Groups[3].Value}:{m.Groups[4].Value}").ToArray());
        // Placeholders: on inputs and text areas, and as a select's empty choice.
        Assert.Matches(Field("customer") + "[^>]*placeholder=\"Who is ordering\"", body);
        Assert.Matches(Field("notes") + "[^>]*placeholder=\"Anything the warehouse should know\"", body);
        Assert.Contains("<option value=\"\">Choose a region</option>", body);

        // Without a nonce nothing carries one; without the parameter Notes has no default.
        html = await Page(client, "/content-blocks/");
        Assert.DoesNotContain("nonce=", html);
        Assert.Matches("<script>\\s*document\\.querySelectorAll", html);
        Assert.DoesNotMatch(Field("notes") + "[^>]*data-default", html);

        // Every variable for this member: username, email, SID, the application's key, today, and a parameter in any case.
        html = await Page(client, "/bind-variables/?region=EAST");
        Assert.Matches(Field("customer") + "[^>]*data-default=\"content-test\"", html);
        Assert.Matches(Field("notes") + "[^>]*data-default=\"content-test@example\\.invalid\"", html);
        Assert.Matches(Field("status") + $"[^>]*data-default=\"{Sid}\"", html);
        Assert.Matches(Field("amount") + $"[^>]*data-default=\"{member.Id}\"", html);
        Assert.Matches(Field("region") + "[^>]*data-default=\"EAST\"", html);
        var today = Regex.Match(html, Field("orderDate") + "[^>]*data-default=\"(\\d{4}-\\d{2}-\\d{2})\"").Groups[1].Value;
        Assert.Contains(today, new[] { DateTime.Today.AddDays(-1), DateTime.Today }.Select(day => day.ToString("yyyy-MM-dd")));

        // The website shell writes the same blocks into its own head and body.
        html = await Page(client, "/content-blocks-in-the-website/", Nonce);
        (head, body) = Split(html);
        Assert.Contains("id=\"site-header\"", html);
        Assert.Contains("/dist/css/site.css", head);
        Assert.Equal(1, Count(html, ".content-demo-callout {"));
        Assert.Matches($"<style nonce=\"{Nonce}\">\\s*\\.content-demo-callout \\{{", head);
        Assert.Matches($"<script nonce=\"{Nonce}\">\\s*document\\.querySelectorAll[^<]*</script>\\s*</body>", body);
        Assert.True(body.IndexOf("/dist/scripts/site.js", StringComparison.Ordinal) < body.IndexOf($"<script nonce=\"{Nonce}\">", StringComparison.Ordinal));
        Assert.Contains("<div class=\"ah-rich-text\"><p>Content blocks sit in a form", body);
        Assert.Matches("<input type=\"hidden\" [^>]*name=\"status\"", body);

        // Each shell's stylesheet styles the rich text and the placeholders.
        foreach (var stylesheet in new[] { "/dist/css/app.css", "/dist/css/site.css" })
        {
            var css = await client.GetStringAsync(stylesheet);
            Assert.Contains(".ah-rich-text", css);
            Assert.Contains(".app-form-field ::placeholder", css);
        }

        // A hidden field is bound and submitted like any other: a new order sets the hidden status.
        var forms = site.Forms(client, demo.Key, AppBlockSchema.Key("sample:content-blocks-form"));
        var initialized = await forms.LoadAsync();
        Assert.True(initialized.IsSuccess, initialized.Body.GetRawText());
        Assert.Equal("NEW", initialized.Values.GetProperty("status").GetString());
        var created = await forms.CreateAsync(new { customer = "Hidden field test", region = "EAST", status = "PENDING", amount = "3.50", orderDate = "2026-10-23", notes = (string?)null });
        Assert.True(created.IsSuccess, created.Body.GetRawText());
        Assert.Equal("PENDING", created.Values.GetProperty("status").GetString());
        var reloaded = await forms.LoadAsync(created.RecordKey);
        Assert.Equal("PENDING", reloaded.Values.GetProperty("status").GetString());
    }

    private static async Task<string> Page(HttpClient client, string path, string? nonce = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (nonce is not null) request.Headers.Add(NonceHeader, nonce);
        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, html);
        Assert.DoesNotContain("Could not render", html);
        Assert.DoesNotContain("invalid resource", html);
        return html;
    }

    private static (string Head, string Body) Split(string html)
    {
        var end = html.IndexOf("</head>", StringComparison.Ordinal);
        Assert.True(end > 0, html);
        return (html[..end], html[end..]);
    }

    /// <summary>The start of a field's control, whichever element it is; its attributes follow up to the tag's end.</summary>
    private static string Field(string name) => $"<(?:input|textarea|select)\\b[^>]*\\bname=\"{name}\"";

    private static int Count(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;

    /// <summary>The page's blocks with the default value of each named field replaced.</summary>
    private static string WithDefaults(string json, IReadOnlyDictionary<string, string> defaults)
    {
        var page = JsonNode.Parse(json)!;
        foreach (var block in page["contentData"]!.AsArray())
        {
            var values = block!["values"]!.AsArray();
            var field = values.FirstOrDefault(v => v!["alias"]!.GetValue<string>() == "field")?["value"]?.GetValue<string>();
            if (field is null || !defaults.TryGetValue(field, out var value)) continue;
            foreach (var existing in values.Where(v => v!["alias"]!.GetValue<string>() == AppBlockField.DefaultValueAlias).ToList()) values.Remove(existing);
            values.Add(new JsonObject { ["alias"] = AppBlockField.DefaultValueAlias, ["culture"] = null, ["editorAlias"] = null, ["segment"] = null, ["value"] = value });
        }
        return page.ToJsonString();
    }
}
