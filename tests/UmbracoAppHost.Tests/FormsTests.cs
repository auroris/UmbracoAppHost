using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using UmbracoAppHost.Application;
using AppBlocks.Connections;
using AppBlocks.Forms;

namespace UmbracoAppHost.Tests;

/// <summary>The sample's Orders report and its C# form resource. AppBlocks tests the forms themselves.</summary>
public sealed class FormsTests
{
    [Fact]
    public void Sample_report_creates_data_only_when_opened_and_uses_the_instance_folder()
    {
        var environment = new TestEnvironment();
        var dataRoot = Path.Combine(environment.ContentRootPath, "instance-data");
        Directory.CreateDirectory(Path.Combine(dataRoot, "App_Data"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["UmbracoAppHost:DataRoot"] = dataRoot,
            ["AppBlocks:DataRoot"] = dataRoot,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddInteractiveReports(configuration).AddAppBlocksConnections(services, connections => connections.AddSampleOrders(environment));
        using var provider = services.AddAppBlocksReportTestSupport().BuildServiceProvider();
        using var connection = provider.GetRequiredService<InteractiveReport.Core.Execution.IReportConnectionFactory>().CreateConnection(SampleReport.Connection);
        var file = Path.Combine(dataRoot, "App_Data", SampleReport.DatabaseFile);
        Assert.False(File.Exists(file));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ORDERS";
        Assert.Equal(500L, Convert.ToInt64(command.ExecuteScalar()));
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(Path.Combine(environment.ContentRootPath, "App_Data", SampleReport.DatabaseFile)));
    }

    [Fact]
    public async Task Orders_enforce_member_access_typed_validation_idempotency_and_concurrency()
    {
        var environment = new TestEnvironment();
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddInteractiveReports(configuration).AddAppBlocksConnections(services, connections => connections.AddSampleOrders(environment));
        await using var provider = services.AddAppBlocksReportTestSupport().BuildServiceProvider();
        IFormResource resource = new OrdersFormResource(provider.GetRequiredService<IApplicationConnectionFactory>());
        var context = new FormExecutionContext(new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, "test-member")], IdentityConstants.ApplicationScheme)), Guid.NewGuid(), Guid.NewGuid(), "1", Guid.NewGuid().ToString());
        var input = JsonSerializer.SerializeToElement(new { customer = "Test order", region = "NORTH", status = "NEW", amount = "125.34", orderDate = "2026-09-11", notes = "test" });
        var denied = context with { Member = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "editor")], "UmbracoBackOffice")) };
        Assert.Equal(403, (await resource.CreateAsync(input, denied, default)).Status);
        Assert.Equal(403, (await resource.LoadAsync("1", denied, default)).Status);
        Assert.Equal(403, (await resource.UpdateAsync("1", input, "1", denied, default)).Status);
        Assert.Equal(403, (await resource.DeleteAsync("1", "1", denied, default)).Status);
        Assert.Equal(404, (await resource.LoadAsync("1 OR 1=1", context, default)).Status);
        var invalid = JsonNode.Parse(input.GetRawText())!; invalid["status"] = "INVALID";
        Assert.Equal(422, (await resource.CreateAsync(JsonSerializer.SerializeToElement(invalid), context, default)).Status);
        invalid["status"] = "NEW"; invalid["owner"] = "someone-else";
        Assert.Equal(422, (await resource.CreateAsync(JsonSerializer.SerializeToElement(invalid), context, default)).Status);
        var created = await resource.CreateAsync(input, context, default);
        Assert.Equal(200, created.Status); Assert.Equal("125.34", created.Record!.Values["amount"]);
        var repeated = await resource.CreateAsync(input, context, default);
        Assert.Equal(created.Record.Key, repeated.Record!.Key);
        var changed = JsonNode.Parse(input.GetRawText())!; changed["notes"] = null;
        var updated = await resource.UpdateAsync(created.Record.Key!, JsonSerializer.SerializeToElement(changed), created.Record.Version!, context, default);
        Assert.Equal(200, updated.Status); Assert.Null(updated.Record!.Values["notes"]);
        Assert.NotEqual(created.Record.Version, updated.Record.Version);
        Assert.Equal(409, (await resource.UpdateAsync(created.Record.Key!, input, created.Record.Version!, context, default)).Status);
        Assert.Equal(409, (await resource.DeleteAsync(created.Record.Key!, created.Record.Version!, context, default)).Status);
        Assert.Equal(204, (await resource.DeleteAsync(created.Record.Key!, updated.Record.Version!, context, default)).Status);
        Assert.Equal(404, (await resource.CreateAsync(input, context, default)).Status); // Receipt survives deletion.
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Form tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Directory.CreateTempSubdirectory("app-form-test-").FullName;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
