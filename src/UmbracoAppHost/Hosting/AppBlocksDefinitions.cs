using AppBlocks.Setup;
using Umbraco.Cms.Core.Events;
using uSync.BackOffice;

namespace UmbracoAppHost.Hosting;

/// <summary>Which definitions belong to AppBlocks and its companions rather than to this library's saved export.</summary>
public sealed class AppBlocksDefinitions(IEnumerable<AppBlocksSchema> schemas)
{
    /// <summary>
    /// The definitions the saved export deliberately keeps although a schema ships the same key. Each one adds something
    /// only the host can say, and carries everything the schema's has, which the ownership tests check. Everything else
    /// a schema ships is AppBlocks' alone and must not be in the export.
    /// </summary>
    public static readonly IReadOnlyDictionary<Guid, string> HostOwned = new Dictionary<Guid, string>
    {
        // AppBlocks ships this as the Forms container's grid. The host also makes it the page grid of Application pages,
        // which offers the blocks a plain AppBlocks site adds to its own grid: Interactive Report, HTML, CSS and JavaScript.
        [Guid.Parse("97b36051-4abe-e750-6667-9cb7ee6a9a99")] = "DataTypes/ApplicationApplicationBlocks.config",
        // Documents' schema cannot name site templates. The host gives the type a default template, its place in the tree
        // and its name; PaperReportTemplates rewrites its allowed templates, which is why SiteTools.ps1 lists it as rewritten.
        [Guid.Parse("996f23f8-7dfc-3686-50ed-a9b95c849ef5")] = "ContentTypes/paperreport.config",
    };

    private readonly Lazy<HashSet<Guid>> keys = new(() => [.. schemas.DistinctBy(schema => schema.Assembly)
        .SelectMany(schema => schema.Definitions("ContentTypes").Concat(schema.Definitions("DataTypes")))
        .Select(definition => Guid.Parse(definition.Attribute("Key")!.Value))]);

    /// <summary>Whether a uSync item with this key is a definition that only a schema may install or change.</summary>
    public bool Owns(Guid key) => keys.Value.Contains(key) && !HostOwned.ContainsKey(key);
}

/// <summary>
/// AppBlocks installs its definitions through Umbraco's tracked package migrations. A uSync import may run before those
/// migrations, so before any import this ensures missing definitions exist, respecting Umbraco's schema migration setting.
/// During an import it refuses a stale copy of a
/// definition that a working folder still holds, which would otherwise replace AppBlocks' with whatever that file says.
/// </summary>
public sealed class AppBlocksDefinitionImport(SchemaInstaller installer, AppBlocksDefinitions definitions, ILogger<AppBlocksDefinitionImport> logger)
    : INotificationAsyncHandler<uSyncImportStartingNotification>, INotificationAsyncHandler<uSyncImportingItemNotification>
{
    /// <summary>
    /// A failure stops the import, because content imported over missing types is lost. uSync logs it as a failed first
    /// boot and records that first boot as done, as it does any failure, so a new site that fails here needs a reset.
    /// </summary>
    public Task HandleAsync(uSyncImportStartingNotification notification, CancellationToken cancellationToken)
        => installer.EnsureAsync(cancellationToken);

    public Task HandleAsync(uSyncImportingItemNotification notification, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(notification.Item.Attribute("Key")?.Value, out var key) && definitions.Owns(key))
        {
            notification.Cancel = true;
            notification.Message = "AppBlocks installs this definition, so uSync does not import it.";
            logger.LogInformation("Skipped the uSync copy of {Alias} ({Key}), which AppBlocks installs.", notification.Item.Attribute("Alias")?.Value, key);
        }
        return Task.CompletedTask;
    }
}
