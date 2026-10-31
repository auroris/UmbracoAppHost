using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

/// <summary>
/// One real site for a class of account tests: a <see cref="TestSite"/> without content and with
/// both account sources set to <c>Umbraco</c>, so every account is pre-created by the test itself.
/// </summary>
public sealed class AccountsTestHost : IAsyncLifetime
{
    public const string AdminPassword = "Local-test-only-Accounts91!";
    public const string MemberTypeAlias = "Member";

    private TestSite? site;

    public TestSite Site => site ?? throw new InvalidOperationException("The test site has not started.");
    public WebApplication App => Site.App;
    public Uri Address => Site.Address;

    public async ValueTask InitializeAsync() => site = await TestSite.StartAsync(new TestSiteOptions
    {
        Application = typeof(Program).Assembly,
        EnvironmentName = "Production",
        AdminPassword = AdminPassword,
        // These tests create their own accounts and no content.
        FirstBootGroup = "Settings",
        Settings = { ["Accounts:BackOfficeUsersManagedBy"] = "Umbraco", ["Accounts:MembersManagedBy"] = "Umbraco" },
    });

    public async ValueTask DisposeAsync()
    {
        if (site is not null) await site.DisposeAsync();
    }

    /// <summary>The registered user service, which is the retiring decorator.</summary>
    public IUserService Users => App.Services.GetRequiredService<IUserService>();

    public IMemberService Members => App.Services.GetRequiredService<IMemberService>();

    public IExternalLoginWithKeyService ExternalLogins => App.Services.GetRequiredService<IExternalLoginWithKeyService>();

    /// <summary>A sign-in as a provider presents it, with a domain account name such as <c>DOMAIN\Test.User</c>.</summary>
    public static SsoAccount Account(string sid, string accountName, string? email = null, params string[] groups)
    {
        var userName = accountName[(accountName.IndexOf('\\') + 1)..];
        return new SsoAccount(sid, accountName, userName, userName.Replace('.', ' '), email, null, null, true, groups);
    }

    /// <summary>Creates an approved backoffice user in the Editors group, as an administrator would.</summary>
    public Task<IUser> CreateUserAsync(string username, string email, string name) => Site.CreateUserAsync(username, email, name);

    /// <summary>Creates an approved member, as an administrator would.</summary>
    public IMember CreateMember(string username, string email, string name) => Site.CreateMember(username, email, name);

    /// <summary>Records a sign-in date, which is what makes Umbraco refuse to delete an account.</summary>
    public async Task<IUser> MarkSignedInAsync(IUser user)
    {
        var current = (await Users.GetAsync(user.Key))!;
        current.LastLoginDate = DateTime.UtcNow;
        Users.Save(current);
        return (await Users.GetAsync(user.Key))!;
    }

    /// <summary>Exercises the optional explicit provisioning helper for a backoffice account.</summary>
    public Task<ProvisioningResult> ProvisionUserAsync(SsoAccount account, string scheme)
        => ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(account, scheme));

    /// <summary>Exercises the optional explicit provisioning helper for a member account.</summary>
    public Task<ProvisioningResult> ProvisionMemberAsync(SsoAccount account, string scheme)
        => ProvisionAsync(provisioning => provisioning.ProvisionMemberAsync(account, scheme));

    /// <summary>Runs the account policy, optionally with other <c>Accounts</c> settings than the site's.</summary>
    public async Task<ProvisioningResult> ProvisionAsync(Func<AccountProvisioning, Task<ProvisioningResult>> provision, AccountsSettings? settings = null)
    {
        using var scope = App.Services.CreateScope();
        var provisioning = settings is null
            ? scope.ServiceProvider.GetRequiredService<AccountProvisioning>()
            : ActivatorUtilities.CreateInstance<AccountProvisioning>(scope.ServiceProvider, Microsoft.Extensions.Options.Options.Create(settings));
        return await provision(provisioning);
    }

    /// <summary>The account's external logins as (scheme, provider key) pairs, in a stable order.</summary>
    public IReadOnlyList<(string Scheme, string Key)> Logins(Guid key)
        => ExternalLogins.GetExternalLogins(key)
            .Select(login => (login.LoginProvider, login.ProviderKey))
            .OrderBy(login => login.LoginProvider, StringComparer.Ordinal)
            .ToList();

    /// <summary>Gives a backoffice user a password for the local login form.</summary>
    public Task AddPasswordAsync(IUser user, string password) => Site.SetPasswordAsync(user, password);

    public Task<HttpClient> SignInAsAdministratorAsync() => Site.SignInAsAdministratorAsync();

    /// <summary>A client with the backoffice cookie and the management API token, as the backoffice signs in.</summary>
    public Task<HttpClient> SignInAsync(string username, string password) => Site.SignInManagementApiAsync(username, password);

    /// <summary>
    /// Asks for an authorization code with the client's backoffice cookie, as the backoffice does
    /// whenever it needs a new token. A valid cookie is answered with a redirect to
    /// <see cref="RedirectUri"/> carrying the code.
    /// </summary>
    public Task<HttpResponseMessage> AuthorizeAsync(HttpClient client, string? verifier = null)
        => client.GetAsync(QueryHelpers.AddQueryString(BackOfficeApi + "/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = Constants.OAuthClientIds.BackOffice,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier ?? Base64Url(RandomNumberGenerator.GetBytes(32))))),
            ["code_challenge_method"] = "S256",
            ["state"] = "accounts-test",
        }));

    public string RedirectUri => new Uri(Address, "/umbraco/oauth_complete").ToString();

    private const string BackOfficeApi = "/umbraco/management/api/v1/security/back-office";

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
