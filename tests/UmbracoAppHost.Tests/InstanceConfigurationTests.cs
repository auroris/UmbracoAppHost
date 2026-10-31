using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UmbracoAppHost.Hosting;

namespace UmbracoAppHost.Tests;

/// <summary>Where an instance tells AppBlocks to keep its files, so an existing instance keeps finding them.</summary>
public sealed class InstanceConfigurationTests
{
    [Fact]
    public void AppBlocks_keeps_its_files_in_the_instance_data_folder()
    {
        var root = Directory.CreateTempSubdirectory("instance-config-").FullName;
        var configuration = Apply(root, configured: null);
        Assert.Equal(Path.Combine(root, "instance", "data"), configuration["AppBlocks:DataRoot"]);
        Assert.Equal(configuration["UmbracoAppHost:DataRoot"], configuration["AppBlocks:DataRoot"]);
    }

    [Fact]
    public void A_host_that_configured_the_AppBlocks_folder_keeps_it()
    {
        var root = Directory.CreateTempSubdirectory("instance-config-").FullName;
        var configured = Path.Combine(root, "appblocks");
        var configuration = Apply(root, configured);
        Assert.Equal(configured, configuration["AppBlocks:DataRoot"]);
        Assert.Equal(Path.Combine(root, "instance", "data"), configuration["UmbracoAppHost:DataRoot"]);
    }

    private static IConfiguration Apply(string root, string? configured)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root, ApplicationName = typeof(Program).Assembly.GetName().Name, EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InstanceRoot"] = Path.Combine(root, "instance"),
            ["AppBlocks:DataRoot"] = configured,
        });
        InstanceConfiguration.Apply(builder);
        // Release the instance lease; only the configuration is under test.
        using var services = builder.Services.BuildServiceProvider();
        services.GetRequiredService<AppHostInstance>().Dispose();
        return builder.Configuration;
    }
}
