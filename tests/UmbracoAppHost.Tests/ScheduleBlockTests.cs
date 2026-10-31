using System.Text.Json.Nodes;
using System.Xml.Linq;
using AppBlocks.Blocks;

namespace UmbracoAppHost.Tests;

/// <summary>The Schedule block in the host's saved export. AppBlocks tests the block itself; <see cref="ScheduleHttpTests"/> renders the sample.</summary>
public sealed class ScheduleBlockTests
{
    [Fact]
    public void Both_grids_offer_the_schedule_in_the_content_group_at_the_root_and_in_areas()
    {
        var key = AppBlockSchema.Key(AppBlockSchema.ScheduleAlias).ToString();
        foreach (var file in new[] { "ApplicationApplicationBlocks.config", "WebsiteBlocks.config" })
        {
            var config = JsonNode.Parse(XElement.Load(Path.Combine(AppContext.BaseDirectory, "uSync", "DataTypes", file)).Element("Config")!.Value)!;
            var block = config["blocks"]!.AsArray().Single(b => b!["contentElementTypeKey"]!.GetValue<string>() == key)!;
            Assert.Equal(AppBlockSchema.Key("blockgroup:Content").ToString(), block["groupKey"]!.GetValue<string>());
            Assert.True(block["allowAtRoot"]!.GetValue<bool>());
            Assert.True(block["allowInAreas"]!.GetValue<bool>());
        }
    }
}
