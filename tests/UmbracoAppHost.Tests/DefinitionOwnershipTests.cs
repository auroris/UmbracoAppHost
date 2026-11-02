using System.Text.Json.Nodes;
using System.Xml.Linq;
using AppBlocks;
using AppBlocks.DataSources;
using AppBlocks.Documents;
using AppBlocks.InteractiveReports;
using AppBlocks.Setup;
using UmbracoAppHost.Hosting;

namespace UmbracoAppHost.Tests;

/// <summary>
/// AppBlocks and its companions own the definitions in their schemas; the saved export owns the rest. These read the
/// files only, so a definition copied back into the export, or one a schema drops, fails here without a running site.
/// </summary>
public sealed class DefinitionOwnershipTests
{
    private static readonly string Export = Path.Combine(AppContext.BaseDirectory, "uSync");

    private static readonly AppBlocksSchema[] Schemas =
        [new(typeof(AppBlocksExtensions).Assembly), new(typeof(InteractiveReportsExtensions).Assembly), new(typeof(AppBlocksDocumentsExtensions).Assembly)];

    /// <summary>The kinds a schema can ship; the file name in the export is what a stale copy in a working folder would have.</summary>
    private static readonly string[] Kinds = ["ContentTypes", "DataTypes"];

    private static IEnumerable<XElement> SchemaDefinitions(string kind) => Schemas.SelectMany(schema => schema.Definitions(kind));

    private static IEnumerable<(string Path, XElement Definition)> ExportDefinitions(string kind) => Directory.EnumerateFiles(Path.Combine(Export, kind), "*.config")
        .Select(file => ($"{kind}/{Path.GetFileName(file)}", XElement.Load(file)));

    private static Guid Key(XElement definition) => Guid.Parse(definition.Attribute("Key")!.Value);
    private static string Alias(XElement definition) => definition.Attribute("Alias")!.Value;
    private static string Text(XElement element, string name) => element.Element(name)?.Value.Trim() ?? "";
    private static IEnumerable<XElement> Properties(XElement type) => type.Element("GenericProperties")!.Elements("GenericProperty");
    private static XElement SchemaType(string alias) => SchemaDefinitions("ContentTypes").Single(type => Alias(type) == alias);

    [Fact]
    public void The_export_carries_no_definition_a_schema_ships_except_the_ones_the_host_owns()
    {
        var copies = new List<string>();
        foreach (var kind in Kinds)
        {
            var shipped = SchemaDefinitions(kind).ToArray();
            foreach (var (path, definition) in ExportDefinitions(kind))
            {
                if (AppBlocksDefinitions.HostOwned.ContainsKey(Key(definition))) continue;
                if (shipped.Any(schema => Key(schema) == Key(definition) || string.Equals(Alias(schema), Alias(definition), StringComparison.OrdinalIgnoreCase)))
                    copies.Add(path);
            }
        }
        Assert.True(copies.Count == 0, "The export must not carry these, because AppBlocks installs them; change them in its Schema folder: " + string.Join(", ", copies));
    }

    [Fact]
    public void Every_definition_the_host_keeps_is_still_a_real_overlap_at_the_path_it_names()
    {
        foreach (var (key, path) in AppBlocksDefinitions.HostOwned)
        {
            Assert.Equal(key, Key(XElement.Load(Path.Combine(Export, path))));
            Assert.Contains(key, SchemaDefinitions(path.Split('/')[0]).Select(Key));
        }
    }

