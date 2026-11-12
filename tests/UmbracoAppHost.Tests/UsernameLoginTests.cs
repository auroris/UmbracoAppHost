using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class UsernameLoginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_host_entry_points_allow_a_username_distinct_from_the_contact_email(bool useWebBuilder)
    {
        const string email = "login-test@example.invalid";
        const string username = "login-test";
        const string password = "Local-test-only-Username91!";
        var root = Directory.CreateTempSubdirectory("username-login-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root, ApplicationName = typeof(Program).Assembly.GetName().Name, EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InstanceRoot"] = Path.Combine(root, "instance"),
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Login test administrator",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = email,
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = password,
            ["Umbraco:CMS:Hosting:Debug"] = "false",
            // CreateUmbracoBuilder scans assemblies before the library's defaults, which exclude these, are added.
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
            // Exercise the default username policy without asking the administrator configurator
            // to manage this test account's identity.
            ["UmbracoAppHost:AdminUserName"] = "",
            ["UmbracoAppHost:AdminEmail"] = "",
            ["uSync:Settings:ExportOnSave"] = "None",
            ["uSync:Settings:RootFolder"] = Path.Combine(root, "uSync"),
            ["uSync:Settings:FirstBootGroup"] = "Settings",
        });
        if (useWebBuilder)
            builder.AddUmbracoAppHost();
        else
            builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers()
                .AddUmbracoAppHost(builder.Configuration).Build();

        UmbracoTestSupport.UseSingleServer(builder.Services);
        await using var app = builder.Build();
        await app.UseUmbracoAppHostAsync();
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // Umbraco completes unattended upgrades in a background service after the host starts.
            await UmbracoTestSupport.WaitForInstallAsync(app.Services);
            Assert.False(app.Services.GetRequiredService<IOptions<SecuritySettings>>().Value.UsernameIsEmail);
            using (var scope = app.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
                var administrator = await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString());
                Assert.NotNull(administrator);
                // Unattended installation still starts with an email-shaped username.
                Assert.Equal(email, administrator.UserName);
                administrator.UserName = username;
                Assert.True((await users.UpdateAsync(administrator)).Succeeded);
                Assert.Equal(email, (await users.FindByNameAsync(username))!.Email);
            }

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(20),
            };
            var loginPage = await client.GetStringAsync("/umbraco/login", TestContext.Current.CancellationToken);
            Assert.Contains("<umb-auth", loginPage);
            Assert.DoesNotContain("username-is-email", loginPage);

            const string loginUrl = "/umbraco/management/api/v1/security/back-office/login";
            using var emailLogin = await client.PostAsJsonAsync(loginUrl, new { username = email, password }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emailLogin.StatusCode == HttpStatusCode.Unauthorized,
                $"Email login returned {emailLogin.StatusCode}: {await emailLogin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
            using var wrongPassword = await client.PostAsJsonAsync(loginUrl, new { username, password = "Incorrect-password91!" }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
            using var usernameLogin = await client.PostAsJsonAsync(loginUrl, new { username, password }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, usernameLogin.StatusCode);
            Assert.Contains(usernameLogin.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("UMB_UCONTEXT="));
        }
        finally { await app.StopAsync(TestContext.Current.CancellationToken); }
    }
}
