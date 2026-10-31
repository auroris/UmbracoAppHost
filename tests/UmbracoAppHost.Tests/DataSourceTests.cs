using System.Security.Claims;
using InteractiveReport.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UmbracoAppHost.Application;
using AppBlocks.DataSources;

namespace UmbracoAppHost.Tests;

/// <summary>The saved export's Orders data source over the sample database. AppBlocks tests the data source engine itself.</summary>
public sealed class DataSourceTests
{
    [Fact]
    public void The_sample_orders_data_source_from_the_saved_export_reads_the_sample_database()
    {
        var root = Directory.CreateTempSubdirectory("data-source-sample-").FullName;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UmbracoAppHost:DataRoot"] = root, ["AppBlocks:DataRoot"] = root }).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new SampleEnvironment(root));
        collection.AddInteractiveReports(configuration).AddAppBlocksConnections(collection, connections => connections.AddSampleOrders(new SampleEnvironment(root), configuration));
        collection.AddSingleton<DataSourceRuntime>();
        using var provider = collection.AddAppBlocksReportTestSupport().BuildServiceProvider();
        var definition = ApplicationStarter.OrdersDataSource();
        var errors = provider.GetRequiredService<DataSourceRuntime>().Validate(definition);
        Assert.True(errors.Count == 0, string.Join(" | ", errors));
        var source = new SqlDataSource(definition, provider.GetRequiredService<DataSourceRuntime>(), new NoUsers(), []);
        var fields = source.Fields.ToDictionary(f => f.Key);
        // The sample form takes these choices from its published lists, not a duplicate static constraint.
        Assert.Null(fields["region"].Choices);
        Assert.Null(fields["status"].Choices);
        Assert.Equal("date", fields["orderDate"].Kind);
        Assert.Equal(200, fields["customer"].MaxLength);
        Assert.True(fields["orderId"].ReadOnly && fields["version"].ReadOnly && fields["updatedBy"].ReadOnly);
    }

    private sealed class NoUsers : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult<DataSourceUser?>(null);
    }

    private sealed class SampleEnvironment(string root) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Data source tests";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
