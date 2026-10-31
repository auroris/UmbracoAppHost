using System.Text.Json.Nodes;
using System.Xml.Linq;
using AppBlocks.Blocks;

namespace UmbracoAppHost.Tests;

/// <summary>Real starter form/report definitions wrapped in website layouts, with independent block identities.</summary>
internal static class WebPageTestContent
{
    internal static JsonNode Empty() => JsonNode.Parse("""{"contentData":[],"settingsData":[],"expose":[],"layout":{"Umbraco.BlockGrid":[]}}""")!;
    internal static JsonArray Layout(JsonNode content) => content["layout"]!["Umbraco.BlockGrid"]!.AsArray();
    internal static IEnumerable<JsonNode> Blocks(JsonNode content, string alias) => content["contentData"]!.AsArray().Select(block => block!)
        .Where(block => block["contentTypeKey"]!.GetValue<string>() == AppBlockSchema.Key(alias).ToString());
    internal static JsonNode? Value(JsonNode block, string alias) => block["values"]!.AsArray().FirstOrDefault(value => value!["alias"]!.GetValue<string>() == alias)?["value"];
    internal static void SetValue(JsonNode block, string alias, string value)
    {
        var property = block["values"]!.AsArray().FirstOrDefault(item => item!["alias"]!.GetValue<string>() == alias);
        if (property is not null) property["value"] = value;
        else block["values"]!.AsArray().Add(new JsonObject { ["alias"] = alias, ["value"] = value, ["culture"] = null, ["segment"] = null });
    }

    internal static JsonNode Static(string marker)
    {
        var content = Empty();
        Layout(content).Add(Wrap(content, AppBlockSchema.RegionAlias, [Add(content, AppBlockSchema.HtmlAlias, ("html", "<p>" + marker + "</p>"))]));
        return content;
    }

    internal static JsonNode Interactive()
    {
        var content = Empty();
        var first = Merge(content, JsonNode.Parse(ApplicationStarter.FormContent())!)[0];
        var second = JsonNode.Parse(ApplicationStarter.FormContent())!;
        var text = second.ToJsonString();
        foreach (var key in second["contentData"]!.AsArray().Select(block => block!["key"]!.GetValue<string>())) text = text.Replace(key, Guid.NewGuid().ToString(), StringComparison.Ordinal);
        second = JsonNode.Parse(text)!;
        var configuration = Value(Blocks(second, "appForm").Single(), "formInterface")!["contentData"]![0]!;
        SetValue(configuration, "keyParameter", "otherOrder");
        var secondLayout = Merge(content, second)[0];
        var report = Merge(content, JsonNode.Parse(ApplicationStarter.ReportContent())!)[0];
        var publicContent = Add(content, AppBlockSchema.HtmlAlias, ("html", "<p>public-introduction-marker</p>"));
        var region = Wrap(content, AppBlockSchema.RegionAlias, [first, report]);
        Layout(content).Add(Wrap(content, "appColumns", [publicContent], [region, secondLayout]));
        return content;
    }

    internal static JsonNode[] Merge(JsonNode into, JsonNode fragment)
    {
        foreach (var name in new[] { "contentData", "settingsData", "expose" })
            foreach (var item in fragment[name]!.AsArray()) into[name]!.AsArray().Add(item!.DeepClone());
        return Layout(fragment).Select(item => item!.DeepClone()).ToArray();
    }

    internal static JsonNode Add(JsonNode content, string alias, params (string Alias, string Value)[] values)
    {
        var key = Guid.NewGuid().ToString();
        var block = new JsonObject { ["key"] = key, ["contentTypeKey"] = AppBlockSchema.Key(alias).ToString(), ["values"] = new JsonArray() };
        foreach (var value in values) SetValue(block, value.Alias, value.Value);
        content["contentData"]!.AsArray().Add(block);
        content["expose"]!.AsArray().Add(new JsonObject { ["contentKey"] = key, ["culture"] = null, ["segment"] = null });
        return new JsonObject { ["contentKey"] = key, ["columnSpan"] = 12, ["rowSpan"] = 1, ["areas"] = new JsonArray(), ["settingsKey"] = null };
    }

    internal static JsonNode Wrap(JsonNode content, string alias, params JsonNode[][] children)
    {
        var layout = Add(content, alias);
        var configuration = JsonNode.Parse(XElement.Load(Path.Combine(AppContext.BaseDirectory, "uSync", "DataTypes", "ApplicationApplicationBlocks.config")).Element("Config")!.Value)!;
        var block = configuration["blocks"]!.AsArray().Single(item => item!["contentElementTypeKey"]!.GetValue<string>() == AppBlockSchema.Key(alias).ToString())!;
        foreach (var (area, index) in block["areas"]!.AsArray().Select((area, index) => (area: area!, index)))
        {
            var items = new JsonArray();
            if (index < children.Length) foreach (var item in children[index]) items.Add(item);
            layout["areas"]!.AsArray().Add(new JsonObject { ["key"] = area["key"]!.DeepClone(), ["items"] = items });
        }
        return layout;
    }
}
