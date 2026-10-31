using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using UmbracoAppHost.IdentityServer;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Whether Windows Integrated Authentication can work where this site runs. On Kestrel the Negotiate
/// handler performs the Kerberos or NTLM exchange itself. In IIS it defers to IIS Windows
/// Authentication, and when a site has only Anonymous Authentication enabled the handler refuses to
/// work at all: its options throw when they are built, and because the handler inspects every
/// request, every request of the site would fail. The site runs without Windows authentication
/// instead: the embedded identity server refuses to sign anyone in, the login screens omit it,
/// and people sign in with a username and password or through an external
/// identity server, as <see cref="WindowsAuthenticationStartupLog"/> says at startup.
/// </summary>
public sealed class WindowsAuthenticationAvailability
{
    /// <summary>The refusal a person sees when a Windows sign-in is attempted where none can work.</summary>
    public const string UnavailableMessage = "Windows authentication is not enabled where this site runs. Use the application's local sign-in.";

    public WindowsAuthenticationAvailability(IEnumerable<IServerIntegratedAuth> serverAuthentication)
    {
        // The same service, and the same first registration, that the Negotiate handler's options consult.
        var server = serverAuthentication.FirstOrDefault();
        IsAvailable = server is null || server.IsEnabled;
    }

    /// <summary>True on Kestrel, and in IIS when Windows Authentication is enabled for the site.</summary>
    public bool IsAvailable { get; }

    /// <summary>Why Windows authentication is unavailable, for the log; null when it is available.</summary>
    public string? Reason => IsAvailable ? null : "IIS has only Anonymous Authentication enabled for this site";
}

/// <summary>
/// Stands in for the Negotiate handler when Windows authentication is unavailable, so that callers
/// get "not authenticated" instead of the exception the real handler's options would throw.
/// </summary>
public sealed class UnavailableWindowsAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.Fail(WindowsAuthenticationAvailability.UnavailableMessage));

    /// <summary>A challenge cannot start an exchange nobody can complete: 401 without inviting one.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Wraps the Negotiate handler's own post-configuration, which throws when IIS reports Windows
/// Authentication disabled. A startup filter of the handler builds the options when the site starts,
/// so without this the site would not start at all. When Windows authentication is unavailable the
/// options are left alone and the handler is never used; otherwise the framework's logic runs unchanged.
/// </summary>
public sealed class TolerantNegotiateOptions(WindowsAuthenticationAvailability windows, IPostConfigureOptions<NegotiateOptions> framework)
    : IPostConfigureOptions<NegotiateOptions>
{
    public void PostConfigure(string? name, NegotiateOptions options)
    {
        // Unavailable: the options are never used, as UnavailableWindowsAuthenticationHandler replaces the handler.
        if (windows.IsAvailable) framework.PostConfigure(name, options);
    }
}

/// <summary>Says at startup when Windows authentication is unavailable and a feature that needs it is on.</summary>
public sealed class WindowsAuthenticationStartupLog(
    WindowsAuthenticationAvailability windows,
    IOptions<IdentityServerSettings> identityServer,
    IOptions<EmbeddedIdentityServerSettings> embeddedServer,
    ILogger<WindowsAuthenticationStartupLog> logger) : INotificationHandler<UmbracoApplicationStartedNotification>
{
    public void Handle(UmbracoApplicationStartedNotification notification)
    {
        if (windows.IsAvailable || !embeddedServer.Value.Enabled) return;
        var fallback = identityServer.Value is { Enabled: true } site && !site.UsesEmbeddedIdentityServer
            ? $"through the identity server at {site.Authority} or with a username and password"
            : "with a username and password";
        logger.LogWarning("{Reason}, so the embedded identity server cannot sign anyone in; people sign in {Fallback}. Enable Windows Authentication for the site in IIS, or set IdentityServer:Authority to an external identity server and EmbeddedIdentityServer:Enabled to false.",
            windows.Reason, fallback);
    }
}
