using System.Security.Cryptography;
using System.Text;
using AppBlocks.DataSources;

namespace UmbracoAppHost.Tests;

internal static class ApplicationStarter
{
    // These aliases belong to the host's exported content types, not to the generic block library.
    public const string BlockPageAlias = "appBlockPage";
    public const string BlockPropertyAlias = "applicationBlocks";
    // Sample nodes, block instances and editor groups retain the host's original identities.
    // Only block type definitions moved to AppBlockSchema's package-owned key seed.
    public static Guid Key(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes("UmbracoAppHost.Blocks.v1:" + value))[..16]);
    public static readonly Guid ReportPageKey = Key("sample:orders-block");
    public static readonly Guid FormPageKey = Key("sample:orders-form");
    public static readonly Guid FormKey = Key("sample:form");
    public static readonly Guid ReportKey = Key("sample:report");
    public static readonly Guid DataSourceRootKey = Key("datasource:content:root");
    public static readonly Guid OrdersDataSourceKey = Key("datasource:content:orders");
    public static readonly Guid OrderLinesDataSourceKey = Key("sample:orders-lines");
    /// <summary>The Orders form page's Tabular form block, which edits the order's lines.</summary>
    public static readonly Guid FormLinesBlockKey = Key("sample:orders-form:lines");

    /// <summary>The library's saved export, which the build copies beside the tests.</summary>
    public static string Export => Path.Combine(AppContext.BaseDirectory, "uSync");

    public static string FormContent() => BlockContent(FormPageKey);
    public static string ReportContent() => BlockContent(ReportPageKey);

    private static string BlockContent(Guid page) => USyncExport.ContentByKey(Export, page).Property(BlockPropertyAlias)!;

    /// <summary>The Orders data source exactly as the saved export defines it, found by its key.</summary>
    public static DataSourceDefinition OrdersDataSource()
    {
        var node = USyncExport.ContentByKey(Export, OrdersDataSourceKey);
        var errors = new List<string>();
        var definition = DataSourceDefinition.Read(OrdersDataSourceKey, node.Alias, node.Property, _ => [], errors);
        Assert.True(definition is not null, string.Join(" ", errors));
        return definition;
    }
}
