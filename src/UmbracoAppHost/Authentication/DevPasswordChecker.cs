using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Security;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Accepts any nonempty backoffice password when the selected login flow is Debug and
/// <c>Umbraco:CMS:Hosting:Debug</c> is true. Otherwise it uses Umbraco's password check.
/// </summary>
/// <remarks>
/// Umbraco only consults this checker for usernames that exist, so it never creates access
/// for unknown accounts. Enable <c>Hosting:Debug</c> with the Debug flow only for development
/// or deliberate backoffice recovery; normal operation uses the configured password check.
/// </remarks>
public sealed class DevPasswordChecker(IOptions<HostingSettings> hostingSettings, IOptions<BackOfficeLoginSettings> loginSettings)
    : IBackOfficeUserPasswordChecker
{
    public Task<BackOfficeUserPasswordCheckerResult> CheckPasswordAsync(BackOfficeIdentityUser user, string password)
        => Task.FromResult(loginSettings.Value.GetEffectiveFlow(hostingSettings.Value.Debug) == BackOfficeLoginFlow.Debug
            && !string.IsNullOrEmpty(password)
            ? BackOfficeUserPasswordCheckerResult.ValidCredentials
            : BackOfficeUserPasswordCheckerResult.FallbackToDefaultChecker);
}
