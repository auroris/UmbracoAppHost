using System.Xml.Linq;

namespace UmbracoAppHost.Testing;

/// <summary>Reads a saved uSync export without a running site.</summary>
public static class USyncExport
{
    /// <summary>Every content node of the export, from <c>Content/*.config</c> under <paramref name="folder"/>.</summary>
    public static IReadOnlyList<USyncContentItem> Content(string folder)
    {
        var content = Path.Combine(folder, "Content");
        if (!Directory.Exists(content)) throw new TestSiteException($"{content} does not exist, so {folder} is not a uSync export.");
        return [.. Directory.EnumerateFiles(content, "*.config").Order(StringComparer.Ordinal).Select(Read)];
    }

    /// <summary>The one content node whose <c>Alias</c> attribute, the node name the export was made with, is <paramref name="alias"/>.</summary>
    public static USyncContentItem ContentByAlias(string folder, string alias)
        => Single(Content(folder).Where(item => item.Alias == alias).ToList(), $"with the alias '{alias}'", folder);

    public static USyncContentItem ContentByKey(string folder, Guid key)
        => Single(Content(folder).Where(item => item.Key == key).ToList(), $"with the key {key}", folder);

    private static USyncContentItem Single(List<USyncContentItem> matches, string description, string folder) => matches switch
    {
        [var item] => item,
        [] => throw new TestSiteException($"The uSync export in {folder} has no content node {description}."),
        _ => throw new TestSiteException($"The uSync export in {folder} has {matches.Count} content nodes {description}: {string.Join(", ", matches.Select(item => item.File))}."),
    };

    private static USyncContentItem Read(string file)
    {
        var node = XElement.Load(file);
        var info = node.Element("Info");
        return new USyncContentItem(
            Guid.Parse(node.Attribute("Key")?.Value ?? throw new TestSiteException($"{file} has no Key attribute.")),
            node.Attribute("Alias")?.Value ?? "",
            info?.Element("NodeName")?.Attribute("Default")?.Value ?? node.Attribute("Alias")?.Value ?? "",
            info?.Element("ContentType")?.Value ?? "",
            Guid.TryParse(info?.Element("Parent")?.Attribute("Key")?.Value, out var parent) ? parent : Guid.Empty,
            info?.Element("Path")?.Value ?? "",
            file);
    }
}

/// <summary>
/// A content node of a saved uSync export. <see cref="Path"/> is the export's path of node names, such
/// as <c>/SampleApplication/OrdersForm</c>; <see cref="File"/> is the <c>.config</c> file it was read from.
/// </summary>
public sealed record USyncContentItem(Guid Key, string Alias, string Name, string ContentType, Guid ParentKey, string Path, string File)
{
    /// <summary>The property's saved value, CDATA included, or null when the node does not have the property.</summary>
    public string? Property(string alias) => XElement.Load(File).Element("Properties")?.Element(alias)?.Element("Value")?.Value;
}
