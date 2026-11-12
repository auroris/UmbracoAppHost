using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;
using UmbracoAppHost.Authentication;
using UmbracoAppHost.DirectoryAccounts;

namespace UmbracoAppHost.Tests;

/// <summary>
/// Accounts created from the directory carry the person's Windows SID from the start. This machine
/// is not in a domain, so its directory is its local accounts, and the person searched for and
/// given accounts is whoever runs the tests.
/// </summary>
[Collection("Umbraco hosts")]
[SupportedOSPlatform("windows")]
public sealed class DirectoryAccountsTests(AccountsTestHost host) : IClassFixture<AccountsTestHost>
{
    private const string Api = "/umbraco/management/api/v1/directory";
    private const string Domain = "Umbraco.Domain";
    private const string DomainMembers = "UmbracoMembers.Domain";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The account policy with first-sign-in matching by username or email turned off.</summary>
    private static readonly AccountsSettings Strict = new() { LinkByUserNameOnFirstSignIn = false };

    private static string CurrentSid => WindowsIdentity.GetCurrent().User!.Value;

    private static string CurrentAccountName => WindowsIdentity.GetCurrent().Name;

    private static string CurrentUserName => Environment.UserName.ToLowerInvariant();

    [Fact]
    public async Task A_user_created_from_the_directory_carries_the_SID_and_signs_in_by_it_alone()
    {
        var sid = CurrentSid;
        if (AccountDirectorySettings.IsOutsideDomain)
            Assert.IsType<LocalAccountDirectory>(host.App.Services.GetRequiredService<IAccountDirectory>());
        using var administrator = await host.SignInAsAdministratorAsync();

        // The search finds the person running the tests, who has no user yet.
        var before = await SearchAsync(administrator, "users", Environment.UserName);
        var person = Assert.Single(before.Results, result => result.Sid == sid);
        Assert.Equal(CurrentAccountName, person.AccountName, ignoreCase: true);
        Assert.True(person.Enabled);
        Assert.DoesNotContain(sid, before.Existing.Keys);

        // Created with the platform's username, the directory's name and the email fallback, in the
        // chosen group, and linked under every backoffice scheme before anyone signs in.
        using var response = await administrator.PostAsJsonAsync($"{Api}/users", new { sid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken);
        var created = await ReadAsync<CreatedAccount>(response, HttpStatusCode.Created);
        Assert.Equal(CurrentUserName, created.Username);
        Assert.Equal(person.DisplayName, created.Name);
        Assert.Equal(person.Email ?? $"{CurrentUserName}@localhost", created.Email);
        var user = (await host.Users.GetAsync(created.Key))!;
        Assert.False(string.IsNullOrEmpty(user.RawPasswordValue));
        Assert.False(user.RawPasswordValue.StartsWith(Constants.Security.EmptyPasswordPrefix, StringComparison.Ordinal));
        Assert.Equal(CurrentUserName, user.Username);
        Assert.True(user.IsApproved);
        Assert.Equal(new[] { Constants.Security.EditorGroupKey }, user.Groups.Select(group => group.Key));
        Assert.Equal(new[] { (Domain, sid) }, host.Logins(created.Key));
        using (var scope = host.App.Services.CreateScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<IAuditService>().GetItemsByEntityAsync(user.Id, 0, 20);
            Assert.Contains(audit.Items, item => item.AuditType == AuditType.New && item.EntityType == "User" && item.Comment!.Contains(sid));
        }

        // The search now reports the user, and creating a second one is a conflict.
        var after = await SearchAsync(administrator, "users", Environment.UserName);
        Assert.Equal(new ExistingAccount("user", created.Key, CurrentUserName, "active"), after.Existing[sid]);
        using (var again = await administrator.PostAsJsonAsync($"{Api}/users", new { sid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken))
        {
            var problem = await ProblemAsync(again, HttpStatusCode.Conflict);
            Assert.Equal("Already has an account", problem.Title);
            Assert.Contains(CurrentUserName, problem.Detail);
        }

        // The SID alone opens the account, with name matching off, through IdentityServer.
        var signIn = AccountsTestHost.Account(sid, CurrentAccountName);
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(signIn, Domain), Strict));

        // Once retired, the account still holds the SID: the search says so, and a new user is refused.
        Assert.Equal(UserOperationStatus.Success, await host.Users.DeleteAsync(Constants.Security.SuperUserKey, created.Key));
        Assert.Equal(sid, (await host.Users.GetAsync(created.Key))!.Username);
        Assert.Equal("retired", (await SearchAsync(administrator, "users", Environment.UserName)).Existing[sid].State);
        using (var retired = await administrator.PostAsJsonAsync($"{Api}/users", new { sid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken))
        {
            var problem = await ProblemAsync(retired, HttpStatusCode.Conflict);
            Assert.Equal("Retired account", problem.Title);
            Assert.Contains("enable that user in Users and give it back its username and email", problem.Detail);
        }
    }

    [Fact]
    public async Task A_member_created_from_the_directory_carries_the_SID_and_signs_in_by_it_alone()
    {
        var sid = CurrentSid;
        using (var scope = host.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMemberGroupService>().CreateAsync(new MemberGroup { Name = "Directory Readers" });
        using var administrator = await host.SignInAsAdministratorAsync();

        // Member types and groups are checked before anything is created.
        using (var unknownType = await administrator.PostAsJsonAsync($"{Api}/members", new { sid, memberTypeAlias = "noSuchType", memberGroupNames = Array.Empty<string>() }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("Unknown member type", (await ProblemAsync(unknownType, HttpStatusCode.BadRequest)).Title);
        using (var unknownGroup = await administrator.PostAsJsonAsync($"{Api}/members", new { sid, memberGroupNames = new[] { "No Such Group" } }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("Unknown member group", (await ProblemAsync(unknownGroup, HttpStatusCode.BadRequest)).Title);

        var before = await SearchAsync(administrator, "members", Environment.UserName);
        var person = Assert.Single(before.Results, result => result.Sid == sid);
        Assert.DoesNotContain(sid, before.Existing.Keys);

        // The member type defaults to Accounts:MemberTypeAlias.
        using var response = await administrator.PostAsJsonAsync($"{Api}/members", new { sid, memberGroupNames = new[] { "Directory Readers" } }, cancellationToken: TestContext.Current.CancellationToken);
        var created = await ReadAsync<CreatedAccount>(response, HttpStatusCode.Created);
        Assert.Equal(CurrentUserName, created.Username);
        Assert.Equal(person.DisplayName, created.Name);
        Assert.Equal((person.Email ?? $"{CurrentUserName}@localhost").ToLowerInvariant(), created.Email);
        var member = host.Members.GetById(created.Key)!;
        Assert.False(string.IsNullOrEmpty(member.RawPasswordValue));
        Assert.False(member.RawPasswordValue.StartsWith(Constants.Security.EmptyPasswordPrefix, StringComparison.Ordinal));
        Assert.True(member.IsApproved);
        Assert.Equal(AccountsTestHost.MemberTypeAlias, member.ContentTypeAlias);
        Assert.Equal(new[] { "Directory Readers" }, host.Members.GetAllRoles(member.Id));
        Assert.Equal(new[] { (DomainMembers, sid) }, host.Logins(created.Key));

        var after = await SearchAsync(administrator, "members", Environment.UserName);
        Assert.Equal(new ExistingAccount("member", created.Key, CurrentUserName, "active"), after.Existing[sid]);
        using (var again = await administrator.PostAsJsonAsync($"{Api}/members", new { sid, memberGroupNames = Array.Empty<string>() }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("Already has an account", (await ProblemAsync(again, HttpStatusCode.Conflict)).Title);

        var signIn = AccountsTestHost.Account(sid, CurrentAccountName);
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionAsync(provisioning => provisioning.ProvisionMemberAsync(signIn, DomainMembers), Strict));

        using (var scope = host.App.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IMemberEditingService>().DeleteAsync(created.Key, Constants.Security.SuperUserKey)).Success);
        Assert.Equal(sid, host.Members.GetById(created.Key)!.Username);
        using (var retired = await administrator.PostAsJsonAsync($"{Api}/members", new { sid, memberGroupNames = Array.Empty<string>() }, cancellationToken: TestContext.Current.CancellationToken))
        {
            var problem = await ProblemAsync(retired, HttpStatusCode.Conflict);
            Assert.Equal("Retired account", problem.Title);
            Assert.Contains("enable that member in Members", problem.Detail);
        }
    }

    [Fact]
    public async Task With_matching_by_name_off_only_the_SID_opens_an_account()
    {
        const string sid = "S-1-5-21-900-901-902-1001";
        var person = AccountsTestHost.Account(sid, "DOMAIN\\Hand.Made", "hand.made@example.invalid");
        var refused = ProvisioningResult.NotFound(AccountProvisioning.NotLinkedReason);
        var user = await host.CreateUserAsync("hand.made", "hand.made@example.invalid", "Hand Made");
        var member = host.CreateMember("hand.made", "hand.made@example.invalid", "Hand Made");

        // A username and email that fit open nothing, and nothing gets linked.
        Assert.Equal(refused, await host.ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(person, Domain), Strict));
        Assert.Equal(refused, await host.ProvisionAsync(provisioning => provisioning.ProvisionMemberAsync(person, DomainMembers), Strict));
        Assert.Empty(host.Logins(user.Key));
        Assert.Empty(host.Logins(member.Key));
        var stranger = AccountsTestHost.Account("S-1-5-21-900-901-902-9999", "DOMAIN\\No.One");
        Assert.Equal(refused, await host.ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(stranger, Domain), Strict));

        // With the default the same sign-ins claim the accounts, which the SID then opens either way.
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionUserAsync(person, Domain));
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionMemberAsync(person, DomainMembers));
        Assert.Equal(new[] { (Domain, sid) }, host.Logins(user.Key));
        Assert.Equal(new[] { (DomainMembers, sid) }, host.Logins(member.Key));
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(person, Domain), Strict));
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionAsync(provisioning => provisioning.ProvisionMemberAsync(person, DomainMembers), Strict));

