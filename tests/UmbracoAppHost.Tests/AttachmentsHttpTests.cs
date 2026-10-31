using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The sample Orders form's Attachments block in the real host. What the block does, every operation, its limits and the purge, is
/// AppBlocks' to prove, in its own <c>AttachmentsHttpTests</c>; this is that the host's saved export and its sample class put it together.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class AttachmentsHttpTests
{
    /// <summary>The Attachments block the saved export places in the Orders form.</summary>
    private static readonly Guid OrdersAttachments = Guid.Parse("912e843e-ed06-46e1-a292-b576e6408788");

    [Fact]
    [ResetPublishedContentServices]
    public async Task The_sample_orders_files_block_follows_the_samples_records_and_class()
    {
        await using var site = await TestSite.StartAsync(new() { Application = typeof(Program).Assembly });
        await site.WaitForPublishedAsync(ApplicationStarter.FormPageKey);
        var member = site.CreateMember("files", "files@example.invalid", "Files member");
        using var client = await site.SignInMemberAsync(member);
        var files = site.Attachments(client, ApplicationStarter.FormPageKey, OrdersAttachments);

        // The sample's class refuses someone with no account; a record has to exist.
        using (var anonymous = site.CreateClient())
            Assert.Equal(HttpStatusCode.Forbidden, (await site.Attachments(anonymous, ApplicationStarter.FormPageKey, OrdersAttachments).ListAsync("id=4")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await files.ListAsync()).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await files.ListAsync("id=999999")).Response.StatusCode);

        // The export's purposes, and a file on a seeded order that another seeded order's block never sees.
        var list = await files.ListAsync("id=4");
        Assert.True(list.IsSuccess, list.Problem);
        Assert.True(list.CanWrite);
        Assert.Equal(["Invoice", "Delivery note", "Other"], list.Body.GetProperty("purposes").EnumerateArray().Select(p => p.GetString()!).ToArray());
        var uploaded = await files.UploadAsync(new("invoice.pdf", "application/pdf", Encoding.UTF8.GetBytes("first")), "Invoice", query: "id=4");
        Assert.Equal(HttpStatusCode.Created, uploaded.Response.StatusCode);
        Assert.Equal("Files member", uploaded.Body.GetProperty("uploadedBy").GetString());
        Assert.Equal("first", await (await files.DownloadAsync(uploaded.Id!.Value, "id=4")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await files.DownloadAsync(uploaded.Id!.Value, "id=5")).StatusCode);
    }
}
