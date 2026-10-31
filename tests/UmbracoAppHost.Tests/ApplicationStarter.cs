using AppBlocks.Blocks;
using AppBlocks.DataSources;

namespace UmbracoAppHost.Tests;

internal static class ApplicationStarter
{
    public static readonly Guid ReportPageKey = AppBlockSchema.Key("sample:orders-block");
    public static readonly Guid FormPageKey = AppBlockSchema.Key("sample:orders-form");
    public static readonly Guid FormKey = AppBlockSchema.Key("sample:form");
    public static readonly Guid ReportKey = AppBlockSchema.Key("sample:report");
    public static readonly Guid DataSourceRootKey = AppBlockSchema.Key("datasource:content:root");
    public static readonly Guid OrdersDataSourceKey = AppBlockSchema.Key("datasource:content:orders");
    public static readonly Guid OrderLinesDataSourceKey = AppBlockSchema.Key("sample:orders-lines");
    /// <summary>The Orders form page's Tabular form block, which edits the order's lines.</summary>
    public static readonly Guid FormLinesBlockKey = AppBlockSchema.Key("sample:orders-form:lines");

    /// <summary>The library's saved export, which the build copies beside the tests.</summary>
    public static string Export => Path.Combine(AppContext.BaseDirectory, "uSync");

    public static string FormContent() => BlockContent(FormPageKey);
    public static string ReportContent() => BlockContent(ReportPageKey);

    private static string BlockContent(Guid page) => USyncExport.ContentByKey(Export, page).Property(AppBlockSchema.BodyAlias)!;

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
