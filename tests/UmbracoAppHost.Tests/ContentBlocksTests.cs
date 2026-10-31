using System.Text.Json.Nodes;
using System.Xml.Linq;
using AppBlocks.Blocks;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The content blocks in the host's saved export: its grid data type, which unlike the AppBlocks package's offers raw
/// HTML, CSS and JavaScript, and the demo page <see cref="ContentBlocksHttpTests"/> renders. AppBlocks tests the blocks themselves.
/// </summary>
public sealed class ContentBlocksTests
{
    [Fact]
    public void The_grid_offers_the_content_blocks_wherever_it_offers_fields()
    {
        var config = JsonNode.Parse(XElement.Load(Path.Combine(AppContext.BaseDirectory, "uSync", "DataTypes", "ApplicationApplicationBlocks.config"))
            .Element("Config")!.Value)!;
        var blocks = config["blocks"]!.AsArray().Select(b => b!).ToArray();
        JsonNode Block(string alias) => blocks.Single(b => b["contentElementTypeKey"]!.GetValue<string>() == AppBlockSchema.Key(alias).ToString());
        var content = AppBlockSchema.Key("blockgroup:Content").ToString();
        foreach (var alias in AppBlockSchema.ContentAliases)
        {
            var block = Block(alias);
            Assert.Equal(content, block["groupKey"]!.GetValue<string>());
            Assert.True(block["allowAtRoot"]!.GetValue<bool>()); // At the page level too, since 2026-10-30.
            Assert.True(block["allowInAreas"]!.GetValue<bool>());
            Assert.Empty(block["areas"]!.AsArray());
        }
        // The same group and placement as the field blocks, so every area that takes one takes the other.
        Assert.Equal(content, Block("appText")["groupKey"]!.GetValue<string>());
    }

    /// <summary>The Content blocks page of the saved export, which shows every new block and field property.</summary>
    internal static string DemoContent() => XElement.Load(Path.Combine(AppContext.BaseDirectory, "uSync", "Content", "content-blocks.config"))
        .Element("Properties")!.Element(AppBlockSchema.BodyAlias)!.Element("Value")!.Value;
}
