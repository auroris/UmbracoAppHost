using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace UmbracoAppHost.Hosting;

/// <summary>The data and identity owned by one host, held exclusively until that host stops.</summary>
public sealed class AppHostInstance(string root, string settingsPath, FileStream lease) : IDisposable
{
    public string Root { get; } = root;
    public string SettingsPath { get; } = settingsPath;
    public void Dispose() => lease.Dispose();
}

internal static class InstanceConfiguration
{
    public static void Apply(WebApplicationBuilder builder)
    {
        using var defaultsStream = typeof(InstanceConfiguration).Assembly.GetManifestResourceStream("UmbracoAppHost.Hosting.defaults.json")!;
        var defaults = new ConfigurationBuilder().AddJsonStream(defaultsStream).Build();
        using var defaultsLifetime = defaults as IDisposable;
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = defaults.AsEnumerable().ToArray() });

        var root = Path.GetFullPath(builder.Configuration["InstanceRoot"] ?? Path.Combine("App_Data", "UmbracoAppHost"),
            builder.Environment.ContentRootPath);
        var state = Directory.CreateDirectory(Path.Combine(root, ".apphost")).FullName;
        FileStream lease;
        try { lease = File.Open(Path.Combine(state, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new InvalidOperationException("This instance is already running, being reset, or being updated.", exception); }

        try
        {
            var settingsPath = Path.Combine(root, "instance.json");
            if (!File.Exists(settingsPath))
            {
                using var settingsFile = File.Create(settingsPath);
                JsonSerializer.Serialize(settingsFile, new
                {
                    Umbraco = new { CMS = new
                    {
                        Global = new { Id = Guid.NewGuid().ToString() },
                        Imaging = new { HMACSecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) },
                        Unattended = new
                        {
                            InstallUnattended = true, UpgradeUnattended = true, UnattendedTelemetryLevel = "Minimal",
                            UnattendedUserName = "Administrator", UnattendedUserEmail = "admin@localhost.local",
                            UnattendedUserPassword = "Start!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)) + "a9",
                        },
                    } },
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            // Defaults and persisted identity come first. The host's appsettings, environment
            // variables and command line retain their usual precedence.
            builder.Configuration.Sources.Insert(1, new JsonConfigurationSource
            {
                Path = "instance.json",
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root),
                Optional = false,
                ReloadOnChange = false,
            });
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            var media = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
            Directory.CreateDirectory(Path.Combine(data, "App_Data"));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UmbracoAppHost:DataRoot"] = data,
                // AppBlocks keeps its files here too. A host that configured its own folder keeps it.
                ["AppBlocks:DataRoot"] = builder.Configuration["AppBlocks:DataRoot"] ?? data,
                ["Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath"] = media,
                ["uSync:Settings:RootFolder"] = Path.Combine(root, "uSync"),
            });
            var database = Path.Combine(data, "Umbraco.sqlite.db");
            var managedDatabase = string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("umbracoDbDSN"));
            if (managedDatabase)
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:umbracoDbDSN"] = new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true, Pooling = true }.ToString(),
                    ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
                });
            File.WriteAllText(Path.Combine(state, "runtime.json"), JsonSerializer.Serialize(new
            {
                projectRoot = builder.ProjectRoot(), managedDatabase,
                uSyncRoot = Path.Combine(AppContext.BaseDirectory, "uSync"),
                hostExport = USyncFiles.HostExport(builder.Configuration, builder.Environment.ContentRootPath, Path.Combine(root, "uSync")),
                samples = builder.Configuration.GetValue(SampleContent.Setting, true),
            }));
            builder.Services.AddDataProtection().PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(data, "keys")))
                .SetApplicationName(builder.Configuration["Umbraco:CMS:Global:Id"]!);
            var instance = new AppHostInstance(root, settingsPath, lease);
            // A factory makes the DI container own the lease and dispose it with the host.
            builder.Services.AddSingleton(_ => instance);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}
