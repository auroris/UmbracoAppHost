using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace UmbracoAppHost.IdentityServer;

/// <summary>Delegates IIS Windows authentication to the native handler registered by the outer web host.</summary>
internal sealed class IntegratedWindowsAuthenticationBridge(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => InHostAsync(() => Context.AuthenticateAsync(Scheme.Name));

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => InHostAsync(async () => { await Context.ChallengeAsync(Scheme.Name, properties); return true; });

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => InHostAsync(async () => { await Context.ForbidAsync(Scheme.Name, properties); return true; });

    private async Task<T> InHostAsync<T>(Func<Task<T>> action)
    {
        var services = Context.RequestServices;
        try
        {
            Context.RequestServices = Context.Features.Get<EmbeddedIdentityServerRequest>()?.HostServices
                ?? throw new InvalidOperationException("The integrated Windows scheme can only run inside the embedded identity server.");
            return await action();
        }
        finally { Context.RequestServices = services; }
    }
}
