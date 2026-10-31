using System.Xml.Linq;
using UmbracoAppHost.Hosting;

namespace UmbracoAppHost.Tests;

public sealed class SampleContentTests
{
    /// <summary>The Data sources root's own children that only the samples use; the root and the Welcome report are every site's.</summary>
    private static readonly string[] SampleDataSources =
        ["/DataSources/Orders", "/DataSources/Orderslines", "/DataSources/OrdersPrintRows", "/DataSources/Ordersreport", "/DataSources/Orderregionchoices", "/DataSources/Orderstatuschoices",
            "/DataSources/Ordersschedulerows", "/DataSources/Ordersschedulebars", "/DataSources/Ordersschedule"];

    [Fact]
    public void The_samples_are_the_two_sample_roots_and_the_orders_data_source_nodes_of_the_saved_export()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "uSync", "Content"), "*.config").Select(XElement.Load).ToList();
        static bool Under(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
        var samples = files.Where(file => file.Element("Info")!.Element("Path")!.Value is var path
            && (Under(path, "/SampleApplication") || Under(path, "/WebsiteThemeDemo") || SampleDataSources.Contains(path))).ToList();

        Assert.All(samples, file => Assert.True(SampleContent.IsSample(file), file.Element("Info")!.Element("Path")!.Value));
        Assert.All(files.Except(samples), file => Assert.False(SampleContent.IsSample(file), file.Element("Info")!.Element("Path")!.Value));
        // Every listed key belongs to a sample file, so the list holds nothing else.
        Assert.Equal(samples.Select(file => Guid.Parse(file.Attribute("Key")!.Value)).Order(), SampleContent.Keys.Order());
    }
}
