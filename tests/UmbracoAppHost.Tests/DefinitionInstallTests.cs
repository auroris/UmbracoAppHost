using System.Xml.Linq;
using AppBlocks;
using AppBlocks.Documents;
using AppBlocks.InteractiveReports;
using AppBlocks.Setup;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using uSync.BackOffice;
using uSync.BackOffice.Models;
using uSync.BackOffice.Services;
using uSync.BackOffice.SyncHandlers.Models;
using UmbracoAppHost.Hosting;
using UmbracoAppHost.Testing;

namespace UmbracoAppHost.Tests;

/// <summary>Installing AppBlocks' definitions against a real uSync import, on first boot and afterwards.</summary>
[Collection("Umbraco hosts")]
public sealed class DefinitionInstallTests
{
    private static readonly Guid OrdersInteractive = Guid.Parse("dfa6b0e6-ff22-1969-c26c-6526f38715bb");
    private static readonly Guid ApplicationGrid = Guid.Parse("97b36051-4abe-e750-6667-9cb7ee6a9a99");

    private static IEnumerable<XElement> SchemaDefinitions(string kind) =>
        new[] { typeof(AppBlocksExtensions).Assembly, typeof(InteractiveReportsExtensions).Assembly, typeof(AppBlocksDocumentsExtensions).Assembly }
            .SelectMany(assembly => new AppBlocksSchema(assembly).Definitions(kind));

    /// <summary>Looks at the site when uSync reaches the first item it imports.</summary>
    private sealed class ImportProbe(IContentTypeService contentTypes, IDataTypeService dataTypes, ProbeResult result) : INotificationAsyncHandler<uSyncImportingItemNotification>
    {
        public async Task HandleAsync(uSyncImportingItemNotification notification, CancellationToken cancellationToken)
        {
            if (result.Seen) return;
            result.Seen = true;
            foreach (var definition in SchemaDefinitions("ContentTypes"))
                if (contentTypes.Get(Guid.Parse(definition.Attribute("Key")!.Value)) is null) result.Missing.Add(definition.Attribute("Alias")!.Value);
            foreach (var definition in SchemaDefinitions("DataTypes"))
                if (await dataTypes.GetAsync(Guid.Parse(definition.Attribute("Key")!.Value)) is null) result.Missing.Add(definition.Attribute("Alias")!.Value);
        }
    }

    private sealed class ProbeResult
    {
        public bool Seen { get; set; }
        public List<string> Missing { get; } = [];
    }

