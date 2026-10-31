using System.Xml.Linq;
using Microsoft.Extensions.FileProviders;
using UmbracoAppHost.Themes;

namespace UmbracoAppHost.Hosting;

/// <summary>The packaged source baseline; working exports always go to the instance.</summary>
internal static class USyncFiles
{
    /// <summary>The host's own saved export, relative to its <see cref="AppHostProject"/> root; empty disables it.</summary>
    public const string HostExportSetting = "UmbracoAppHost:HostExport";

    private static readonly IFileProvider Files = new ManifestEmbeddedFileProvider(typeof(USyncFiles).Assembly, "uSync");

    /// <summary>
    /// The folder <see cref="HostExportSetting"/> names (default <c>uSync</c>) as an absolute path, or null when the
    /// setting is empty, the folder does not exist, or it is the working folder itself.
    /// </summary>
    public static string? HostExport(IConfiguration configuration, string contentRoot, string workingFolder)
    {
        if ((configuration[HostExportSetting] ?? "uSync") is not { Length: > 0 } setting) return null;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(setting, AppHostProject.Root(configuration, contentRoot)));
        return Directory.Exists(folder) && !folder.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingFolder)), StringComparison.OrdinalIgnoreCase)
            ? folder : null;
    }

    /// <summary>
    /// Supplies a working folder that has no <c>usync.config</c>: the library's export, without the sample content
    /// unless <paramref name="includeSamples"/>, then every file of <paramref name="hostExport"/> at a path the library
    /// does not ship, then the same of each theme library's export. Existing files are never overwritten.
    /// </summary>
    public static void EnsureWorkingCopy(string destination, bool includeSamples, string? hostExport, ILogger logger, IEnumerable<ThemeLibrary>? libraries = null)
    {
        if (File.Exists(Path.Combine(destination, "usync.config"))) return;
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CopyDirectory("/", destination);
        if (hostExport is not null)
        {
            var working = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
            var copied = 0;
            foreach (var file in Directory.EnumerateFiles(hostExport, "*.config", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(hostExport, file);
                var target = Path.Combine(destination, relative);
                // uSync's default working folder, uSync/v18, sits inside a host's uSync folder.
                if (shipped.Contains(relative) || file.StartsWith(working, StringComparison.OrdinalIgnoreCase) || File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                copied++;
            }
            logger.LogInformation("Copied {Count} files of the host's export {HostExport} to the working uSync folder {Destination}.", copied, hostExport, destination);
        }
        foreach (var library in libraries ?? []) CopyLibraryExport(library.Files, "uSync", destination);

        void CopyLibraryExport(IFileProvider files, string sourceDirectory, string targetDirectory)
        {
            foreach (var file in files.GetDirectoryContents(sourceDirectory))
            {
                var target = Path.Combine(targetDirectory, file.Name);
                if (file.IsDirectory) { CopyLibraryExport(files, sourceDirectory + "/" + file.Name, target); continue; }
                if (!file.Name.EndsWith(".config", StringComparison.OrdinalIgnoreCase) || shipped.Contains(Path.GetRelativePath(destination, target)) || File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = file.CreateReadStream();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                source.CopyTo(output);
            }
        }

        void CopyDirectory(string sourceDirectory, string targetDirectory)
        {
            foreach (var file in Files.GetDirectoryContents(sourceDirectory))
            {
                var target = Path.Combine(targetDirectory, file.Name);
                if (file.IsDirectory) { CopyDirectory(sourceDirectory.TrimEnd('/') + "/" + file.Name, target); continue; }
                if (!file.Name.EndsWith(".config", StringComparison.OrdinalIgnoreCase)) continue;
                shipped.Add(Path.GetRelativePath(destination, target));
                if (File.Exists(target) || (!includeSamples && sourceDirectory == "/Content" && IsSample(file))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = file.CreateReadStream();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                source.CopyTo(output);
            }
        }
    }

    private static bool IsSample(IFileInfo file)
    {
        using var stream = file.CreateReadStream();
        return SampleContent.IsSample(XElement.Load(stream));
    }
}
