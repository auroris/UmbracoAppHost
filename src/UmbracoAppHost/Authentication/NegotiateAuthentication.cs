using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UmbracoAppHost.IdentityServer;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Registers the Negotiate scheme (Kerberos and NTLM) used by the embedded identity server.
/// Repeated calls leave the existing registration in place.
/// </summary>
public static class NegotiateAuthentication
{
    /// <summary>Adds <see cref="NegotiateDefaults.AuthenticationScheme"/> unless an earlier call already did.</summary>
    public static IServiceCollection AddNegotiateOnce(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(NegotiateRegistered)))
            return services;
        services.AddSingleton<NegotiateRegistered>();
        services.AddAuthentication().AddNegotiate();
        services.TryAddSingleton<WindowsAuthenticationAvailability>();
        services.AddTransient<UnavailableWindowsAuthenticationHandler>();
        // In IIS with Windows Authentication switched off the handler's options throw when they are
        // built, which the handler's startup filter does as the site starts and every request would do
        // again (see WindowsAuthenticationAvailability). Wrap that post-configuration to skip the check
        // when Windows authentication is unavailable, and answer "not authenticated" there instead of
        // running the handler, so the site starts, keeps serving and the sign-in flows fall back.
        var framework = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IPostConfigureOptions<NegotiateOptions>)
            && descriptor.ImplementationType is { } type && type.Assembly == typeof(NegotiateOptions).Assembly);
        if (framework is not null)
        {
            services.Remove(framework);
            services.AddSingleton<IPostConfigureOptions<NegotiateOptions>>(provider => new TolerantNegotiateOptions(
                provider.GetRequiredService<WindowsAuthenticationAvailability>(),
                (IPostConfigureOptions<NegotiateOptions>)ActivatorUtilities.CreateInstance(provider, framework.ImplementationType!)));
        }
        services.AddOptions<AuthenticationOptions>().PostConfigure<WindowsAuthenticationAvailability>((options, windows) =>
        {
            if (windows.IsAvailable) return;
            foreach (var scheme in options.Schemes.Where(scheme => scheme.Name == NegotiateDefaults.AuthenticationScheme))
                scheme.HandlerType = typeof(UnavailableWindowsAuthenticationHandler);
        });
        services.PostConfigure<NegotiateOptions>(NegotiateDefaults.AuthenticationScheme, options =>
        {
            options.Events ??= new NegotiateEvents();
            var onFailure = options.Events.OnAuthenticationFailed;
            options.Events.OnAuthenticationFailed = async context =>
            {
                await onFailure(context);
                if (context.Result is not null || !EmbeddedIdentityServerDefaults.IsAuthorizationEndpoint(context.Request)
                    || context.HttpContext.RequestServices.GetService<IOptions<EmbeddedIdentityServerSettings>>()?.Value.Enabled != true)
                    return;
                // Negotiate runs before OpenIddict validates the client and redirect address. Let that
                // validation finish, then let the embedded fallback return the protocol error safely.
                context.HttpContext.Items[EmbeddedIdentityServerDefaults.WindowsAuthenticationFailureItem] = context.Exception;
                context.SkipHandler();
            };
        });
        return services;
    }

    /// <summary>Marks the service collection as having the Negotiate scheme.</summary>
    private sealed class NegotiateRegistered;
}
