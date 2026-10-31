using System.Threading.Channels;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Primitives;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Authentication;

/// <summary>Applies the built-in administrator's identity and optional password at startup and when configuration reloads.</summary>
public sealed class ConfiguredAdminPassword(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    IRuntimeState runtime,
    ILogger<ConfiguredAdminPassword> logger) : BackgroundService
{
    public const string ConfigurationKey = "UmbracoAppHost:AdminPassword";
    public const string UserNameConfigurationKey = "UmbracoAppHost:AdminUserName";
    public const string EmailConfigurationKey = "UmbracoAppHost:AdminEmail";
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly TaskCompletionSource _startupApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // File saves can send several notifications. Process them serially and always read
    // the latest configuration, keeping at most one pending update.
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    private void QueueUpdate() => _changes.Writer.TryWrite(true);

    internal async Task ApplyAtStartupAsync(CancellationToken cancellationToken)
    {
        if (runtime.Level != RuntimeLevel.Run) return;
        // Run in Umbraco's ordered startup notifications, after the base migrations.
        // Parallel identity writes can invalidate SQLite transactions used during startup.
        await ApplyUpdateAsync(cancellationToken);
        _startupApplied.TrySetResult();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var reload = ChangeToken.OnChange(configuration.GetReloadToken, QueueUpdate);
        await _startupApplied.Task.WaitAsync(stoppingToken);
        await foreach (var change in _changes.Reader.ReadAllAsync(stoppingToken))
            await ApplyUpdateAsync(stoppingToken);
    }

    private async Task ApplyUpdateAsync(CancellationToken cancellationToken)
    {
        await _applyLock.WaitAsync(cancellationToken);
        try
        {
            if (runtime.Level != RuntimeLevel.Run) return;
            try
            {
                await ApplyAsync();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not apply the configured administrator credentials. Check {UserNameSetting}, {EmailSetting} and {PasswordSetting}, then save the file to try again.",
                    UserNameConfigurationKey, EmailConfigurationKey, ConfigurationKey);
            }
        }
        finally { _applyLock.Release(); }
    }

    private async Task ApplyAsync()
    {
        var password = configuration[ConfigurationKey];
        var username = (configuration[UserNameConfigurationKey] ?? "admin").Trim();
        var email = (configuration[EmailConfigurationKey] ?? "admin@localhost.local").Trim();
        if (string.IsNullOrWhiteSpace(password) && username.Length == 0 && email.Length == 0) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        // The original administrator remains the target even if its email or name changes.
        var administrator = await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString());
        if (administrator is null)
        {
            logger.LogError("Could not configure the administrator credentials: the built-in administrator account was not found.");
            return;
        }

        // Apply identity and password settings in the same queue to avoid stale identity records.
        var rename = username.Length > 0 && !string.Equals(administrator.UserName, username, StringComparison.Ordinal);
        var changeEmail = email.Length > 0 && !string.Equals(administrator.Email, email, StringComparison.Ordinal);
        if (rename || changeEmail)
        {
            if (rename) administrator.UserName = username;
            if (changeEmail) administrator.Email = email;
            var renamed = await users.UpdateAsync(administrator);
            if (!renamed.Succeeded)
            {
                logger.LogError("Could not apply {UserNameSetting} and {EmailSetting}: {Errors}.", UserNameConfigurationKey, EmailConfigurationKey,
                    string.Join("; ", renamed.Errors.Select(error => error.Description)));
                return;
            }
            logger.LogInformation("Administrator identity configured as {UserName}, {Email}.", administrator.UserName, administrator.Email);
            // Umbraco can change the persisted security stamp when the identity is saved.
            // Password reset tokens must use that stamp, not the pre-update instance.
            administrator = await users.FindByIdAsync(administrator.Id)
                ?? throw new InvalidOperationException("The administrator disappeared after its identity was configured.");
        }
        if (string.IsNullOrWhiteSpace(password)) return;

        // Check the stored hash directly: the development password checker can accept any
        // password. An unchanged password must not reset the account's security stamp.
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<BackOfficeIdentityUser>>();
        var unchanged = administrator.PasswordHash is { } hash
            && hasher.VerifyHashedPassword(administrator, hash, password) != PasswordVerificationResult.Failed;
        if (!unchanged)
        {
            var token = await users.GeneratePasswordResetTokenAsync(administrator);
            var result = await users.ChangePasswordWithResetAsync(administrator.Id, token, password);
            if (!result.Succeeded)
            {
                logger.LogError("Could not apply {Setting}: {Errors}. Correct the setting and save the file to try again.",
                    ConfigurationKey, string.Join("; ", result.Errors.Select(error => error.Description)));
                return;
            }

            // ChangePasswordWithResetAsync loads and saves another identity instance.
            // Reload it before recovery so no old password or security stamp can be saved back.
            administrator = await users.FindByIdAsync(administrator.Id)
                ?? throw new InvalidOperationException("The administrator disappeared after its password was reset.");
            logger.LogInformation("Administrator password applied from {Setting} for {UserName}.", ConfigurationKey, administrator.UserName);
        }

        // Reapplying the same valid password can also recover a locked account, without
        // rotating its hash or stamp when an unrelated configuration change is saved.
        if (administrator.IsLockedOut)
        {
            var unlocked = await users.SetLockoutEndDateAsync(administrator, DateTimeOffset.UtcNow.AddMinutes(-1));
            if (!unlocked.Succeeded)
            {
                logger.LogError("Administrator password is configured, but unlocking failed: {Errors}.",
                    string.Join("; ", unlocked.Errors.Select(error => error.Description)));
                return;
            }
        }
        if (administrator.AccessFailedCount > 0)
        {
            var resetAttempts = await users.ResetAccessFailedCountAsync(administrator);
            if (!resetAttempts.Succeeded)
                logger.LogError("Administrator password is configured, but resetting failed login attempts failed: {Errors}.",
                    string.Join("; ", resetAttempts.Errors.Select(error => error.Description)));
        }

        // The configured password is the break-glass credential, so nothing may stand between it and
        // the account: two-factor enrolment goes too, as a lost authenticator is the other lockout.
        var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorLoginService>();
        foreach (var provider in (await twoFactor.GetEnabledTwoFactorProviderNamesAsync(administrator.Key)).ToArray())
        {
            if (await twoFactor.DisableAsync(administrator.Key, provider))
                logger.LogInformation("Administrator password is configured; two-factor authentication with {Provider} was removed from {UserName}.", provider, administrator.UserName);
            else
                logger.LogError("Administrator password is configured, but two-factor authentication with {Provider} could not be removed.", provider);
        }
    }
}

// Umbraco can finish an unattended installation after the web server starts. Its own
// started notification is also raised after that installation or upgrade completes.
internal sealed class ConfiguredAdminPasswordStartup(ConfiguredAdminPassword password)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
        => password.ApplyAtStartupAsync(cancellationToken);
}
