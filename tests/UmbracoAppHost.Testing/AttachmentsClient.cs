using System.Net.Http.Headers;
using System.Text.Json;

namespace UmbracoAppHost.Testing;

/// <summary>The file an upload or a replacement sends: its name, its content type and its bytes.</summary>
public sealed record AttachmentFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Drives <c>/api/attachments</c> as the Attachments block's runtime does: the list's antiforgery token goes on
/// every write as the <c>RequestVerificationToken</c> header, and the page's parameters, such as the record key
/// the block follows, travel as a query string on every call.
/// </summary>
/// <remarks>List before the first write and again whenever a browser would reload the page.</remarks>
public sealed class AttachmentsClient(HttpClient client, string pathBase, Guid pageKey, Guid blockKey)
{
    private readonly string api = $"{pathBase}/api/attachments/{pageKey}/{blockKey}";
    private string? antiforgeryToken;

    /// <summary>The record's files with their earlier revisions, whether the caller may write, and the block's purposes and limits.</summary>
    public async Task<AttachmentsResponse> ListAsync(string? query = null)
    {
        var listed = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Http.WithQuery(api, query)));
        if (listed.IsSuccess) antiforgeryToken = Http.Text(listed.Body, "antiforgeryToken");
        return listed;
    }

    /// <summary>Uploads a file; the answer is the new item (201).</summary>
    public Task<AttachmentsResponse> UploadAsync(AttachmentFile file, string? purpose = null, string? label = null, string? notes = null, string? query = null)
        => WriteAsync(HttpMethod.Post, api, query, file, purpose, label, notes);

    /// <summary>
    /// Replaces the current file with a new revision when <paramref name="file"/> is given, and changes the details
    /// given: null keeps a value, an empty string clears it. The answer is the item with its new current id.
    /// </summary>
    public Task<AttachmentsResponse> ReplaceAsync(long id, AttachmentFile? file = null, string? purpose = null, string? label = null, string? notes = null, string? query = null)
        => WriteAsync(HttpMethod.Put, Item(id), query, file, purpose, label, notes);

    /// <summary>Downloads the current file or an earlier revision; the response carries the stored name and type.</summary>
    public Task<HttpResponseMessage> DownloadAsync(long id, string? query = null) => client.GetAsync(Http.WithQuery(Item(id), query));

    /// <summary>Removes the item whose current file is <paramref name="id"/>; 204 when it was one of the record's files.</summary>
    public async Task<HttpResponseMessage> RemoveAsync(long id, string? query = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Http.WithQuery(Item(id), query));
        request.Headers.Add("RequestVerificationToken", Token());
        return await client.SendAsync(request);
    }

    private string Item(long id) => $"{api}/{id}";

    private string Token() => antiforgeryToken
        ?? throw new TestSiteException($"List the attachments at {api} before writing to them: a write carries the antiforgery token the list returns.");

    private Task<AttachmentsResponse> WriteAsync(HttpMethod method, string path, string? query, AttachmentFile? file, string? purpose, string? label, string? notes)
    {
        var body = new MultipartFormDataContent();
        if (file is not null)
        {
            var bytes = new ByteArrayContent(file.Content);
            bytes.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
            body.Add(bytes, "file", file.FileName);
        }
        foreach (var (name, value) in new[] { ("purpose", purpose), ("label", label), ("notes", notes) })
            if (value is not null) body.Add(new StringContent(value), name);
        var request = new HttpRequestMessage(method, Http.WithQuery(path, query)) { Content = body };
        request.Headers.Add("RequestVerificationToken", Token());
        return SendAsync(request);
    }

    private async Task<AttachmentsResponse> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            var response = await client.SendAsync(request);
            return new AttachmentsResponse(response, await Http.ReadJsonAsync(response));
        }
    }
}

/// <summary>An attachments endpoint's answer: the response and its parsed JSON body, an empty object when there is none.</summary>
public sealed record AttachmentsResponse(HttpResponseMessage Response, JsonElement Body)
{
    public bool IsSuccess => Response.IsSuccessStatusCode;

    /// <summary>The listed items, newest first, or none.</summary>
    public IReadOnlyList<JsonElement> Items
        => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : [];

    public bool CanWrite => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("canWrite", out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The id of the item an upload or a replacement answered with.</summary>
    public long? Id => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : null;

    /// <summary>A refusal's explanation, from the problem response's detail or title.</summary>
    public string? Problem => Http.Text(Body, "detail") ?? Http.Text(Body, "title");
}
