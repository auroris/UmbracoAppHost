using System.Data.Common;
using AppBlocks.Connections;
using AppBlocks.InteractiveReports;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AppBlocks.DataSources;

namespace UmbracoAppHost.Tests;

/// <summary>Minimal fixtures use the data source engine over the report engine's connections, without a full Umbraco composer pipeline.</summary>
internal static class AppBlocksReportTestSupport
{
    public static IServiceCollection AddAppBlocksReportTestSupport(this IServiceCollection services)
    {
        services.TryAddSingleton<IApplicationConnectionFactory, TestConnections>();
        services.TryAddSingleton<DataSourceRuntime>();
        return services;
    }

    /// <summary>Registers connections with AppBlocks as a host does; the report engine gets them as the companion passes them on.</summary>
    public static InteractiveReportBuilder AddAppBlocksConnections(this InteractiveReportBuilder reports, IServiceCollection services, Action<AppBlocksBuilder> configure)
    {
        var connections = new AppBlocksBuilder();
        configure(connections);
        services.AddSingleton(connections);
        return reports.AddConnectionsFrom(connections);
    }

    private sealed class TestConnections(IReportConnectionFactory reports, ReportDefinitionCompiler compiler) : IApplicationConnectionFactory
    {
        public DbConnection CreateConnection(string name) => reports.CreateConnection(name);
        public DatabaseDialect Dialect(string name) => Enum.Parse<DatabaseDialect>(compiler.Compile("data-source",
            new ReportDefinition { Connection = name, Sql = "SELECT 1" }).Dialect!.Value.ToString());
        public void SetDetectedDialect(string name, DatabaseDialect dialect) => reports.SetDetectedDialect(name, Enum.Parse<ReportDialect>(dialect.ToString()));
    }
}
