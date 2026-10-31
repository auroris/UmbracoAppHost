using AppBlocks;
using AppBlocks.Connections;
using AppBlocks.InteractiveReports;
using AppBlocks.Documents;
using InteractiveReport.Client.FileDownload;
using InteractiveReport.Client.Json;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using UmbracoAppHost.Authentication;
using UmbracoAppHost.DirectoryAccounts;
using UmbracoAppHost.Overrides;
using AppBlocks.InteractiveReports.Reports;
using UmbracoAppHost.Reports;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Notifications;
using UmbracoAppHost.Application;
using AppBlocks.Attachments;
using AppBlocks.DataSources;
using AppBlocks.Documents.PaperReports;
using AppBlocks.Schedules;
using AppBlocks.Tabular;
using UmbracoAppHost.Hosting;
using uSync.BackOffice;
using uSync.BackOffice.Configuration;

namespace UmbracoAppHost;

/// <summary>
/// The two calls a site makes to get everything the base provides: <see cref="AddUmbracoAppHost"/>
/// while services are registered and <see cref="UseUmbracoAppHostAsync"/> once the application is built.
/// </summary>
public static class UmbracoAppHostExtensions
{
    /// <summary>
    /// Initializes an independent site with the shared services, views and starter content, and the samples unless
    /// <c>UmbracoAppHost:Samples</c> is false.
    /// </summary>
    public static WebApplicationBuilder AddUmbracoAppHost(
        this WebApplicationBuilder builder, Action<AppBlocksBuilder>? configureConnections = null)
    {
        InstanceConfiguration.Apply(builder);
        builder.WebHost.UseStaticWebAssets();
        var samples = builder.Configuration.GetValue(SampleContent.Setting, true);
        if (samples) builder.Services.AddScoped<IFormResource, OrdersFormResource>();
        builder.CreateUmbracoBuilder()
            .AddBackOffice()
            .AddWebsite()
            .AddComposers()
            .AddUmbracoAppHost(builder.Configuration, connections =>
            {
                if (samples) connections.AddSampleOrders(builder.Services);
                configureConnections?.Invoke(connections);
            })
            .Build();
        return builder;
    }

    /// <summary>
    /// <see cref="AddUmbracoAppHost(WebApplicationBuilder, Action{AppBlocksBuilder}?)"/> with the host's own
    /// registrations: <typeparamref name="THost"/>'s services first, then its connections and
    /// <paramref name="configureConnections"/>.
    /// </summary>
    public static WebApplicationBuilder AddUmbracoAppHost<THost>(
        this WebApplicationBuilder builder, Action<AppBlocksBuilder>? configureConnections = null) where THost : IUmbracoAppHost
    {
        THost.ConfigureServices(builder);
        return builder.AddUmbracoAppHost(connections =>
        {
            THost.ConfigureConnections(builder, connections);
            configureConnections?.Invoke(connections);
        });
    }

    /// <summary>
    /// Adds the shared services: single sign-on and the account policy, accounts created from the
    /// directory, Interactive Reports with Umbraco-backed authorization and the built-in audit
    /// reports, configured accounts, Debug backoffice recovery, and virtual-path
    /// hosting.
    /// On first boot the working uSync folder receives the library's export, without its sample content when
    /// <c>UmbracoAppHost:Samples</c> is false, then the host's own export from <c>UmbracoAppHost:HostExport</c>.
    /// Call it after <c>AddComposers()</c> so account provisioning runs after uSync's startup import.
    /// </summary>
    /// <param name="configureConnections">Registers the site's own connections, for example a sample database.</param>
    public static IUmbracoBuilder AddUmbracoAppHost(
        this IUmbracoBuilder builder,
        IConfiguration configuration,
        Action<AppBlocksBuilder>? configureConnections = null)
    {
        SharedViews.Configure(builder.Services);
        // Umbraco initializes publication state before uSync imports, so installation needs a
        // default language. Further languages and their settings come from the saved export.
        builder.Services.Configure<InstallDefaultDataSettings>(Umbraco.Cms.Core.Constants.Configuration.NamedOptions.InstallDefaultData.Languages,
            settings =>
            {
                if (configuration.GetSection("Umbraco:CMS:InstallDefaultData:Languages").Exists()) return;
                settings.InstallData = InstallDefaultDataOption.Values;
                settings.Values = new HashSet<string> { "en" };
            });
        builder.Services.AddOptions<uSyncSettings>().Configure<IWebHostEnvironment, ILoggerFactory, IEnumerable<UmbracoAppHost.Themes.ThemeLibrary>>((settings, environment, loggers, libraries) =>
        {
            settings.ImportOnFirstBoot = configuration.GetValue("uSync:Settings:ImportOnFirstBoot", true);
            settings.FirstBootGroup = configuration["uSync:Settings:FirstBootGroup"] ?? "All";
            var working = Path.GetFullPath(settings.RootFolder, environment.ContentRootPath);
            USyncFiles.EnsureWorkingCopy(working, configuration.GetValue(SampleContent.Setting, true),
                USyncFiles.HostExport(configuration, environment.ContentRootPath, working), loggers.CreateLogger(typeof(USyncFiles)), libraries);
            SharedViews.EnsureTemplates(environment, loggers.CreateLogger(typeof(SharedViews)), libraries);
        });
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(Umbraco.Cms.Web.Common.Controllers.RenderController).Assembly)
            .AddApplicationPart(typeof(UmbracoAppHostExtensions).Assembly);
        builder.Services.AddScoped<ReportAccessPolicy>();
        builder.Services.Replace(ServiceDescriptor.Scoped<IReportAccessPolicy>(services => services.GetRequiredService<ReportAccessPolicy>()));
        builder
            .AddSingleSignOn(configuration)
            .AddAccountDirectory(configuration)
            .AddAppBlocksInteractiveReports()
            // After the companion, so a report seed that a callback configures comes after Interactive Reports' own configuration.
            .AddAppBlocks(configureConnections)
            .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, ConfiguredAccounts>()
            // The configured groups exist before uSync's first-boot import resolves the export's group pickers.
            .AddNotificationAsyncHandler<uSyncImportStartingNotification, ConfiguredAccounts>()
            // AppBlocks' definitions exist before uSync imports what uses them, and only AppBlocks changes them.
            .AddNotificationAsyncHandler<uSyncImportStartingNotification, AppBlocksDefinitionImport>()
            .AddNotificationAsyncHandler<uSyncImportingItemNotification, AppBlocksDefinitionImport>();
        builder.Services.AddSingleton<AppBlocksDefinitions>();

