using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Web.Common.Hosting;

namespace UmbracoAppHost.Overrides;

/// <summary>
/// Works around Umbraco 18.1.1 emitting a root-absolute import map for the backoffice when an
/// application virtual path is configured.
/// </summary>
/// <remarks>
/// The backoffice page carries an import map whose entries are root-absolute: the core ones point
/// at <c>/umbraco/backoffice/...</c> and package ones at <c>/App_Plugins/...</c>. When rendering
/// it, Umbraco replaces the backoffice virtual directory with the cache-busted assets path, but
/// under a virtual path that directory is <c>/portal/umbraco/backoffice</c>, which never occurs in
/// the map, so the core entries stay unhashed and every entry is fetched from the root. Prefixing
/// each root-absolute entry with the virtual path before Umbraco renders the map lets its own
/// replacement add the hash and keeps package modules on the same URL the SPA uses for their
/// manifests, so the browser evaluates each module once. Does nothing when no virtual path is
/// configured.
/// </remarks>
public sealed class BackOfficeImportMapUnderVirtualPathComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        var services = builder.Services;
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IPackageManifestService))
            ?? throw new InvalidOperationException(
                $"{nameof(IPackageManifestService)} is not registered; update {nameof(BackOfficeImportMapUnderVirtualPathComposer)} for this Umbraco version.");

        services.Remove(inner);
        services.Add(new ServiceDescriptor(
            typeof(IPackageManifestService),
            provider => new VirtualPathImportMapService(Resolve(provider, inner), provider.GetRequiredService<IBackOfficePathGenerator>()),
            inner.Lifetime));
    }

    private static IPackageManifestService Resolve(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IPackageManifestService instance)
            return instance;
        if (descriptor.ImplementationFactory is not null)
            return (IPackageManifestService)descriptor.ImplementationFactory(provider);
        return (IPackageManifestService)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }

    private sealed class VirtualPathImportMapService(IPackageManifestService inner, IBackOfficePathGenerator paths) : IPackageManifestService
    {
        private const string BackOfficeAssets = "/umbraco/backoffice";

        public Task<IEnumerable<PackageManifest>> GetAllPackageManifestsAsync() => inner.GetAllPackageManifestsAsync();

        public Task<IEnumerable<PackageManifest>> GetPublicPackageManifestsAsync() => inner.GetPublicPackageManifestsAsync();

        public Task<IEnumerable<PackageManifest>> GetPrivatePackageManifestsAsync() => inner.GetPrivatePackageManifestsAsync();

        public async Task<PackageManifestImportmap> GetPackageManifestImportmapAsync()
        {
            PackageManifestImportmap map = await inner.GetPackageManifestImportmapAsync();
            var virtualDirectory = paths.BackOfficeVirtualDirectory.TrimEnd('/');
            if (virtualDirectory == BackOfficeAssets)
                return map;

            // "/portal/umbraco/backoffice" -> "/portal"
            var virtualPath = virtualDirectory[..^BackOfficeAssets.Length];
            return new PackageManifestImportmap
            {
                Imports = Rewrite(map.Imports, virtualPath),
                Scopes = map.Scopes?.ToDictionary(scope => Rewrite(scope.Key, virtualPath), scope => Rewrite(scope.Value, virtualPath)),
            };
        }

        private static Dictionary<string, string> Rewrite(Dictionary<string, string> entries, string virtualPath)
            => entries.ToDictionary(entry => entry.Key, entry => Rewrite(entry.Value, virtualPath));

        private static string Rewrite(string value, string virtualPath)
        {
            var isRootAbsolute = value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal);
            var alreadyPrefixed = value.StartsWith(virtualPath + "/", StringComparison.Ordinal);
            return isRootAbsolute && !alreadyPrefixed ? virtualPath + value : value;
        }
    }
}
