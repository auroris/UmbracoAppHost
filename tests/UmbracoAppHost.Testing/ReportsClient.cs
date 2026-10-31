using System.Net.Http.Json;
using System.Text.Json;

namespace UmbracoAppHost.Testing;

/// <summary>
/// Reads reports through <c>/api/reports</c> and downloads them through <c>/api/download</c>, as the
/// report viewer does. A report block's name is <c>AppBlockContent.ReportName(page, block)</c>; a
/// published report definition's is its alias.
/// </summary>
public sealed class ReportsClient(HttpClient client, string pathBase)
{
    public Task<HttpResponseMessage> SchemaAsync(string report, string? query = null)
        => client.GetAsync(Http.WithQuery($"{pathBase}/api/reports/{Uri.EscapeDataString(report)}/schema", query));

    /// <summary>The rows of the report's default view, <c>result.rows</c>; a refusal throws with the response.</summary>
    public async Task<JsonElement> RowsAsync(string report, string? query = null)
    {
        using var response = await client.GetAsync(Http.WithQuery($"{pathBase}/api/reports/{Uri.EscapeDataString(report)}/default", query));
        if (!response.IsSuccessStatusCode)
            throw new TestSiteException($"Report {report} returned {(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var body = await Http.ReadJsonAsync(response);
        return Http.Object(body, "result") is var result && result.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows : throw new TestSiteException($"Report {report} answered without result.rows: {body.GetRawText()}");
    }

    /// <summary>Downloads the report in a format such as <c>csv</c>, for the query state a viewer holds, such as <c>result.document</c>.</summary>
    public Task<HttpResponseMessage> DownloadAsync(string report, string format, JsonElement document, string? query = null)
        => client.PostAsJsonAsync(Http.WithQuery($"{pathBase}/api/download/{Uri.EscapeDataString(report)}/{Uri.EscapeDataString(format)}", query), document);
}
