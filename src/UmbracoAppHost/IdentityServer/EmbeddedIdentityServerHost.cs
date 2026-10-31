using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using UmbracoAppHost.Authentication;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace UmbracoAppHost.IdentityServer;

/// <summary>
/// An in-process host for IdentityServer.Core. Its services, authentication schemes, keys and
/// OpenIddict options are separate from Umbraco's; only protocol requests enter this pipeline.
/// </summary>
public sealed class EmbeddedIdentityServerHost : IDisposable, IAsyncDisposable
{
    private readonly ServiceProvider services;
    private readonly RequestDelegate pipeline;

    public EmbeddedIdentityServerHost(IServiceProvider parent, IOptions<EmbeddedIdentityServerSettings> settings,
        IOptions<IdentityServerSettings> site, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var embedded = settings.Value;
        var dataRoot = configuration["UmbracoAppHost:DataRoot"]
            ?? Path.Combine(environment.ContentRootPath, "App_Data", "UmbracoAppHost");
        var coreConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IdentityServer:PersistKeys"] = embedded.PersistKeys.ToString(),
            ["IdentityServer:DataPath"] = string.IsNullOrWhiteSpace(embedded.DataPath)
                ? Path.Combine(dataRoot, "keys", "identity-server")
                : Path.GetFullPath(embedded.DataPath, environment.ContentRootPath),
            ["IdentityServer:AccessTokenLifetime"] = embedded.AccessTokenLifetime?.ToString("c", CultureInfo.InvariantCulture),
            ["IdentityServer:IdentityTokenLifetime"] = embedded.IdentityTokenLifetime?.ToString("c", CultureInfo.InvariantCulture),
            ["IdentityServer:AuthorizationCodeLifetime"] = embedded.AuthorizationCodeLifetime?.ToString("c", CultureInfo.InvariantCulture),
        }.Concat(embedded.Groups.Select((pattern, index) =>
            new KeyValuePair<string, string?>($"IdentityServer:Groups:{index}", pattern)))).Build();

        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(coreConfiguration);
        collection.AddSingleton(environment);
        collection.AddSingleton<IHostEnvironment>(environment);
        collection.AddLogging();
        collection.AddSingleton(parent.GetRequiredService<ILoggerFactory>());
        collection.AddSingleton(parent.GetRequiredService<IHttpContextAccessor>());
        var integrations = parent.GetServices<IServerIntegratedAuth>().ToArray();
        foreach (var integration in integrations) collection.AddSingleton(integration);
        collection.AddSingleton(settings);
        collection.AddSingleton(site);
        collection.AddNegotiateOnce();
        collection.AddAuthentication(NegotiateDefaults.AuthenticationScheme);
        // Negotiate forwards to the web server's Windows scheme under IIS. That native handler
        // belongs to the outer host, so bridge to it using the original request services.
        foreach (var scheme in integrations.Where(integration => integration.IsEnabled)
                     .Select(integration => integration.AuthenticationScheme).Distinct(StringComparer.Ordinal))
            collection.AddAuthentication().AddScheme<AuthenticationSchemeOptions, IntegratedWindowsAuthenticationBridge>(scheme, _ => { });

        // Use the core's protocol registration directly: its full UseIdentityServer pipeline also
        // protects application routes, which belong to Umbraco here. No server code is copied.
        global::IdentityServer.IdentityServer.Add(collection, coreConfiguration);
        collection.AddOpenIddict().AddServer(options =>
        {
            // The standalone server accepts clients and callback hosts from its configuration.
            // This host retains exact callbacks, mandatory PKCE and the same-origin site client.
            options.Configure(server =>
            {
                var clientHandlers = server.Handlers.Where(handler => handler.Type == OpenIddictServerHandlerType.Custom
                    && (handler.ContextType == typeof(OpenIddictServerEvents.ValidateAuthorizationRequestContext)
                        || handler.ContextType == typeof(OpenIddictServerEvents.ValidateTokenRequestContext))).ToArray();
                if (clientHandlers.Count(handler => handler.ContextType == typeof(OpenIddictServerEvents.ValidateAuthorizationRequestContext)) != 1
                    || clientHandlers.Count(handler => handler.ContextType == typeof(OpenIddictServerEvents.ValidateTokenRequestContext)) != 1)
                    throw new InvalidOperationException("IdentityServer.Core's client validation handlers changed. Review the embedded client adapter before using this version.");
                foreach (var handler in clientHandlers) server.Handlers.Remove(handler);
                server.GrantTypes.Clear();
                server.GrantTypes.Add(GrantTypes.AuthorizationCode);
                server.ResponseTypes.Clear();
                server.ResponseTypes.Add(ResponseTypes.Code);
                server.Claims.UnionWith([Claims.Subject, ClaimTypes.Name, ClaimTypes.WindowsAccountName,
                    ClaimTypes.GivenName, ClaimTypes.Surname, ClaimTypes.Email, ClaimTypes.Role, ClaimTypes.HomePhone]);
            });
            options.RequireProofKeyForCodeExchange();
            options.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(handler =>
                handler.UseSingletonHandler<EmbeddedClientRules>());
            options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(handler =>
                handler.UseSingletonHandler<EmbeddedClientRules>());
            options.AddEventHandler<OpenIddictServerEvents.HandleAuthorizationRequestContext>(handler =>
                handler.UseSingletonHandler<EmbeddedWindowsSignInFallback>().SetOrder(int.MinValue + 100_000));
        });

        services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        try
        {
            _ = ServerOptions;
            var app = new ApplicationBuilder(services);
            app.UseAuthentication();
            app.Run(context => { context.Response.StatusCode = StatusCodes.Status404NotFound; return Task.CompletedTask; });
            pipeline = app.Build();
        }
        catch
        {
            services.Dispose();
            throw;
        }
    }

    /// <summary>Metadata for the site's OIDC client, obtained locally without trusting Host-header discovery.</summary>
    public OpenIddictServerOptions ServerOptions => services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;

    public async Task InvokeAsync(HttpContext context)
    {
        var originalServices = context.RequestServices;
        var originalFeature = context.Features.Get<EmbeddedIdentityServerRequest>();
        await using var scope = services.CreateAsyncScope();
        try
        {
            context.Features.Set(new EmbeddedIdentityServerRequest(originalServices));
            context.RequestServices = scope.ServiceProvider;
            await pipeline(context);
        }
        finally
        {
            context.RequestServices = originalServices;
            context.Features.Set(originalFeature);
        }
    }

    public void Dispose() => services.Dispose();
    public ValueTask DisposeAsync() => services.DisposeAsync();
}

internal sealed record EmbeddedIdentityServerRequest(IServiceProvider HostServices);
