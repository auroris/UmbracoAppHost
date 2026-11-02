using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using Umbraco.Extensions;

namespace UmbracoAppHost.Testing;

/// <summary>The pieces a host test process needs regardless of how it boots the site.</summary>
public static class UmbracoTestSupport
{
    /// <summary>Isolated test hosts need no server election, whose writes can race their SQLite startup import.</summary>
    public static void UseSingleServer(IServiceCollection services)
        => services.Replace(ServiceDescriptor.Singleton<IServerRoleAccessor, SingleServerRoleAccessor>());

    /// <summary>
    /// Waits for the unattended install or upgrade to finish, then for the runtime to reach
    /// <see cref="RuntimeLevel.Run"/> and content routing to be ready. Call it after the host has started.
    /// </summary>
    /// <param name="timeout">How long each of the two waits may take; 60 seconds unless a fixture allows more.</param>
    public static async Task WaitForInstallAsync(IServiceProvider services, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(60);
        // RuntimeLevel.Run and routing readiness precede the application-started handlers.
        // Await their actual completion before writes or shutdown can race SQLite startup scopes.
        // Umbraco's upgrade service is internal, but exposes BackgroundService.ExecuteTask.
        var upgrade = services.GetServices<IHostedService>().OfType<BackgroundService>()
            .SingleOrDefault(service => service.GetType().Name == "UnattendedUpgradeBackgroundService")
            ?? throw new TestSiteException("Umbraco's UnattendedUpgradeBackgroundService is not registered, so the unattended install cannot be awaited.");
        try { if (upgrade.ExecuteTask is { } task) await task.WaitAsync(limit); }
        catch (TimeoutException) { throw new TestSiteException($"The unattended install was still running after {limit}."); }

        var runtime = services.GetRequiredService<IRuntimeState>();
        var readiness = services.GetRequiredService<IContentRoutingReadiness>();
        var waited = Stopwatch.StartNew();
        while ((runtime.Level != RuntimeLevel.Run || !readiness.IsReady)
            && runtime.Level != RuntimeLevel.BootFailed && waited.Elapsed < limit) await Task.Delay(100);
        if (runtime.Level != RuntimeLevel.Run)
            throw new TestSiteException($"Umbraco is at runtime level {runtime.Level}, not Run, after {waited.Elapsed}.", runtime.BootFailedException);
        if (!readiness.IsReady) throw new TestSiteException($"Umbraco did not finish initializing its content caches within {limit}.");
    }

    /// <summary>
    /// Umbraco caches the backoffice path in a static field the first time any site asks for it, so
    /// in a test process that hosts sites under different virtual paths, later sites inherit the
    /// first one's. A root site that inherits <c>/wing/umbraco</c> does not recognize its own
    /// backoffice requests and never registers the backoffice's OpenID Connect client, which the
    /// token sign-in needs. A deployed site runs one host per process and is not affected.
    /// </summary>
    public static void ResetBackOfficePath()
    {
        var cached = typeof(Umbraco.Cms.Core.Hosting.HostingEnvironmentExtensions).GetField("_backOfficePath", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new TestSiteException("Umbraco no longer caches the backoffice path in HostingEnvironmentExtensions._backOfficePath.");
        cached.SetValue(null, null);
    }

    /// <summary>
    /// Forgets the services Umbraco's published-content extensions cached from the first host that
    /// rendered a page, so a later host in the same process does not render with a disposed one's.
    /// Umbraco exposes this reset internally to its own tests.
    /// </summary>
    public static void ResetPublishedContentServices()
    {
        var reset = typeof(FriendlyPublishedContentExtensions).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new TestSiteException("Umbraco no longer has FriendlyPublishedContentExtensions.Reset.");
        reset.Invoke(null, null);
    }
}
