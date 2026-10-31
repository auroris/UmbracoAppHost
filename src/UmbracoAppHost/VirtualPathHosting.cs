using Umbraco.Cms.Core.Configuration.Models;

namespace UmbracoAppHost;

/// <summary>
/// Hosts the whole site, backoffice included, under <c>Umbraco:CMS:Hosting:ApplicationVirtualPath</c>
/// (for example <c>/portal</c>).
/// </summary>
/// <remarks>
/// Umbraco strips the prefix from incoming URLs before content routing and prepends it to the links
/// it generates. The site also applies it as the path base, which strips the prefix when the request
/// still carries it and is a no-op when the host has already moved it into the path base, as an IIS
/// child application does, and prefixes the three <c>Umbraco:CMS:Security:AuthorizeCallback*PathName</c>
/// settings so OpenIddict registers the prefixed OAuth callback. One setting covers both hosting
/// styles. Report links get the prefix in AppBlocks.InteractiveReports, and
/// <c>Overrides/</c> covers what Umbraco 18.1.1 still gets wrong under a virtual path.
/// </remarks>
public static class VirtualPathHosting
{
    public const string SettingKey = "Umbraco:CMS:Hosting:ApplicationVirtualPath";

    /// <summary>The configured prefix, such as <c>/portal</c>, or null when the site is at the root.</summary>
    public static string? Prefix(IConfiguration configuration)
    {
        var virtualPath = configuration[SettingKey]?.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(virtualPath) ? null : "/" + virtualPath;
    }

    public static IServiceCollection AddVirtualPathHosting(this IServiceCollection services, IConfiguration configuration)
    {
        if (Prefix(configuration) is not { } prefix)
            return services;

        services.PostConfigure<SecuritySettings>(security =>
        {
            security.AuthorizeCallbackPathName = WithPrefix(security.AuthorizeCallbackPathName);
            security.AuthorizeCallbackLogoutPathName = WithPrefix(security.AuthorizeCallbackLogoutPathName);
            security.AuthorizeCallbackErrorPathName = WithPrefix(security.AuthorizeCallbackErrorPathName);
            string WithPrefix(string path) => path.StartsWith(prefix + "/", StringComparison.Ordinal) ? path : prefix + path;
        });
        return services;
    }

    public static WebApplication UseVirtualPathBase(this WebApplication app)
    {
        if (Prefix(app.Configuration) is { } prefix)
            app.UsePathBase(prefix);
        return app;
    }
}