    [Theory]
    [InlineData("ApplicationApplicationBlocks.config")]
    [InlineData("WebsiteBlocks.config")]
    public void The_host_grids_offer_every_schema_block_with_its_field_settings(string file)
    {
        var key = Guid.Parse("97b36051-4abe-e750-6667-9cb7ee6a9a99");
        JsonNode Config(XElement definition) => JsonNode.Parse(definition.Element("Config")!.Value)!;
        var schema = Config(SchemaDefinitions("DataTypes").Single(definition => Key(definition) == key));
        var host = Config(XElement.Load(Path.Combine(Export, "DataTypes", file)));
        JsonNode Block(JsonNode config, string element) => config["blocks"]!.AsArray().Single(b => b!["contentElementTypeKey"]!.GetValue<string>() == element)!;

        Assert.Equal(schema["gridColumns"]!.GetValue<int>(), host["gridColumns"]!.GetValue<int>());
        foreach (var group in schema["blockGroups"]!.AsArray())
            Assert.Contains(host["blockGroups"]!.AsArray(), other => JsonNode.DeepEquals(group, other));
        foreach (var block in schema["blocks"]!.AsArray())
        {
            var element = block!["contentElementTypeKey"]!.GetValue<string>();
            var expected = block.DeepClone().AsObject();
            var actual = Block(host, element).DeepClone().AsObject();
            if (file == "WebsiteBlocks.config")
            {
                // Website areas also admit native forms and reports. Their area keys and allowed blocks belong to
                // the host; field settings and every other schema setting must still follow AppBlocks.
                expected.Remove("areas");
                actual.Remove("areas");
                if (element == "322396a8-4598-4ff5-8312-6ee2cdf866cc") expected["allowInAreas"] = true;
            }
            Assert.True(JsonNode.DeepEquals(expected, actual), $"{file}'s {block["label"]} block differs from the schema's.");
        }
    }

