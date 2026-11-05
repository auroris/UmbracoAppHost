using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace UmbracoAppHost.Testing;

/// <summary>
/// Drives <c>/api/forms</c> as the browser runtime does: the GET's antiforgery token goes on every write
/// as the <c>RequestVerificationToken</c> header, page parameters are passed as a query string.
/// </summary>
/// <remarks>
/// Writes carry the definition version and the antiforgery token of the last successful load, so load
/// before the first write and again whenever a browser would reload the page.
/// </remarks>
public sealed class FormsClient(HttpClient client, string pathBase, Guid pageKey, Guid formKey)
{
    private readonly string api = $"{pathBase}/api/forms/{pageKey}/{formKey}";
    private string? antiforgeryToken, definitionVersion;

    /// <summary>The query parameter that names the record to load, as the form block's Key parameter does.</summary>
    public string KeyParameter { get; init; } = "id";

    /// <summary>Loads the record named by <paramref name="key"/>, or initializes a new one without it.</summary>
    public async Task<FormResponse> LoadAsync(string? key = null, string? query = null)
    {
        var url = Http.WithQuery(api, query);
        if (key is not null) url = QueryHelpers.AddQueryString(url, KeyParameter, key);
        var loaded = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
        if (loaded.IsSuccess) (antiforgeryToken, definitionVersion) = (Http.Text(loaded.Body, "antiforgeryToken"), loaded.DefinitionVersion);
        return loaded;
    }

    /// <summary>Creates a record; a new submission key is generated unless one is given to repeat a submission.</summary>
    public Task<FormResponse> CreateAsync(object values, string? query = null, string? submissionKey = null)
        => WriteAsync(HttpMethod.Post, api, query, values, null, submissionKey);

    public Task<FormResponse> UpdateAsync(string key, string? version, object values, string? query = null)
        => WriteAsync(HttpMethod.Put, Record(key), query, values, version, null);

    /// <summary>Deletes a record with the JSON body the endpoint reads, as the runtime's Delete button sends it.</summary>
    public async Task<HttpResponseMessage> DeleteAsync(string key, string? version, string? query = null)
        => (await WriteAsync(HttpMethod.Delete, Record(key), query, new { }, version, null)).Response;

    private string Record(string key) => $"{api}/{Uri.EscapeDataString(key)}";

    private Task<FormResponse> WriteAsync(HttpMethod method, string path, string? query, object values, string? version, string? submissionKey)
    {
        if (definitionVersion is null) throw new TestSiteException($"Load the form at {api} before writing to it: a write carries the definition version and antiforgery token the load returns.");
        var request = new HttpRequestMessage(method, Http.WithQuery(path, query))
        {
            Content = JsonContent.Create(new { values, definitionVersion, version, submissionKey = submissionKey ?? Guid.NewGuid().ToString() }),
        };
        request.Headers.Add("RequestVerificationToken", antiforgeryToken);
        return SendAsync(request);
    }

    private async Task<FormResponse> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            var response = await client.SendAsync(request);
            return new FormResponse(response, await Http.ReadJsonAsync(response));
        }
    }
}

/// <summary>A form endpoint's answer: the response and its parsed JSON body, an empty object when there is none.</summary>
public sealed record FormResponse(HttpResponseMessage Response, JsonElement Body)
{
    public bool IsSuccess => Response.IsSuccessStatusCode;
    public string? RecordKey => Http.Text(Record, "key");
    public string? RecordVersion => Http.Text(Record, "version");
    public string? DefinitionVersion => Http.Text(Body, "definitionVersion");

    /// <summary>The record's values, or an empty object.</summary>
    public JsonElement Values => Http.Object(Record, "values");

    /// <summary>The validation errors by field, or an empty object.</summary>
    public JsonElement Errors => Http.Object(Body, "errors");

    private JsonElement Record => Http.Object(Body, "record");
}
