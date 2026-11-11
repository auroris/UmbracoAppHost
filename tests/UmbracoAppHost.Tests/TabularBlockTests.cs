using System.Text.Json.Nodes;
using System.Xml.Linq;
using AppBlocks.Blocks;

namespace UmbracoAppHost.Tests;

/// <summary>The Tabular form block in the host's saved export. AppBlocks tests the block itself; <see cref="TabularHttpTests"/> publishes and drives it in a site.</summary>
public sealed class TabularBlockTests
{
    [Fact]
    public void Both_grids_offer_the_tabular_form_in_the_content_group_in_areas()
    {
        var key = AppBlockSchema.Key(AppBlockSchema.TabularAlias).ToString();
        foreach (var file in new[] { "ApplicationApplicationBlocks.config", "WebsiteBlocks.config" })
        {
            var config = JsonNode.Parse(XElement.Load(Path.Combine(AppContext.BaseDirectory, "uSync", "DataTypes", file)).Element("Config")!.Value)!;
            var blocks = config["blocks"]!.AsArray();
            var block = blocks.Single(b => b!["contentElementTypeKey"]!.GetValue<string>() == key)!;
            Assert.Equal("Tabular form", block["label"]!.GetValue<string>());
            Assert.Equal(ApplicationStarter.Key("blockgroup:Content").ToString(), block["groupKey"]!.GetValue<string>());
            Assert.True(block["allowAtRoot"]!.GetValue<bool>());
            Assert.True(block["allowInAreas"]!.GetValue<bool>());
            // Right after the Attachments block, which it resembles.
            var attachments = blocks.Select((b, i) => (b, i)).Single(p => p.b!["contentElementTypeKey"]!.GetValue<string>() == AppBlockSchema.Key(AppBlockSchema.AttachmentsAlias).ToString()).i;
            Assert.Same(block, blocks[attachments + 1]);
        }
    }

    [Fact]
    public void The_lines_data_source_is_imported_before_the_pages_that_use_it()
    {
        // A first import publishes the nodes of one level in sort order, and publishing a page checks its data source,
        // so the source must come before both sample pages whatever order their parents give.
        static int SortOrder(Guid key) => int.Parse(XElement.Load(USyncExport.ContentByKey(ApplicationStarter.Export, key).File).Element("Info")!.Element("SortOrder")!.Value);
        var lines = SortOrder(ApplicationStarter.OrderLinesDataSourceKey);
        Assert.True(lines < SortOrder(ApplicationStarter.FormPageKey));
        Assert.True(lines < SortOrder(Guid.Parse("d9a69f9f-f8e4-dc51-76c8-9d1ec178b238")));
    }
}