        builder.AddAppBlocksDocuments();
        builder.Services.Replace(ServiceDescriptor.Scoped<IDocumentPresentationProvider, UmbracoAppHost.Themes.AppHostDocumentPresentation>());
        builder.Services.AddSingleton<UmbracoAppHost.Themes.SiteThemeCatalog>();
        // The authored menu with its visitor tokens replaced, once per request.
        builder.Services.AddScoped<UmbracoAppHost.Website.SiteMenu>();
        builder.AddNotificationAsyncHandler<RoutingRequestNotification, UmbracoAppHost.Website.WebsiteTemplateInheritance>();

        // Use separate usernames for local login, including hosts using the IUmbracoBuilder entry point.
        builder.Services.Configure<SecuritySettings>(security =>
            security.UsernameIsEmail = configuration.GetValue("Umbraco:CMS:Security:UsernameIsEmail", false));

        // Explicit Debug recovery bypasses password, lockout and two-factor challenges only
        // during backoffice password sign-in. Normal flows retain native authentication.
        builder.Services.AddUnique<IBackOfficeUserPasswordChecker, DevPasswordChecker>();
        builder.Services.AddScoped<DebugBackOfficeSignInManager>();
        builder.Services.Replace(ServiceDescriptor.Scoped<Microsoft.AspNetCore.Identity.SignInManager<BackOfficeIdentityUser>>(services => services.GetRequiredService<DebugBackOfficeSignInManager>()));
        builder.Services.Replace(ServiceDescriptor.Scoped<Umbraco.Cms.Api.Management.Security.BackOfficeSignInManager>(services => services.GetRequiredService<DebugBackOfficeSignInManager>()));
        builder.Services.Replace(ServiceDescriptor.Scoped<Umbraco.Cms.Web.Common.Security.IBackOfficeSignInManager>(services => services.GetRequiredService<DebugBackOfficeSignInManager>()));
        builder.Services.Replace(ServiceDescriptor.Scoped<ICoreBackOfficeSignInManager>(services => services.GetRequiredService<DebugBackOfficeSignInManager>()));
        builder.Services.AddSingleton<ConfiguredAdminPassword>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ConfiguredAdminPassword>());
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, ConfiguredAdminPasswordStartup>();
        builder.Services.AddVirtualPathHosting(configuration);
        return builder;
    }

    /// <summary>
    /// Boots Umbraco and builds the pipeline: the virtual path base, the workarounds in
    /// <c>Overrides/</c>, the backoffice and website middleware with report authentication, and the
    /// report endpoints under <c>/api/reports</c> and <c>/api/download</c>, and the form, paper report and
    /// attachment endpoints. Call it after
    /// <c>Build()</c> and before <c>RunAsync()</c>. The two callbacks add a site's own middleware
    /// and endpoints inside Umbraco's pipeline.
    /// </summary>
    public static async Task<WebApplication> UseUmbracoAppHostAsync(
        this WebApplication app,
        Action<IUmbracoApplicationBuilderContext>? configureMiddleware = null,
        Action<IUmbracoEndpointBuilderContext>? configureEndpoints = null)
    {
        if (app.Services.GetService<AppHostInstance>() is { } instance)
            app.Logger.LogInformation("Site data: {DataRoot}. Initial administrator credentials: {SettingsPath}",
                instance.Root, instance.SettingsPath);
        app.UseVirtualPathBase();
        await app.BootUmbracoAsync();

        // Umbraco 18.1.1 neither finds its backoffice assets nor keeps the SPA inside the prefix
        // under a path base; see Overrides/.
        app.UseBackOfficeAssetsUnderPathBase();
        app.UseBackOfficeAppUnderVirtualPath();

        app.UseUmbraco()
            .WithMiddleware(u =>
            {
                u.UseBackOffice();
                u.UseWebsite();
                configureMiddleware?.Invoke(u);
            })
            .WithEndpoints(u =>
            {
                u.UseBackOfficeEndpoints();
                u.UseWebsiteEndpoints();
                configureEndpoints?.Invoke(u);
            });

        return app;
    }

    /// <summary><see cref="UseUmbracoAppHostAsync"/> with <typeparamref name="THost"/>'s middleware and endpoints.</summary>
    public static Task<WebApplication> UseUmbracoAppHostAsync<THost>(this WebApplication app) where THost : IUmbracoAppHost
        => app.UseUmbracoAppHostAsync(THost.ConfigureMiddleware, THost.ConfigureEndpoints);
}
