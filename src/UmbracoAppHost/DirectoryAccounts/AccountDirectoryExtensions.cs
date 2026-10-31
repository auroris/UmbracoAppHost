using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>
/// Registers the directory that backoffice users and members are created from, and the service and
/// management API that create them. Settings come from the <c>Directory</c> section (the LDAP roots
/// and limits of the local lookup, and <see cref="AccountDirectorySettings.UseLocalAccounts"/>) and
/// from <c>IdentityServer</c>, whose authority decides between this site's own directory and an
/// external identity server's.
/// </summary>
public static class AccountDirectoryExtensions
{
    /// <summary>
    /// Adds <see cref="IAccountDirectory"/> and <see cref="DirectoryAccountService"/>. Nothing touches
    /// a directory until someone searches it, so a directory that is down or misconfigured never
    /// stops the site from starting; the search reports it instead. Called by
    /// <see cref="UmbracoAppHostExtensions"/> after <see cref="SsoExtensions.AddSingleSignOn"/>, whose
    /// account services and provider settings it relies on.
    /// </summary>
    public static IUmbracoBuilder AddAccountDirectory(this IUmbracoBuilder builder, IConfiguration configuration)
    {
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IAccountDirectory)))
            return builder;

        builder.Services.Configure<DirectoryOptions>(configuration.GetSection(DirectoryOptions.SectionName));
        builder.Services.Configure<AccountDirectorySettings>(configuration.GetSection(AccountDirectorySettings.SectionName));
        // A host may register a lookup of its own first, such as a test double.
        builder.Services.TryAddSingleton<IDirectoryLookup>(services =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Active Directory lookups in process need Windows.");
            return ActivatorUtilities.CreateInstance<ActiveDirectoryLookup>(services);
        });
        builder.Services.AddHttpClient(IdentityServerAccountDirectory.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(services =>
                IdentityServerAccountDirectory.CreateHandler(services.GetRequiredService<IOptions<IdentityServerSettings>>().Value));
        builder.Services.AddSingleton<IAccountDirectory>(CreateDirectory);
        builder.Services.AddScoped<DirectoryAccountService>();
        builder.AddNotificationHandler<UmbracoApplicationStartedNotification, AccountDirectoryStartupLog>();
        return builder;
    }

    /// <summary>The source chosen at startup, described for the log and for whoever reads it.</summary>
    public static string Describe(AccountDirectorySource source, AccountDirectorySettings directory, IdentityServerSettings identityServer, DirectoryOptions lookup)
        => source switch
        {
            AccountDirectorySource.LocalAccounts => directory.UseLocalAccounts == true
                ? $"the local accounts of {Environment.MachineName}, as Directory:UseLocalAccounts asks"
                : $"the local accounts of {Environment.MachineName}, which is not in a domain (set Directory:UseLocalAccounts to false to query Active Directory)",
            AccountDirectorySource.IdentityServer => $"the directory API of the identity server at {identityServer.Authority}, "
                + (string.IsNullOrWhiteSpace(identityServer.DirectoryApiKey)
                    ? $"as {Environment.UserDomainName}\\{Environment.UserName}"
                    : "with the key in IdentityServer:DirectoryApiKey"),
            _ => $"Active Directory, queried as {Environment.UserDomainName}\\{Environment.UserName} "
                + (string.IsNullOrWhiteSpace(lookup.UserRootGlobal) ? "across the whole domain" : $"under {lookup.UserRootGlobal}"),
        };

    private static IAccountDirectory CreateDirectory(IServiceProvider services)
    {
        var source = AccountDirectorySettings.Choose(
            services.GetRequiredService<IOptions<AccountDirectorySettings>>().Value,
            services.GetRequiredService<IOptions<IdentityServerSettings>>().Value);
        if (source == AccountDirectorySource.IdentityServer)
            return ActivatorUtilities.CreateInstance<IdentityServerAccountDirectory>(services);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Directory lookups in process need Windows; set IdentityServer:Authority to use an identity server's directory instead.");
        return source == AccountDirectorySource.LocalAccounts
            ? new LocalAccountDirectory()
            : ActivatorUtilities.CreateInstance<ActiveDirectoryAccountDirectory>(services);
    }
}

/// <summary>Says at startup where accounts will be created from, without asking that directory anything.</summary>
public sealed class AccountDirectoryStartupLog(
    IOptions<AccountDirectorySettings> directory,
    IOptions<IdentityServerSettings> identityServer,
    IOptions<DirectoryOptions> lookup,
    ILogger<AccountDirectoryStartupLog> logger) : INotificationHandler<UmbracoApplicationStartedNotification>
{
    public void Handle(UmbracoApplicationStartedNotification notification)
    {
        var source = AccountDirectorySettings.Choose(directory.Value, identityServer.Value);
        logger.LogInformation("Accounts are created from {Directory}.",
            AccountDirectoryExtensions.Describe(source, directory.Value, identityServer.Value, lookup.Value));
    }
}
