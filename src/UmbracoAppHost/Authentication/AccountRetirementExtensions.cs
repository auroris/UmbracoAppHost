using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Turns backoffice "delete" of a user or member that has signed in into retirement: the account
/// row stays so its name remains on everything it authored, but its username becomes the Windows
/// SID it was linked to, it is disabled, and its external logins are retained, so nobody can sign
/// in as it again. Settings come from the <c>Accounts</c> section.
/// </summary>
public static class AccountRetirementExtensions
{
    /// <summary>Registers retirement on delete. Called by <see cref="SsoExtensions.AddSingleSignOn"/>.</summary>
    /// <param name="builder">The Umbraco builder; Umbraco's user and member editing services must already be registered.</param>
    /// <param name="configuration">
    /// Unused: <see cref="AccountsSettings.RetireDeletedUsers"/> and <see cref="AccountsSettings.RetireDeletedMembers"/>
    /// are read from the <c>Accounts</c> section bound by <see cref="SsoExtensions.AddSingleSignOn"/> at every
    /// delete, so a change applies without a restart.
    /// </param>
    public static IUmbracoBuilder AddAccountRetirement(this IUmbracoBuilder builder, IConfiguration configuration)
    {
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(AccountRetirement)))
            return builder;

        builder.Services.AddSingleton<AccountRetirement>();
        // The retirement service is resolved at the first delete, not with the decorators: Umbraco
        // resolves its user service while starting up, from many singletons and threads, and the
        // decorator must not add the member, audit and login services to that construction.
        Decorate<IUserService>(builder.Services, (provider, inner) => new RetiringUserService(
            inner,
            new Lazy<AccountRetirement>(provider.GetRequiredService<AccountRetirement>),
            provider.GetRequiredService<ICoreScopeProvider>(),
            provider.GetRequiredService<IOptionsMonitor<AccountsSettings>>(),
            provider.GetRequiredService<ILogger<RetiringUserService>>()));
        Decorate<IMemberEditingService>(builder.Services, (provider, inner) => new RetiringMemberEditingService(
            inner,
            provider.GetRequiredService<IMemberService>(),
            new Lazy<AccountRetirement>(provider.GetRequiredService<AccountRetirement>),
            provider.GetRequiredService<IOptionsMonitor<AccountsSettings>>()));
        return builder;
    }

    /// <summary>
    /// Replaces Umbraco's registration of <typeparamref name="TService"/> with a decorator around the
    /// instance that registration builds, keeping its lifetime, as <c>AddUmbracoAppHost</c> does for
    /// the authorization policy provider: Umbraco's implementations are internal and cannot be
    /// subclassed. Nothing is replaced when the service is not registered.
    /// </summary>
    internal static void Decorate<TService>(IServiceCollection collection, Func<IServiceProvider, TService, TService> decorate)
        where TService : class
    {
        var original = collection.LastOrDefault(descriptor => descriptor.ServiceType == typeof(TService) && !descriptor.IsKeyedService);
        if (original is null) return;
        collection.Replace(new ServiceDescriptor(typeof(TService), provider => decorate(provider, (TService)(original.ImplementationInstance
            ?? original.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!))), original.Lifetime));
    }
}
