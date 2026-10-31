using System.Text;
using Umbraco.Cms.Web.Common.Hosting;

namespace UmbracoAppHost.Overrides;

/// <summary>
/// Works around the Umbraco 18.1.1 backoffice SPA assuming it lives at <c>{origin}/umbraco</c>.
/// </summary>
/// <remarks>
/// The SPA builds its management API, OAuth and post-login URLs from <c>window.location.origin</c>
/// plus a hard-coded <c>/umbraco</c>, so under a path base every call leaves the prefix. The
/// <c>umb-app</c> element accepts <c>server-url</c> and <c>backoffice-path</c> attributes that
/// change both, but the page that renders it is a precompiled view in Umbraco.Cms.StaticAssets and
/// this project's Razor setup does not override those, so the attributes are added to the served
/// HTML instead. Only GET responses of type text/html under the backoffice path are buffered, and
/// nothing happens when no virtual path is configured. The OAuth callback paths are prefixed in
/// Program.cs so OpenIddict accepts the redirect URI the SPA now sends.
/// </remarks>
public static class BackOfficeAppUnderVirtualPath
{
    private static readonly PathString BackOfficeRoot = "/umbraco";
    private static readonly PathString[] Untouched = ["/umbraco/backoffice", "/umbraco/management", "/umbraco/delivery"];

    /// <summary>Register after <c>UsePathBase</c> and before <c>UseUmbraco</c>.</summary>
    public static IApplicationBuilder UseBackOfficeAppUnderVirtualPath(this IApplicationBuilder app)
    {
        var paths = app.ApplicationServices.GetRequiredService<IBackOfficePathGenerator>();
        var backOfficePath = paths.BackOfficePath;
        if (backOfficePath == BackOfficeRoot)
            return app;

        return app.Use(async (context, next) =>
        {
            if (!ShouldPatch(context.Request))
            {
                await next(context);
                return;
            }

            var originalBody = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await next(context);

                byte[] bytes = buffer.ToArray();
                if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var serverUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
                    var html = Encoding.UTF8.GetString(bytes).Replace(
                        "<umb-app ",
                        $"<umb-app server-url=\"{serverUrl}\" backoffice-path=\"{backOfficePath}\" ",
                        StringComparison.Ordinal);
                    bytes = Encoding.UTF8.GetBytes(html);
                    if (context.Response.ContentLength is not null)
                        context.Response.ContentLength = bytes.Length;
                }

                await originalBody.WriteAsync(bytes, context.RequestAborted);
            }
            finally
            {
                context.Response.Body = originalBody;
            }
        });
    }

    private static bool ShouldPatch(HttpRequest request)
    {
        if (!request.PathBase.HasValue || !HttpMethods.IsGet(request.Method))
            return false;
        if (!request.Path.StartsWithSegments(BackOfficeRoot))
            return false;
        foreach (PathString prefix in Untouched)
        {
            if (request.Path.StartsWithSegments(prefix))
                return false;
        }

        return true;
    }
}
