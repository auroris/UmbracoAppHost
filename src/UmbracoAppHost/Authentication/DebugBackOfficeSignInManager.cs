using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Management.Security;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Security;

namespace UmbracoAppHost.Authentication;

/// <summary>Allows explicitly enabled Debug recovery through the native backoffice password login.</summary>
public sealed class DebugBackOfficeSignInManager(
    BackOfficeUserManager userManager,
    IHttpContextAccessor contextAccessor,
    IBackOfficeExternalLoginProviders externalLogins,
    IUserClaimsPrincipalFactory<BackOfficeIdentityUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    IOptions<GlobalSettings> globalSettings,
    ILogger<SignInManager<BackOfficeIdentityUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<BackOfficeIdentityUser> confirmation,
    IEventAggregator eventAggregator,
    IOptions<SecuritySettings> securitySettings,
    IOptions<BackOfficeAuthenticationTypeSettings> authenticationTypes,
    IRequestCache requestCache,
    IOptions<HostingSettings> hosting,
    IOptions<BackOfficeLoginSettings> login)
    : BackOfficeSignInManager(userManager, contextAccessor, externalLogins, claimsFactory,
        optionsAccessor, globalSettings, logger, schemes, confirmation, eventAggregator,
        securitySettings, authenticationTypes, requestCache)
{
    private bool RecoveryEnabled => login.Value.GetEffectiveFlow(hosting.Value.Debug) == BackOfficeLoginFlow.Debug;

    public override async Task<SignInResult> PasswordSignInAsync(BackOfficeIdentityUser user, string password, bool isPersistent, bool lockoutOnFailure)
    {
        if (!RecoveryEnabled) return await base.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
        if (string.IsNullOrEmpty(password)) return SignInResult.Failed;
        if (!user.IsApproved || !await CanSignInAsync(user)) return SignInResult.NotAllowed;

        // Umbraco's authorize endpoint signs a locked account out again, so recovery has to lift the lockout.
        if (await UserManager.IsLockedOutAsync(user)
            && !((await UserManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(-1))).Succeeded
                && (await UserManager.ResetAccessFailedCountAsync(user)).Succeeded))
        {
            Logger.LogWarning("Could not lift the lockout for Debug recovery of backoffice user {UserId}.", user.Id);
            return SignInResult.Failed;
        }

        user.LastLoginDate = DateTime.UtcNow;
        var update = await UserManager.UpdateAsync(user);
        if (!update.Succeeded)
        {
            Logger.LogWarning("Could not record Debug recovery sign-in for backoffice user {UserId}.", user.Id);
            return SignInResult.Failed;
        }

        var principal = await CreateUserPrincipalAsync(user);
        // Native SignInAsync also updates the account's security stamp. Recovery leaves that
        // intact while retaining the cookie handler's native login session.
        await Context.SignInAsync(AuthenticationType, principal, new AuthenticationProperties { IsPersistent = isPersistent });
        userManager.NotifyLoginSuccess(Context.User, user.Id);
        Logger.LogWarning("Debug recovery sign-in for backoffice user {UserId}.", user.Id);
        return SignInResult.Success;
    }
}
