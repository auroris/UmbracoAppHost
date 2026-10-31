using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Authentication;
using File = System.IO.File;
using TestTwoFactorProvider = UmbracoAppHost.Tests.BackOfficeSsoHttpTests.TestTwoFactorProvider;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class AdminPasswordTests
{
    private const string InstalledPassword = "Initial-install-only91!";
    private const string FirstPassword = "First-configured-password91!";
    private const string ChangedPassword = "  Changed-configured-password92!  ";
    private const string ManualPassword = "Manually-changed-password93!";
    private const string RestartPassword = "Configured-before-restart94!";

    [Fact]
    public async Task Configuration_updates_the_original_administrator_on_startup_and_live_file_saves()
    {
        var root = Directory.CreateTempSubdirectory("admin-password-").FullName;
        var log = new PasswordLog();
        await WriteSettings(root, FirstPassword);
        await using (var site = await StartApp(root, log))
        {
            var app = site.App;
            Assert.Equal(FirstPassword, app.Configuration[ConfiguredAdminPassword.ConfigurationKey]);
            await WaitUntil(() => Task.FromResult(app.Services.GetRequiredService<IRuntimeState>().Level == RuntimeLevel.Run));
            await WaitUntil(() => AcceptsPassword(app, FirstPassword), log);
            Assert.False(await AcceptsPassword(app, InstalledPassword));
            using (var scope = app.Services.CreateScope())
            {
                var administrator = scope.ServiceProvider.GetRequiredService<IUserService>().GetUserById(-1);
                Assert.NotNull(administrator);
                Assert.Equal(-1, administrator.Id);
                Assert.Equal("admin", administrator.Username);
                Assert.Equal("admin@localhost.local", administrator.Email);
            }

            using (var scope = app.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
                var administrator = (await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString()))!;
                administrator.Email = "renamed@example.invalid";
                administrator.UserName = administrator.Email;
                Assert.True((await users.UpdateAsync(administrator)).Succeeded);
                var other = BackOfficeIdentityUser.CreateNew(
                    scope.ServiceProvider.GetRequiredService<IOptions<GlobalSettings>>().Value,
                    "other-user@example.invalid", "other-user@example.invalid", "en", "Other backoffice user");
                Assert.True((await users.CreateAsync(other)).Succeeded);
                Assert.True((await users.AddPasswordAsync(other, InstalledPassword)).Succeeded);
            }
            var otherBefore = await AccountState(app, "other-user@example.invalid");
            await FailLogins(app, 6);
            Assert.True((await LockoutState(app)).Locked);

            // Exercise the real JSON file watcher, without calling IConfiguration.Reload().
            await WriteSettings(root, ChangedPassword);
            await WaitUntil(() => AcceptsPassword(app, ChangedPassword));
            await WaitUntil(async () => await LockoutState(app) == (false, 0));
            await WaitUntil(() => Task.FromResult(app.Services.GetRequiredService<IContentRoutingReadiness>().IsReady));
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using (var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) })
            {
                using var response = await client.PostAsJsonAsync("/umbraco/management/api/v1/security/back-office/login",
                    new { username = "admin", password = ChangedPassword });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("UMB_UCONTEXT="));
            }
            Assert.False(await AcceptsPassword(app, FirstPassword));
            Assert.False(await AcceptsPassword(app, ChangedPassword.Trim()));
            Assert.Equal(otherBefore, await AccountState(app, "other-user@example.invalid"));
            var changed = await AccountState(app);

            // An unrelated edit must not rotate the password hash or security stamp.
            await SaveAndSettle(app, root, ChangedPassword, "An unrelated title change");
            Assert.Equal(changed, await AccountState(app));

            await FailLogins(app, 6);
            var locked = await LockoutState(app);
            Assert.True(locked.Locked);
            var errors = log.Errors.Count;
            await WriteSettings(root, "short");
            await WaitUntil(() => Task.FromResult(log.Errors.Count > errors));
            Assert.Equal(changed, await AccountState(app));
            Assert.True(await AcceptsPassword(app, ChangedPassword));
            Assert.Equal(locked, await LockoutState(app));

            // Empty configuration never unlocks, even after a failed password update.
            await SaveAndSettle(app, root, "");
            Assert.Equal(locked, await LockoutState(app));
            // Saving the same valid password recovers lockout without rotating credentials.
            await SaveAndSettle(app, root, ChangedPassword);
            await WaitUntil(async () => await LockoutState(app) == (false, 0));
            Assert.Equal(changed, await AccountState(app));

            // A configured password also removes two-factor enrolment, the other way an administrator is locked out.
            await EnrollTwoFactor(app);
            Assert.Equal(TestTwoFactorProvider.Name, await TwoFactorProviders(app));
            await SaveAndSettle(app, root, ChangedPassword, "Two-factor recovery");
            await WaitUntil(async () => await TwoFactorProviders(app) == "");
            Assert.Equal(changed, await AccountState(app));
            // Without a configured password, enrolment is left alone.
            await EnrollTwoFactor(app);
            await SaveAndSettle(app, root, "");
            Assert.Equal(TestTwoFactorProvider.Name, await TwoFactorProviders(app));

            // A partial failure count is cleared even before lockout is reached.
            await FailLogins(app, 2);
            Assert.Equal((false, 2), await LockoutState(app));

            // A rejected value must not stop subsequent file updates from working.
            await WriteSettings(root, FirstPassword);
            await WaitUntil(() => AcceptsPassword(app, FirstPassword));
            await WaitUntil(async () => await LockoutState(app) == (false, 0));
            await WaitUntil(async () => await TwoFactorProviders(app) == "");
            var current = await AccountState(app);
            foreach (var empty in new string?[] { "", " \t ", null })
            {
                await SaveAndSettle(app, root, empty);
                Assert.Equal(current, await AccountState(app));
            }

            using (var scope = app.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
                var administrator = (await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString()))!;
                var token = await users.GeneratePasswordResetTokenAsync(administrator);
                Assert.True((await users.ChangePasswordWithResetAsync(administrator.Id, token, ManualPassword)).Succeeded);
            }
        }

        // The generated unattended-install password must not be reapplied on an existing site.
        await using (var site = await StartApp(root, log))
        {
            var app = site.App;
            await SaveAndSettle(app, root, null, "Still no configured password");
            Assert.True(await AcceptsPassword(app, ManualPassword));
            Assert.False(await AcceptsPassword(app, InstalledPassword));
        }

        await WriteSettings(root, RestartPassword);
        await using (var site = await StartApp(root, log, debug: true))
        {
            var app = site.App;
            await WaitUntil(() => HasPassword(app, RestartPassword));
            Assert.False(await HasPassword(app, ManualPassword));
            Assert.True(await AcceptsPassword(app, "Debug accepts an unrelated password"));

            // Debug's permissive sign-in checker must not mask a different stored password.
            await WriteSettings(root, FirstPassword);
            await WaitUntil(() => HasPassword(app, FirstPassword));
            Assert.False(await HasPassword(app, RestartPassword));
        }

        foreach (var password in new[] { InstalledPassword, FirstPassword, ChangedPassword, ManualPassword, RestartPassword, "short" })
            Assert.DoesNotContain(log.Messages, message => message.Contains(password, StringComparison.Ordinal));
    }

    /// <summary>A site on <paramref name="root"/>, whose <c>appsettings.json</c> the tests write while it runs.</summary>
    private static Task<TestSite> StartApp(string root, PasswordLog log, bool debug = false) => TestSite.StartAsync(new TestSiteOptions
    {
        Application = typeof(Program).Assembly,
        Root = root,
        EnvironmentName = "Production",
        AdminPassword = InstalledPassword,
        Settings = { ["Umbraco:CMS:Hosting:Debug"] = debug.ToString() },
        ConfigureBuilder = builder =>
        {
            builder.Services.AddSingleton<ILogger<ConfiguredAdminPassword>>(log);
            builder.Services.AddSingleton<TestTwoFactorProvider>();
            builder.Services.AddSingleton<ITwoFactorProvider>(services => services.GetRequiredService<TestTwoFactorProvider>());
            // After Umbraco's own configuration of these options, which this callback precedes.
            builder.Services.PostConfigure<BackOfficeIdentityOptions>(options =>
                options.Tokens.ProviderMap[TestTwoFactorProvider.Name] = new TokenProviderDescriptor(typeof(TestTwoFactorProvider)));
        },
    });

    private static Task WriteSettings(string root, string? password, string title = "Password test")
    {
        var settings = new Dictionary<string, string?> { ["Title"] = title };
        if (password is not null) settings["AdminPassword"] = password;
        return File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), JsonSerializer.Serialize(new { UmbracoAppHost = settings }));
    }

    private static async Task SaveAndSettle(WebApplication app, string root, string? password, string title = "Password test")
    {
        await WriteSettings(root, password, title);
        // A password left out of the file falls back to the launcher's empty one.
        await WaitUntil(() => Task.FromResult((app.Configuration[ConfiguredAdminPassword.ConfigurationKey] ?? "") == (password ?? "")
            && app.Configuration["UmbracoAppHost:Title"] == title));
        // Allow the queued notification (including a duplicate file-system event) to finish.
        await Task.Delay(750);
    }

    private static async Task<bool> AcceptsPassword(WebApplication app, string password)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        var administrator = await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString());
        return administrator is not null && await users.CheckPasswordAsync(administrator, password);
    }

    private static async Task<bool> HasPassword(WebApplication app, string password)
    {
        using var scope = app.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>().FindByIdAsync(Constants.Security.SuperUserKey.ToString());
        return user?.PasswordHash is { } hash
            && scope.ServiceProvider.GetRequiredService<IPasswordHasher<BackOfficeIdentityUser>>()
                .VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;
    }

    private static async Task<(string? Hash, string? Stamp)> AccountState(WebApplication app, string? email = null)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        var user = email is null ? await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString()) : await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        return (user.PasswordHash, user.SecurityStamp);
    }

    private static async Task FailLogins(WebApplication app, int count)
    {
        await WaitUntil(() => Task.FromResult(app.Services.GetRequiredService<IContentRoutingReadiness>().IsReady));
        string username;
        using (var scope = app.Services.CreateScope())
            username = (await scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>()
                .FindByIdAsync(Constants.Security.SuperUserKey.ToString()))!.UserName!;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20),
        };
        for (var attempt = 0; attempt < count; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/umbraco/management/api/v1/security/back-office/login",
                new { username, password = "An incorrect password" });
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }
    }

    private static async Task EnrollTwoFactor(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITwoFactorLoginService>().SaveAsync(new TwoFactorLogin
        {
            UserOrMemberKey = Constants.Security.SuperUserKey, ProviderName = TestTwoFactorProvider.Name,
            Secret = "test-only-secret", Confirmed = true,
        });
    }

    private static async Task<string> TwoFactorProviders(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        return string.Join(",", await scope.ServiceProvider.GetRequiredService<ITwoFactorLoginService>()
            .GetEnabledTwoFactorProviderNamesAsync(Constants.Security.SuperUserKey));
    }

    private static async Task<(bool Locked, int Attempts)> LockoutState(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        var user = (await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString()))!;
        return (await users.IsLockedOutAsync(user), user.AccessFailedCount);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, PasswordLog? log = null)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("The administrator password configuration did not take effect within 10 seconds. "
            + (log is null ? "" : string.Join(Environment.NewLine, log.Messages)));
    }

    private sealed class PasswordLog : ILogger<ConfiguredAdminPassword>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception) + (exception is null ? "" : Environment.NewLine + exception);
            Messages.Enqueue(message);
            if (logLevel >= LogLevel.Error) Errors.Enqueue(message);
        }
    }
}