    [Theory]
    [InlineData("All")]
    [InlineData("Settings")]
    public async Task A_first_boot_installs_the_schemas_before_it_imports_anything(string group)
    {
        var probe = new ProbeResult();
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            FirstBootGroup = group,
            UseInstallSnapshot = false,
            ConfigureBuilder = builder =>
            {
                builder.Services.AddSingleton(probe);
                builder.Services.AddTransient<INotificationAsyncHandler<uSyncImportingItemNotification>, ImportProbe>();
            },
        });

        Assert.True(probe.Seen, "uSync's first boot did not import anything.");
        Assert.Empty(probe.Missing);

        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        foreach (var definition in SchemaDefinitions("ContentTypes"))
        {
            var type = types.Get(Guid.Parse(definition.Attribute("Key")!.Value));
            Assert.NotNull(type);
            var missing = definition.Element("GenericProperties")!.Elements("GenericProperty").Select(p => p.Element("Alias")!.Value)
                .Where(alias => !type.PropertyTypeExists(alias)).ToArray();
            Assert.True(missing.Length == 0, $"{type.Alias} lost {string.Join(", ", missing)}.");
        }
        // What the host keeps in the export is what was imported over the schema's.
        var grid = await services.GetRequiredService<IDataTypeService>().GetAsync(ApplicationGrid);
        Assert.Equal("Application: Application blocks", grid!.Name);
        Assert.Equal("OrdersPaperReport", types.Get("paperReport")!.DefaultTemplate?.Alias);
        Assert.Equal(new[] { "sqlDataSource", "reportDefinition", "listOfValues" }, types.Get("dataSources")!.AllowedContentTypes!.OrderBy(child => child.SortOrder).Select(child => child.Alias));

        var contents = services.GetRequiredService<IContentService>();
        if (group == "Settings")
        {
            Assert.Null(contents.GetById(OrdersInteractive));
            return;
        }
        await site.WaitForPublishedAsync(OrdersInteractive, TimeSpan.FromMinutes(2));
        Assert.Contains("\"paperReport\"", contents.GetById(OrdersInteractive)!.GetValue<string>("applicationBlocks"));
    }

    /// <summary>uSync logs the failure and carries on with the site, without importing content over types that are not there.</summary>
    [Fact]
    public async Task A_failed_installation_stops_the_first_boot_import()
    {
        var probe = new ProbeResult();
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            UseInstallSnapshot = false,
            // The started notification fails the same way, and its later handlers include the administrator's rename.
            WaitForAdministratorRename = false,
            ConfigureBuilder = builder =>
            {
                builder.Services.AddSingleton(probe);
                builder.Services.AddTransient<INotificationAsyncHandler<uSyncImportingItemNotification>, ImportProbe>();
                // This assembly embeds no schema, so reading its definitions fails the installation.
                builder.Services.AddSingleton(new AppBlocksSchema(typeof(DefinitionInstallTests).Assembly));
            },
        });
        Assert.False(probe.Seen, "The first boot imported although the definitions failed to install.");
        using var scope = site.CreateScope();
        Assert.Null(scope.ServiceProvider.GetRequiredService<IContentService>().GetById(OrdersInteractive));
        // uSync counts it as done, so the failure is the site's to repair: a reset, as for any failed first boot.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IKeyValueService>().GetValue(Constants.Conventions.Migrations.KeyValuePrefix + "uSync_FirstBoot"));
    }

    /// <summary>
    /// The import a person starts from the backoffice, over a working folder that still holds an older copy of AppBlocks'
    /// definitions: they stay as AppBlocks installed them, and the rest of the folder imports as usual.
    /// </summary>
    [Fact]
    public async Task An_import_cannot_put_a_stale_copy_of_an_AppBlocks_definition_back()
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions { Application = typeof(Program).Assembly });
        await site.WaitForPublishedAsync(OrdersInteractive, TimeSpan.FromMinutes(2));

        // The report block as the old export had it: no paper report, and a differently named heading.
        var stale = SchemaDefinitions("ContentTypes").Single(definition => definition.Attribute("Alias")!.Value == "appReport");
        stale.Element("Info")!.Element("Name")!.Value = "Stale report block";
        stale.Element("GenericProperties")!.Elements("GenericProperty").Single(p => p.Element("Alias")!.Value == "paperReport").Remove();
        var folder = Path.Combine(site.USyncFolder, "ContentTypes");
        Directory.CreateDirectory(folder);
        stale.Save(Path.Combine(folder, "appreport.config"));
        // A file the site added imports as it always did.
        var kept = XElement.Load(Path.Combine(AppContext.BaseDirectory, "uSync", "ContentTypes", "navigationmenu.config"));
        var name = "Site renamed menu";
        kept.Element("Info")!.Element("Name")!.Value = name;
        kept.Save(Path.Combine(folder, "navigationmenu.config"));

        using var scope = site.CreateScope();
        var services = scope.ServiceProvider;
        // As the backoffice's Import button does it: the start of the process, then each handler in turn.
        var service = services.GetRequiredService<ISyncActionService>();
        await service.StartProcessAsync(HandlerActions.Import);
        var actions = new List<uSyncAction>();
        foreach (var handler in service.GetActionHandlers(HandlerActions.Import, new uSyncOptions { Group = "Settings" }))
            actions.AddRange((await service.ImportHandlerAsync(new SyncActionOptions { Handler = handler.Alias, Force = true, Group = "Settings", Folders = [site.USyncFolder] }, null)).Actions);

        var cancelled = Assert.Single(actions, action => action.FileName?.EndsWith("appreport.config", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains("AppBlocks installs this definition", cancelled.Message);
        var types = services.GetRequiredService<IContentTypeService>();
        var report = types.Get("appReport")!;
        Assert.Equal("Interactive Report", report.Name);
        Assert.Contains(report.PropertyTypes, p => p.Alias == "paperReport");
        Assert.Equal(name, types.Get("navigationMenu")!.Name);
    }
}
