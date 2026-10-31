using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using File = System.IO.File;

namespace UmbracoAppHost.Testing;

public sealed partial class TestSite
{
    /// <summary>Read on every start and written to nothing an install keeps, so sites that differ only in these share an install.</summary>
    private static readonly string[] StartupSettings = ["IdentityServer:", "EmbeddedIdentityServer:"];

    private const string FirstBootState = Constants.Conventions.Migrations.KeyValuePrefix + "uSync_FirstBoot";

    private static readonly Dictionary<string, string> Snapshots = [];
    private static readonly SemaphoreSlim SnapshotGate = new(1, 1);

    /// <summary>
    /// Fills a new root from this process's snapshot of an install with the same options, making the snapshot if it is
    /// the first. A setting not known to be read only at startup is part of what makes an install different, so an
    /// unfamiliar one costs another install rather than a site that was installed differently from what it asked for.
    /// </summary>
    private static async Task CopyInstallSnapshotAsync(TestSiteOptions options, string project, Type? host, string root, CancellationToken ct)
    {
        var settings = options.Settings.Where(setting => !StartupSettings.Any(prefix => setting.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(setting => setting.Key, StringComparer.Ordinal).ToArray();
        var key = string.Join('\n', [project, host?.AssemblyQualifiedName, options.EnvironmentName, options.AdminPassword, options.FirstBootGroup,
            .. settings.Select(setting => $"{setting.Key}={setting.Value}")]);
        await SnapshotGate.WaitAsync(ct);
        try
        {
            if (!Snapshots.TryGetValue(key, out var snapshot))
            {
                snapshot = Directory.CreateTempSubdirectory("apphost-test-install-").FullName;
                var install = new TestSiteOptions
                {
                    Application = options.Application, ProjectFolder = project, Host = host, Root = snapshot,
                    EnvironmentName = options.EnvironmentName, AdminPassword = options.AdminPassword, FirstBootGroup = options.FirstBootGroup,
                    InstallTimeout = options.InstallTimeout, TestLoginRoute = false, UseInstallSnapshot = false,
                };
                foreach (var (name, value) in settings) install.Settings[name] = value;
                // Stopping the site closes its unpooled SQLite connections, which leaves the database whole in its file.
                await using (var site = await StartAsync(install, ct)) await site.WaitForFirstBootAsync(options.InstallTimeout, ct);
                if (Snapshots.Count == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => { foreach (var folder in Snapshots.Values) TryDelete(folder); };
                Snapshots[key] = snapshot;
            }
            CopyFolder(snapshot, root);
        }
        finally { SnapshotGate.Release(); }
    }

    /// <summary>Waits for uSync's first-boot import, which runs in the background after the site has started.</summary>
    private async Task WaitForFirstBootAsync(TimeSpan limit, CancellationToken ct)
    {
        if (!App.Configuration.GetValue("uSync:Settings:ImportOnFirstBoot", true)) return;
        var values = Services.GetRequiredService<IKeyValueService>();
        var waited = Stopwatch.StartNew();
        while (values.GetValue(FirstBootState) is null)
        {
            if (waited.Elapsed >= limit) throw new TestSiteException($"uSync's first-boot import was still running after {limit}.");
            await Task.Delay(100, ct);
        }
    }

    private static void CopyFolder(string from, string to)
    {
        var logs = Path.Combine(from, "umbraco", "Logs") + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(logs, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>A file a stopping site still holds only leaves a temporary folder behind, which is no reason to fail a test.</summary>
    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
