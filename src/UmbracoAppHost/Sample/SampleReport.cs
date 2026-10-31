using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using AppBlocks.Connections;
using AppBlocks.Databases;

namespace UmbracoAppHost.Application;

/// <summary>
/// The built-in <c>orders</c> example, backed by an application database: a SQLite file in the instance's data
/// folder unless <c>ConnectionStrings:ReportsSampleDb</c> says otherwise. The application starter shows it on its
/// Orders page. Additional application reports are registered alongside this example in the common host.
/// </summary>
public static class SampleReport
{
    public const string Connection = "ReportsSampleDb";
    public const string DatabaseFile = "ReportsSample.sqlite.db";
    public const string ReportName = "orders";

    /// <summary>
    /// Registers the sample connection. Data is created on first use, so unused building blocks
    /// do not require or initialize an application database during host startup.
    /// </summary>
    /// <param name="environment">Unused: the instance folder comes from the services when a connection is created.</param>
    /// <param name="configuration">Unused, for the same reason.</param>
    public static AppBlocksBuilder AddSampleOrders(this AppBlocksBuilder connections, IWebHostEnvironment environment, IConfiguration? configuration = null)
        => connections.AddApplicationDatabase(Connection, SampleData.Schema, DatabaseFile);

    /// <summary>
    /// Registers the sample connection and the <see cref="ReportName"/> report over it. The report is added only when
    /// the configured reports have none of that name, so a host's own <c>orders</c> wins; call it from the connections
    /// callback of <c>AddUmbracoAppHost</c>, after Interactive Reports has bound its configuration.
    /// </summary>
    public static AppBlocksBuilder AddSampleOrders(this AppBlocksBuilder connections, IServiceCollection services)
    {
        services.Configure<InteractiveReportOptions>(options => options.Reports.TryAdd(ReportName, new ReportDefinition
        {
            Title = "Orders",
            Connection = Connection,
            Sql = "SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS",
            ColumnLabels = new() { ["ORDER_ID"] = "Order #", ["CUSTOMER"] = "Customer Name", ["ORDER_DATE"] = "Ordered On" },
            Columns = new() { ["AMOUNT"] = new() { HelpText = "Order total before tax." } },
        }));
        return connections.AddApplicationDatabase(Connection, SampleData.Schema, DatabaseFile);
    }
}
