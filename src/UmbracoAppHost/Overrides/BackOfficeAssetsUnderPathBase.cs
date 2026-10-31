using Umbraco.Cms.Web.Common.Hosting;

namespace UmbracoAppHost.Overrides;

/// <summary>
/// Works around Umbraco 18.1.1 losing the backoffice's scripts and stylesheets when the site runs
/// under a path base (<c>UsePathBase</c>, or an IIS child application).
/// </summary>
/// <remarks>
/// Umbraco serves the cache-busted backoffice assets through a rewrite rule that matches
/// <c>{ApplicationVirtualPath}/umbraco/backoffice/{hash}/</c> against <c>Request.Path</c>. Under a
/// path base that prefix has already been moved into <c>Request.PathBase</c>, so the rule never
/// matches and every asset request returns 404. Putting the virtual directory back onto the path,
/// for just those hashed requests, lets Umbraco's cache-header middleware and rewrite rule see the
/// shape they expect. Unhashed <c>/umbraco/backoffice/...</c> requests, such as the login page's
/// stylesheet, are left alone because the static-file handler serves those directly. Does nothing
/// when no virtual path is configured, and becomes redundant once
/// <c>UseUmbracoBackOfficeRewrites</c> accounts for the path base upstream.
/// </remarks>
public static class BackOfficeAssetsUnderPathBase
{
    private static readonly PathString AssetsRoot = "/umbraco/backoffice";

    /// <summary>Register after <c>UsePathBase</c> and before <c>UseUmbraco</c>.</summary>
    public static IApplicationBuilder UseBackOfficeAssetsUnderPathBase(this IApplicationBuilder app)
    {
        var paths = app.ApplicationServices.GetRequiredService<IBackOfficePathGenerator>();
        PathString virtualDirectory = paths.BackOfficeVirtualDirectory;
        if (virtualDirectory == AssetsRoot)
            return app;

        PathString hashedAssets = AssetsRoot.Add("/" + paths.BackOfficeCacheBustHash);
        return app.Use((context, next) =>
        {
            if (context.Request.PathBase.HasValue
                && context.Request.Path.StartsWithSegments(hashedAssets, out PathString remaining))
                context.Request.Path = virtualDirectory.Add("/" + paths.BackOfficeCacheBustHash).Add(remaining);
            return next(context);
        });
    }
}