    [Fact]
    public void Every_sample_data_source_explicitly_requires_sign_in()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(Export, "Content"), "*.config")
            .Select(XElement.Load).Where(node => Text(node.Element("Info")!, "ContentType") == DataSourceSchema.SqlAlias).ToArray();
        Assert.NotEmpty(sources);
        foreach (var node in sources)
        {
            object? Value(string alias) => node.Element("Properties")?.Element(alias)?.Element("Value")?.Value;
            var errors = new List<string>();
            var source = DataSourceDefinition.Read(Key(node), Alias(node), Value, _ => [], errors);
            Assert.True(source is not null, $"{Alias(node)}: {string.Join("; ", errors)}");
            Assert.Equal(DataSourceAccess.NotPublic, source.Access);
        }
    }

    [Fact]
    public void The_host_paper_report_type_has_the_schema_properties_and_tabs()
    {
        static string[] Shape(XElement type) => [
            .. Properties(type).Select(p => string.Join('|', Text(p, "Key"), Text(p, "Alias"), Text(p, "Definition"), Text(p, "Mandatory"), Text(p, "SortOrder"), p.Element("Tab")!.Attribute("Alias")!.Value)).Order(StringComparer.Ordinal),
            .. type.Element("Tabs")!.Elements("Tab").Select(tab => Text(tab, "Key") + "|" + Text(tab, "Alias")).Order(StringComparer.Ordinal),
        ];
        var host = XElement.Load(Path.Combine(Export, "ContentTypes", "paperreport.config"));
        Assert.Equal(Shape(SchemaType("paperReport")), Shape(host));
    }

    [Fact]
    public void The_report_and_form_blocks_have_their_paper_report_and_the_report_no_report_name()
    {
        var report = SchemaType("appReport");
        var paper = Assert.Single(Properties(report), p => Text(p, "Alias") == "paperReport");
        Assert.Equal("19d44ea0-2fc1-9c96-2309-48b710df73a7", Text(paper, "Key"));
        Assert.Equal("ae149b4b-f7e2-c6be-9ea4-58e0c3574f6b", Text(paper, "Definition"));
        Assert.DoesNotContain(Properties(report), p => Text(p, "Alias") == "reportName");
        Assert.Equal("a0d3f966-38f3-f7f4-5bdb-774a158efab3", Text(Assert.Single(Properties(SchemaType("appForm")), p => Text(p, "Alias") == "paperReport"), "Key"));
    }

    /// <summary>What the scripts remove from a site's working folder is everything a schema ships and the host does not keep.</summary>
    [Fact]
    public void The_list_the_scripts_remove_is_exactly_the_schemas_definitions()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository!.FullName, "UmbracoAppHost.slnx"))) repository = repository.Parent;
        var listed = File.ReadAllLines(Path.Combine(repository.FullName, "deployment", "appblocks-definitions.txt")).Where(line => line.Length > 0).ToArray();

        var expected = new List<string>();
        foreach (var schema in Schemas)
        foreach (var kind in Kinds)
            expected.AddRange(new Microsoft.Extensions.FileProviders.ManifestEmbeddedFileProvider(schema.Assembly).GetDirectoryContents("Schema/" + kind)
                .Where(file => !file.IsDirectory).Select(file => $"{kind}/{file.Name}"));
        expected.RemoveAll(AppBlocksDefinitions.HostOwned.Values.Contains);

        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), listed.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(listed, path => Assert.False(File.Exists(Path.Combine(Export, path)), $"{path} is on the list the scripts remove, and the export ships it."));
    }

    /// <summary>Every definition a site has once the schemas are installed and the export is imported.</summary>
    private sealed class Installed
    {
        public Dictionary<string, XElement> Types { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, XElement> TypesByKey { get; } = [];
        public Dictionary<Guid, XElement> DataTypes { get; } = [];

        public Installed()
        {
            // The export comes second, as its import does: what the host keeps replaces the schema's.
            foreach (var type in SchemaDefinitions("ContentTypes").Concat(ExportDefinitions("ContentTypes").Select(item => item.Definition)))
            {
                Types[Alias(type)] = type;
                TypesByKey[Key(type)] = type;
            }
            foreach (var dataType in SchemaDefinitions("DataTypes").Concat(ExportDefinitions("DataTypes").Select(item => item.Definition)))
                DataTypes[Key(dataType)] = dataType;
        }

        /// <summary>A type's properties, its compositions' included.</summary>
        public IEnumerable<XElement> AllProperties(XElement type) => Properties(type).Concat(type.Element("Info")!.Element("Compositions")!.Elements()
            .Where(composition => TypesByKey.ContainsKey(Guid.Parse(composition.Attribute("Key")!.Value)))
            .SelectMany(composition => AllProperties(TypesByKey[Guid.Parse(composition.Attribute("Key")!.Value)])));
    }

    [Fact]
    public void Every_definition_uses_only_definitions_that_exist_once_the_schemas_are_installed()
    {
        var installed = new Installed();
        var problems = new List<string>();
        foreach (var type in installed.Types.Values)
        {
            foreach (var property in Properties(type))
                if (!installed.DataTypes.ContainsKey(Guid.Parse(Text(property, "Definition"))))
                    problems.Add($"{Alias(type)}.{Text(property, "Alias")} uses data type {Text(property, "Definition")}");
            foreach (var composition in type.Element("Info")!.Element("Compositions")!.Elements())
                if (!installed.TypesByKey.ContainsKey(Guid.Parse(composition.Attribute("Key")!.Value)))
                    problems.Add($"{Alias(type)} is composed of {composition.Value}");
            foreach (var child in type.Element("Structure")!.Elements("ContentType"))
                if (!installed.TypesByKey.ContainsKey(Guid.Parse(child.Attribute("Key")!.Value)))
                    problems.Add($"{Alias(type)} allows {child.Value} as a child");
        }
        foreach (var dataType in installed.DataTypes.Values)
        {
            if (Text(dataType, "Config") is not { Length: > 0 } config || JsonNode.Parse(config)?["blocks"] is not JsonArray blocks) continue;
            foreach (var block in blocks)
            foreach (var reference in new[] { "contentElementTypeKey", "settingsElementTypeKey" })
                if (block![reference]?.GetValue<string>() is { Length: > 0 } element && !installed.TypesByKey.ContainsKey(Guid.Parse(element)))
                    problems.Add($"{Alias(dataType)} offers block type {element}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Everything_the_exports_content_uses_exists_once_the_schemas_are_installed()
    {
        var installed = new Installed();
        var templates = Directory.EnumerateFiles(Path.Combine(Export, "Templates"), "*.config").Select(file => Key(XElement.Load(file))).ToHashSet();
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Export, "Content"), "*.config"))
        {
            var content = XElement.Load(file);
            var name = Path.GetFileName(file);
            var info = content.Element("Info")!;
            if (!installed.Types.TryGetValue(Text(info, "ContentType"), out var type))
            {
                problems.Add($"{name} is a {Text(info, "ContentType")}, which no schema or export defines.");
                continue;
            }
            if (info.Element("Template")?.Attribute("Key")?.Value is { Length: > 0 } template && !templates.Contains(Guid.Parse(template)))
                problems.Add($"{name} uses template {info.Element("Template")!.Value}, which the export does not define.");
            var properties = installed.AllProperties(type).ToDictionary(p => Text(p, "Alias"));
            foreach (var value in content.Element("Properties")?.Elements() ?? [])
            {
                if (!properties.TryGetValue(value.Name.LocalName, out var property))
                {
                    problems.Add($"{name} sets {value.Name.LocalName}, which {Alias(type)} does not have.");
                    continue;
                }
                foreach (var saved in value.Elements("Value"))
                    if (installed.DataTypes.TryGetValue(Guid.Parse(Text(property, "Definition")), out var dataType) && IsBlockEditor(dataType) && saved.Value.Trim() is { Length: > 0 } json)
                        CheckBlocks(installed, $"{name} {value.Name.LocalName}", dataType, JsonNode.Parse(json)!, problems);
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static bool IsBlockEditor(XElement dataType) => Text(dataType.Element("Info")!, "EditorAlias") is "Umbraco.BlockGrid" or "Umbraco.BlockList" or "Umbraco.SingleBlock";

    /// <summary>Each block is a type its editor offers, and sets only properties that type has, however deep it is nested.</summary>
    private static void CheckBlocks(Installed installed, string where, XElement dataType, JsonNode value, List<string> problems)
    {
        var editor = Text(dataType.Element("Info")!, "EditorAlias");
        if (value["layout"]?[editor] is not JsonArray layout)
            problems.Add($"{where} needs a {editor} layout to match {Alias(dataType)}.");
        else if (editor == "Umbraco.SingleBlock" && layout.Count > 1)
            problems.Add($"{where} cannot contain more than one Single Block.");
        var offered = (JsonNode.Parse(Text(dataType, "Config"))?["blocks"]?.AsArray() ?? []).Select(block => block!["contentElementTypeKey"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var section in new[] { "contentData", "settingsData" })
        foreach (var block in value[section]?.AsArray() ?? [])
        {
            var key = block!["contentTypeKey"]!.GetValue<string>();
            if (!installed.TypesByKey.TryGetValue(Guid.Parse(key), out var element))
            {
                problems.Add($"{where} has a block of type {key}, which no schema or export defines.");
                continue;
            }
            if (section == "contentData" && !offered.Contains(key)) problems.Add($"{where} has a {Alias(element)} block, which {Alias(dataType)} does not offer.");
            var properties = installed.AllProperties(element).ToDictionary(p => Text(p, "Alias"));
            foreach (var property in block["values"]?.AsArray() ?? [])
            {
                var alias = property!["alias"]!.GetValue<string>();
                if (!properties.TryGetValue(alias, out var definition))
                {
                    problems.Add($"{where} sets {alias} on a {Alias(element)} block, which does not have it.");
                    continue;
                }
                if (installed.DataTypes.TryGetValue(Guid.Parse(Text(definition, "Definition")), out var nested) && IsBlockEditor(nested) && property["value"] is { } inner)
                    CheckBlocks(installed, $"{where} > {Alias(element)}.{alias}", nested, inner is JsonValue ? JsonNode.Parse(inner.GetValue<string>())! : inner, problems);
            }
        }
    }
}