        // A retired SID is still refused as retired.
        Assert.Equal(UserOperationStatus.Success, await host.Users.DeleteAsync(Constants.Security.SuperUserKey, user.Key));
        Assert.Equal(ProvisioningResult.Unauthorized("This account has been retired."),
            await host.ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(person, Domain), Strict));
    }

    [Fact]
    public async Task Requests_are_checked_before_the_directory_is_asked()
    {
        using var administrator = await host.SignInAsAdministratorAsync();

        using (var tooShort = await administrator.GetAsync($"{Api}/users/search?query=s", TestContext.Current.CancellationToken))
        {
            var problem = await ProblemAsync(tooShort, HttpStatusCode.BadRequest);
            Assert.Equal("The directory refused the search", problem.Title);
            Assert.Contains("at least 2 characters", problem.Detail);
        }
        using (var empty = await administrator.GetAsync($"{Api}/members/search", TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using (var notASid = await administrator.PostAsJsonAsync($"{Api}/users", new { sid = "DOMAIN\\Test.User", userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("No person chosen", (await ProblemAsync(notASid, HttpStatusCode.BadRequest)).Title);
        using (var noGroup = await administrator.PostAsJsonAsync($"{Api}/users", new { sid = CurrentSid, userGroupKeys = Array.Empty<Guid>() }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("Choose a user group", (await ProblemAsync(noGroup, HttpStatusCode.BadRequest)).Title);
        using (var nobody = await administrator.PostAsJsonAsync($"{Api}/users", new { sid = "S-1-5-21-1-2-3-4242", userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("Not in the directory", (await ProblemAsync(nobody, HttpStatusCode.NotFound)).Title);
    }

    [Fact]
    public async Task Only_people_with_access_to_the_section_may_search_or_create()
    {
        const string password = "Local-test-only-Directory91!";
        using (var anonymous = new HttpClient { BaseAddress = host.Address })
        using (var response = await anonymous.GetAsync($"{Api}/users/search?query={Environment.UserName}", TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Editors have the Content and Media sections, but neither Users nor Members.
        var editor = await host.CreateUserAsync("directory.editor", "directory.editor@example.invalid", "Directory Editor");
        await host.AddPasswordAsync(editor, password);
        using var session = await host.SignInAsync("directory.editor", password);
        foreach (var path in new[] { $"{Api}/users/search?query={Environment.UserName}", $"{Api}/members/search?query={Environment.UserName}" })
        {
            using var response = await session.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var response = await session.PostAsJsonAsync($"{Api}/users", new { sid = CurrentSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await session.PostAsJsonAsync($"{Api}/members", new { sid = CurrentSid, memberGroupNames = Array.Empty<string>() }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<DirectorySearchResult> SearchAsync(HttpClient client, string kind, string query)
    {
        using var response = await client.GetAsync($"{Api}/{kind}/search?query={Uri.EscapeDataString(query)}");
        return await ReadAsync<DirectorySearchResult>(response, HttpStatusCode.OK);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    private static async Task<(string Title, string Detail)> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var problem = await ReadAsync<JsonElement>(response, expected);
        return (problem.GetProperty("title").GetString()!, problem.GetProperty("detail").GetString()!);
    }
}

/// <summary>
/// The client for an external identity server's directory API, against a fake server that answers
/// as the identity server does: camelCase records, problem details for a refusal or an outage.
/// </summary>
public sealed class DirectoryAccountsExternalClientTests
{
    private const string Authority = "https://login.example/IdentityServer";
    private const string Key = "a-test-key-of-24-chars!!";

    private const string People = """
        [
          {"sid":"S-1-5-21-10-20-30-1001","guid":"6f1c0f3e-5d9a-4c2b-9a6e-2b4a7f3d0c01","accountName":"DOMAIN\\Test.User","userName":"Test.User",
           "displayName":"Test User","givenName":"Test","surname":"User","email":"test.user@example.invalid","telephone":null,"mobile":null,"fax":null,
           "office":null,"title":"Test Role","department":"Test Team","employeeId":null,"enabled":true},
          {"sid":"S-1-5-21-10-20-30-1002","guid":"6f1c0f3e-5d9a-4c2b-9a6e-2b4a7f3d0c02","accountName":"DOMAIN\\Test.Old","userName":"Test.Old",
           "displayName":"Test Old","givenName":null,"surname":null,"email":null,"telephone":null,"mobile":null,"fax":null,
           "office":null,"title":null,"department":null,"employeeId":null,"enabled":false}
        ]
        """;

    [Fact]
    public async Task A_search_asks_the_whole_directory_with_the_key_and_keeps_enabled_people()
    {
        var server = new FakeServer(_ => Answer(HttpStatusCode.OK, People));
        var people = await Client(server, Key).SearchAsync("test user", 20, TestContext.Current.CancellationToken);

        var person = Assert.Single(people);
        Assert.Equal("S-1-5-21-10-20-30-1001", person.Sid);
        Assert.Equal("DOMAIN\\Test.User", person.AccountName);
        Assert.Equal("Test.User", person.UserName);
        Assert.Equal("Test User", person.DisplayName);
        Assert.Equal("test.user@example.invalid", person.Email);
        Assert.Equal("Test Team", person.Department);
        Assert.Equal("Test Role", person.Title);
        var request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{Authority}/directory/users?query=test%20user&take=20&scope=global&includeDisabled=true", request.RequestUri!.AbsoluteUri);
        Assert.Equal(Key, Assert.Single(request.Headers.GetValues(IdentityServerAccountDirectory.ApiKeyHeader)));
    }

    [Fact]
    public async Task A_lookup_reads_the_detail_and_treats_404_as_nobody()
    {
        const string detail = """
            {"groups":["Test Team Users","Report Users"],"sid":"S-1-5-21-10-20-30-1001","guid":"6f1c0f3e-5d9a-4c2b-9a6e-2b4a7f3d0c01",
             "accountName":"DOMAIN\\Test.User","userName":"Test.User","displayName":"Test User","email":null,"enabled":true}
            """;
        var server = new FakeServer(request => request.RequestUri!.AbsolutePath.EndsWith("-1001", StringComparison.Ordinal)
            ? Answer(HttpStatusCode.OK, detail)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var directory = Client(server, apiKey: null);

        var person = await directory.FindAsync("S-1-5-21-10-20-30-1001", TestContext.Current.CancellationToken);
        Assert.NotNull(person);
        Assert.Equal("DOMAIN\\Test.User", person.AccountName);
        Assert.Null(person.Email);
        Assert.Equal(new[] { "Test Team Users", "Report Users" }, person.Groups);
        Assert.Null(await directory.FindAsync("S-1-5-21-10-20-30-1009", TestContext.Current.CancellationToken));
        Assert.Equal($"{Authority}/directory/users/S-1-5-21-10-20-30-1001", server.Requests[0].RequestUri!.AbsoluteUri);
        // Without a key the site authenticates as itself, so no key header is sent.
        Assert.All(server.Requests, request => Assert.False(request.Headers.Contains(IdentityServerAccountDirectory.ApiKeyHeader)));
        await Assert.ThrowsAsync<DirectoryQueryException>(() => directory.FindAsync("DOMAIN\\Test.User", TestContext.Current.CancellationToken));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Refusals_and_outages_become_the_shared_exceptions()
    {
        var refused = Client(new FakeServer(_ => Answer(HttpStatusCode.BadRequest,
            """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"The directory request was refused.","status":400,"detail":"scope must be local or global."}""")), Key);
        Assert.Equal("scope must be local or global.", (await Assert.ThrowsAsync<DirectoryQueryException>(() => refused.SearchAsync("test", 20, TestContext.Current.CancellationToken))).Message);

        var down = Client(new FakeServer(_ => Answer(HttpStatusCode.ServiceUnavailable,
            """{"title":"The directory is unavailable.","status":503,"detail":"Active Directory could not be queried: The server is not operational."}""")), Key);
        var outage = await Assert.ThrowsAsync<DirectoryUnavailableException>(() => down.SearchAsync("test", 20, TestContext.Current.CancellationToken));
        Assert.Contains("The server is not operational.", outage.Message);
        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => down.FindAsync("S-1-5-21-10-20-30-1001", TestContext.Current.CancellationToken));

        var wrongKey = Client(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), Key);
        Assert.Contains("IdentityServer:DirectoryApiKey", (await Assert.ThrowsAsync<DirectoryUnavailableException>(() => wrongKey.SearchAsync("test", 20, TestContext.Current.CancellationToken))).Message);

        var unreachable = Client(new FakeServer(_ => throw new HttpRequestException("No such host is known.")), Key);
        Assert.Contains("No such host is known.", (await Assert.ThrowsAsync<DirectoryUnavailableException>(() => unreachable.SearchAsync("test", 20, TestContext.Current.CancellationToken))).Message);

        var garbled = Client(new FakeServer(_ => Answer(HttpStatusCode.OK, "<html>Sign in</html>")), Key);
        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => garbled.SearchAsync("test", 20, TestContext.Current.CancellationToken));

        // A term that is too short never reaches the server.
        var server = new FakeServer(_ => Answer(HttpStatusCode.OK, "[]"));
        await Assert.ThrowsAsync<DirectoryQueryException>(() => Client(server, Key).SearchAsync("b", 20, TestContext.Current.CancellationToken));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void The_handler_uses_the_sites_Windows_identity_only_without_a_key()
    {
        using var withKey = Assert.IsType<HttpClientHandler>(IdentityServerAccountDirectory.CreateHandler(new IdentityServerSettings { DirectoryApiKey = Key }));
        Assert.False(withKey.UseDefaultCredentials);
        Assert.Null(withKey.ServerCertificateCustomValidationCallback);
        using var withoutKey = Assert.IsType<HttpClientHandler>(IdentityServerAccountDirectory.CreateHandler(new IdentityServerSettings { AcceptAnyServerCertificate = true }));
        Assert.True(withoutKey.UseDefaultCredentials);
        Assert.NotNull(withoutKey.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void The_source_follows_the_identity_server_and_the_local_accounts_setting()
    {
        var embedded = new IdentityServerSettings();
        var external = new IdentityServerSettings { Authority = Authority };
        Assert.Equal(AccountDirectorySource.LocalAccounts, AccountDirectorySettings.Choose(new AccountDirectorySettings { UseLocalAccounts = true }, external));
        Assert.Equal(AccountDirectorySource.IdentityServer, AccountDirectorySettings.Choose(new AccountDirectorySettings(), external));
        Assert.Equal(AccountDirectorySource.ActiveDirectory, AccountDirectorySettings.Choose(new AccountDirectorySettings { UseLocalAccounts = false }, embedded));
        Assert.Equal(AccountDirectorySettings.IsOutsideDomain ? AccountDirectorySource.LocalAccounts : AccountDirectorySource.ActiveDirectory,
            AccountDirectorySettings.Choose(new AccountDirectorySettings(), embedded));
    }

    private static IdentityServerAccountDirectory Client(FakeServer server, string? apiKey)
        => new(new FakeHttpClientFactory(server), Microsoft.Extensions.Options.Options.Create(new IdentityServerSettings { Authority = Authority, DirectoryApiKey = apiKey }));

    private static HttpResponseMessage Answer(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, status == HttpStatusCode.OK ? "application/json" : "application/problem+json") };

    /// <summary>Answers every request through a function and keeps the requests for inspection.</summary>
    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
