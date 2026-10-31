using System.Net.Http.Json;
using System.Text.Json;

namespace UmbracoAppHost.Testing;

/// <summary>One change of a batch: <c>create</c> with values, <c>update</c> with the key, the version and the changed values, or <c>delete</c> with the key and the version.</summary>
public sealed record TabularChange(string Operation, string? Key = null, string? Version = null, object? Values = null);

/// <summary>
/// Drives <c>/api/tabular</c> as the Tabular form block's runtime does: the list's antiforgery token goes on every
/// save as the <c>RequestVerificationToken</c> header, and the page's parameters, such as the record key the block
/// follows, travel as a query string on every call.
/// </summary>
/// <remarks>List before the first save and again whenever a browser would reload the page.</remarks>
public sealed class TabularClient(HttpClient client, string pathBase, Guid pageKey, Guid blockKey)
{
    private readonly string api = $"{pathBase}/api/tabular/{pageKey}/{blockKey}";
    private string? antiforgeryToken;

    /// <summary>The record's rows, the columns, what the caller may do, and the record a new row starts from.</summary>
    public async Task<TabularResponse> ListAsync(string? query = null)
    {
        var listed = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Http.WithQuery(api, query)));
        if (listed.IsSuccess) antiforgeryToken = Http.Text(listed.Body, "antiforgeryToken");
        return listed;
    }

    /// <summary>Saves the changes as one batch; the answer is the list afterwards, or the failing change's index and errors.</summary>
    public Task<TabularResponse> SaveAsync(IEnumerable<TabularChange> changes, string? query = null, string? submissionKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Http.WithQuery(api, query))
        {
            Content = JsonContent.Create(new {
                submissionKey = submissionKey ?? Guid.NewGuid().ToString(),
                changes = changes.Select(change => new { operation = change.Operation, key = change.Key, version = change.Version, values = change.Values }).ToArray(),
            }),
        };
        request.Headers.Add("RequestVerificationToken", antiforgeryToken
            ?? throw new TestSiteException($"List the rows at {api} before saving them: a save carries the antiforgery token the list returns."));
        return SendAsync(request);
    }

    private async Task<TabularResponse> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            var response = await client.SendAsync(request);
            return new TabularResponse(response, await Http.ReadJsonAsync(response));
        }
    }
}

/// <summary>A tabular endpoint's answer: the response and its parsed JSON body, an empty object when there is none.</summary>
public sealed record TabularResponse(HttpResponseMessage Response, JsonElement Body)
{
    public bool IsSuccess => Response.IsSuccessStatusCode;

    /// <summary>The listed rows in key order, or none.</summary>
    public IReadOnlyList<JsonElement> Rows
        => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().ToArray() : [];

    /// <summary>The columns the block shows, each with its key, label, kind and rules.</summary>
    public IReadOnlyList<JsonElement> Fields
        => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array ? fields.EnumerateArray().ToArray() : [];

    public bool CanAdd => Flag("canAdd");
    public bool CanUpdate => Flag("canUpdate");
    public bool CanDelete => Flag("canDelete");

    /// <summary>The position of the change a failed save refused, or null.</summary>
    public int? Index => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("index", out var index) && index.TryGetInt32(out var value) ? value : null;

    /// <summary>The field errors of a refused change, by field alias; an empty alias is the change as a whole.</summary>
    public IReadOnlyDictionary<string, string[]> Errors
        => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object
            ? errors.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToArray())
            : new Dictionary<string, string[]>();

    /// <summary>A refusal's explanation, from the problem response's detail or title.</summary>
    public string? Problem => Http.Text(Body, "detail") ?? Http.Text(Body, "title");

    private bool Flag(string name) => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

public static class TabularTestSiteExtensions
{
    /// <summary>A client for one Tabular form block, as <see cref="TestSite.Attachments"/> is for an Attachments block.</summary>
    public static TabularClient Tabular(this TestSite site, HttpClient client, Guid pageKey, Guid blockKey) => new(client, site.PathBase, pageKey, blockKey);
}
