using System.Text.Json;

namespace UmbracoAppHost.Testing;

internal static class Http
{
    /// <summary>An empty JSON object, which stands in for a body or a property that is not there.</summary>
    public static readonly JsonElement Empty = Parse("{}");

    /// <summary>The path with a query string appended; the query may be given with or without its leading <c>?</c>.</summary>
    public static string WithQuery(string path, string? query)
        => string.IsNullOrEmpty(query) ? path : path + (path.Contains('?') ? "&" : "?") + query.TrimStart('?', '&');

    /// <summary>The response's JSON body, or an empty object when it has none.</summary>
    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        try { return Parse(text); }
        catch (JsonException)
        {
            throw new TestSiteException($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {(int)response.StatusCode} {response.StatusCode} with a body that is not JSON: {text}");
        }
    }

    public static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        } : null;

    public static JsonElement Object(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? value : Empty;

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
