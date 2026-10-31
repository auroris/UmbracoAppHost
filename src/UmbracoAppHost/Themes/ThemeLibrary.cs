using System.Reflection;
using Microsoft.Extensions.FileProviders;

namespace UmbracoAppHost.Themes;

/// <summary>
/// A Razor class library that ships a theme. Register one in DI beside the library's <see cref="SiteTheme"/>, and a
/// site referencing it gets the assembly's embedded <c>Views/</c> as this library's own views are supplied: Razor finds
/// them, and the top-level ones are written to the host's Views folder before uSync imports. A new site's working
/// export also receives the assembly's embedded <c>uSync/</c> files at paths that neither this library nor the host's
/// export ship. The library's project sets <c>GenerateEmbeddedFilesManifest</c> and embeds both folders.
/// </summary>
public sealed class ThemeLibrary(Assembly assembly)
{
    internal IFileProvider Files { get; } = new ManifestEmbeddedFileProvider(assembly);
}
