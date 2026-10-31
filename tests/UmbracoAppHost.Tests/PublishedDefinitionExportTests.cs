using System.Xml.Linq;

namespace UmbracoAppHost.Tests;

public sealed class PublishedDefinitionExportTests
{
    [Fact]
    public void Every_document_type_group_has_its_own_database_identity()
    {
        // The export's types and the schemas', which a site has together. A type in both is the same type, with the same groups.
        var types = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "uSync", "ContentTypes"), "*.config").Select(XElement.Load)
            .Concat(new[] { typeof(AppBlocks.AppBlocksExtensions).Assembly, typeof(AppBlocks.InteractiveReports.InteractiveReportsExtensions).Assembly, typeof(AppBlocks.Documents.AppBlocksDocumentsExtensions).Assembly }
                .SelectMany(assembly => new AppBlocks.Setup.AppBlocksSchema(assembly).Definitions("ContentTypes")))
            .DistinctBy(type => type.Attribute("Alias")!.Value);
        var groups = types.SelectMany(type => type.Element("Tabs")?.Elements("Tab").Select(tab => new {
                File = type.Attribute("Alias")!.Value, Key = Guid.Parse(tab.Element("Key")!.Value),
            }) ?? []).ToArray();
        var duplicates = groups.GroupBy(group => group.Key).Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(item => item.File))).ToArray();
        Assert.Empty(duplicates);
    }
}
