using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace UmbracoAppHost.Tests;

/// <summary>
/// Runs a middleware ahead of the whole pipeline, where a site's own middleware added before
/// <c>UseUmbracoAppHostAsync</c> runs; <see cref="TestSiteOptions.ConfigureApp"/> only adds after it.
/// </summary>
internal sealed class FirstMiddleware(Func<HttpContext, RequestDelegate, Task> middleware) : IStartupFilter
{
    public static void Add(WebApplicationBuilder builder, Func<HttpContext, RequestDelegate, Task> middleware)
        => builder.Services.AddSingleton<IStartupFilter>(new FirstMiddleware(middleware));

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(middleware);
        next(app);
    };
}
