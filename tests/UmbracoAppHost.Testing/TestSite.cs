using System.Diagnostics;
using AurorisNet.Umbraco.ApplicationVirtualPathFix;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Authentication;
using UmbracoAppHost.Hosting;
using File = System.IO.File;

namespace UmbracoAppHost.Testing;

/// <summary>
/// One real site for a test: the host's own <c>AddUmbracoAppHost</c> on Kestrel at a free port, with its
/// own SQLite database and instance folder under a temporary root, returned once the unattended
/// install has finished. The install normally comes from a copy of one made once per process; see
/// <see cref="TestSiteOptions.UseInstallSnapshot"/>. Umbraco keeps some state in static fields, which
/// starting and disposing a site reset, so a process runs one site at a time.
/// </summary>
public sealed partial class TestSite : IAsyncDisposable
{
    private const string BackOfficeApi = "/umbraco/management/api/v1/security/back-office";
    private const string LoginRoute = "/__tests/login";

    // The host's registrations are applied through the library's generic calls, for a type found at run time.
    private static readonly MethodInfo AddHost = typeof(UmbracoAppHostExtensions).GetMethods()
        .Single(method => method is { Name: nameof(UmbracoAppHostExtensions.AddUmbracoAppHost), IsGenericMethodDefinition: true });
    private static readonly MethodInfo UseHost = typeof(UmbracoAppHostExtensions).GetMethods()
        .Single(method => method is { Name: nameof(UmbracoAppHostExtensions.UseUmbracoAppHostAsync), IsGenericMethodDefinition: true });

    private bool disposed;
    private bool deleteRoot;

    private TestSite(WebApplication app, string root, string projectFolder, string adminPassword)
    {
        App = app;
        Root = root;
        ProjectFolder = projectFolder;
        AdminPassword = adminPassword;
        Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
            ?? throw new TestSiteException("Kestrel reported no address for the started site."));
        PathBase = VirtualPathHosting.Prefix(app.Configuration) ?? "";
        InstanceRoot = app.Services.GetRequiredService<AppHostInstance>().Root;
        DataRoot = app.Configuration["UmbracoAppHost:DataRoot"]!;
        USyncFolder = Path.GetFullPath(app.Configuration["uSync:Settings:RootFolder"]!, app.Environment.ContentRootPath);
        AdminUserName = (app.Configuration[ConfiguredAdminPassword.UserNameConfigurationKey] ?? "admin").Trim() is { Length: > 0 } name ? name
            // Without a configured name the administrator keeps the unattended install's, which is its email.
            : app.Configuration["Umbraco:CMS:Unattended:UnattendedUserEmail"] ?? "";
    }

    public WebApplication App { get; }
    public IServiceProvider Services => App.Services;

    /// <summary>The Kestrel address on a free loopback port.</summary>
    public Uri Address { get; }

    /// <summary>The virtual path, such as <c>/wing</c>, or empty; the clients put it before every path.</summary>
    public string PathBase { get; }

    /// <summary>The content root, which holds the Umbraco database, the instance folder and the views.</summary>
    public string Root { get; }

    /// <summary>The host project the site took its settings, export and views from.</summary>
    public string ProjectFolder { get; }

    /// <summary>The library's <c>InstanceRoot</c>: <c>instance.json</c>, <c>data/</c>, <c>media/</c> and <c>uSync/</c>.</summary>
    public string InstanceRoot { get; }

    /// <summary>The instance's data folder: Umbraco's SQLite database, and beside it the files of the registered application connections.</summary>
    public string DataRoot { get; }

    /// <summary>
    /// The SQLite file a connection registered with <c>AddApplicationDatabase</c> opens when nothing configures it, as AppBlocks
    /// resolves it: beside Umbraco's SQLite database, or an existing file under <c>App_Data</c> from before that.
    /// </summary>
    public string ApplicationDatabaseFile(string fileName) => AppBlocks.Databases.ApplicationDatabase.DataFile(Services, fileName);

    /// <summary>The working uSync folder the site imported on first boot.</summary>
    public string USyncFolder { get; }

    public string AdminUserName { get; }
    public string AdminPassword { get; }

    /// <summary>
    /// Builds the host as its <c>Program.cs</c> does, starts it, and waits for the unattended install. The content
    /// root is a temporary folder that receives a copy of the project's <c>Views/</c>; the project's
    /// <c>appsettings.json</c> and <c>appsettings.&lt;EnvironmentName&gt;.json</c> are read before the content
    /// root's, and <c>UmbracoAppHost:ProjectRoot</c> names the project, so its <c>uSync/</c> export and the paths
    /// its registrations take from <c>ProjectRoot()</c> are the project's own.
    /// </summary>
    public static async Task<TestSite> StartAsync(TestSiteOptions options, CancellationToken ct = default)
    {
        var name = options.Application.GetName().Name!;
        var project = Path.GetFullPath(options.ProjectFolder ?? FindProjectFolder(options.Application)
            ?? throw new TestSiteException($"No {name}.csproj was found above {ApplicationFolder(options.Application)} or in the solution "
                + "or repository folder above it; set TestSiteOptions.ProjectFolder to the host project's folder."));
        var host = options.Host ?? FindHost(options.Application);
        if (host is not null && !typeof(IUmbracoAppHost).IsAssignableFrom(host))
            throw new TestSiteException($"TestSiteOptions.Host {host} does not implement {nameof(IUmbracoAppHost)}.");
        var root = Directory.CreateDirectory(options.Root ?? Directory.CreateTempSubdirectory("apphost-test-").FullName).FullName;
        // A root the test chose may hold files of its own, and connections can change what the import publishes.
        if (options is { UseInstallSnapshot: true, Root: null, ConfigureConnections: null }) await CopyInstallSnapshotAsync(options, project, host, root, ct);
        // Umbraco renders templates from the content root's views; a view the test already wrote there stays.
        CopyMissingFiles(Path.Combine(project, "Views"), Path.Combine(root, "Views"));
        ResetStatics();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root, ApplicationName = name, EnvironmentName = options.EnvironmentName,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        UseProjectSettings(builder, project);
        var settings = new Dictionary<string, string?>
        {
            [AppHostProject.RootSetting] = project,
            ["InstanceRoot"] = Path.Combine(root, "instance"),
            // Without pooling, sites started one after another in a process never hold each other's files.
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = options.AdminPassword,
            ["Umbraco:CMS:Global:UseHttps"] = "false",
            ["Umbraco:CMS:Hosting:Debug"] = "false",
            ["uSync:Settings:ImportAtStartup"] = "None",
            ["uSync:Settings:ExportOnSave"] = "None",
            ["uSync:Settings:FirstBootGroup"] = options.FirstBootGroup,
        };
        if (!string.IsNullOrEmpty(options.VirtualPath)) settings[VirtualPathHosting.SettingKey] = options.VirtualPath;
        foreach (var (key, value) in options.Settings) settings[key] = value;
        builder.Configuration.AddInMemoryCollection(settings);
        options.ConfigureBuilder?.Invoke(builder);
        if (host is null) builder.AddUmbracoAppHost(options.ConfigureConnections);
        else Invoke(AddHost, host, builder, options.ConfigureConnections);
        UmbracoTestSupport.UseSingleServer(builder.Services);

        var app = builder.Build();
        try
        {
            if (host is null) await app.UseUmbracoAppHostAsync();
            else await (Task<WebApplication>)Invoke(UseHost, host, app)!;
            if (options.TestLoginRoute) MapTestLogin(app);
            options.ConfigureApp?.Invoke(app);
            await app.StartAsync(ct);
            await UmbracoTestSupport.WaitForInstallAsync(app.Services, options.InstallTimeout);
            var site = new TestSite(app, root, project, options.AdminPassword) { deleteRoot = options.Root is null };
            if (options.WaitForAdministratorRename) await site.WaitForAdministratorAsync(ct);
            return site;
        }
        catch
        {
            // The start failure is the one to report, not a failure to stop a half-started site.
            try { await ShutDownAsync(app); } catch { }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await ShutDownAsync(App);
        if (deleteRoot) TryDelete(Root);
    }

    public IServiceScope CreateScope() => App.Services.CreateScope();

    /// <summary>Waits until the content node is published, as a first-boot import or a publish leaves it.</summary>
    public async Task WaitForPublishedAsync(Guid contentKey, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var waited = Stopwatch.StartNew();
        IContent? content;
        while (true)
        {
            using (var scope = CreateScope()) content = scope.ServiceProvider.GetRequiredService<IContentService>().GetById(contentKey);
            if (content?.Published == true) return;
            if (waited.Elapsed >= limit) break;
            await Task.Delay(100);
        }
        throw new TestSiteException(content is null
            ? $"Content {contentKey} does not exist after {limit}."
            : $"Content {contentKey} ({content.Name}) is not published after {limit}.");
    }

    /// <summary>A browser stand-in with its own cookies that does not follow redirects unless asked to.</summary>
    public HttpClient CreateClient(bool allowRedirects = false)
        => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = allowRedirects })
        {
            BaseAddress = Address,
            Timeout = TimeSpan.FromSeconds(30),
        };

    /// <summary>A client with the member cookie for this member, issued by the test login route without a password.</summary>
    public Task<HttpClient> SignInMemberAsync(IMember member) => SignInMemberAsync(member.Id, member.Name ?? member.Username);

    public async Task<HttpClient> SignInMemberAsync(int memberId, string name = "Test member")
    {
        var client = CreateClient();
        try
        {
            using var response = await client.GetAsync($"{PathBase}{LoginRoute}/{memberId}?scheme=member&name={Uri.EscapeDataString(name)}");
            if (!response.IsSuccessStatusCode)
                throw new TestSiteException($"The test login route returned {(int)response.StatusCode} {response.StatusCode} for member {memberId}; it is mapped only when TestSiteOptions.TestLoginRoute is true.");
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>
    /// A client signed in with the backoffice login form's endpoint. Its cookie is enough to open
    /// application pages and forms as a backoffice Administrator, but not to call the management API.
    /// </summary>
    public async Task<HttpClient> SignInBackOfficeAsync(string username, string password)
    {
        var client = CreateClient();
        try
        {
            using var login = await client.PostAsJsonAsync(PathBase + BackOfficeApi + "/login", new { username, password });
            if (!login.IsSuccessStatusCode)
                throw new TestSiteException($"The backoffice login of {username} returned {(int)login.StatusCode} {login.StatusCode}: {await login.Content.ReadAsStringAsync()}");
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>
    /// Signs in the way the backoffice does: the password login sets the backoffice cookie, and the
    /// PKCE authorization code flow of the <c>umbraco-back-office</c> client issues the management
    /// API token. Umbraco keeps the code and the token in HTTP-only cookies and hands out a redacted
    /// placeholder, which the cookie container and the bearer header carry back.
    /// </summary>
    public async Task<HttpClient> SignInManagementApiAsync(string username, string password)
    {
        var client = await SignInBackOfficeAsync(username, password);
        try
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var redirectUri = new Uri(Address, PathBase + "/umbraco/oauth_complete").ToString();
            using var authorize = await client.GetAsync(QueryHelpers.AddQueryString(PathBase + BackOfficeApi + "/authorize", new Dictionary<string, string?>
            {
                ["client_id"] = Constants.OAuthClientIds.BackOffice,
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri,
                ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["code_challenge_method"] = "S256",
                ["state"] = "test-site",
            }));
            var code = authorize is { StatusCode: HttpStatusCode.Redirect, Headers.Location: { } location }
                ? QueryHelpers.ParseQuery(new Uri(Address, location).Query)["code"].ToString() : "";
            if (string.IsNullOrEmpty(code))
                throw new TestSiteException($"Authorize for {username} returned {(int)authorize.StatusCode} {authorize.StatusCode} to {authorize.Headers.Location} without a code: {await authorize.Content.ReadAsStringAsync()}");

            using var token = await client.PostAsync(PathBase + BackOfficeApi + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = Constants.OAuthClientIds.BackOffice,
                ["redirect_uri"] = redirectUri,
                ["code"] = code,
                ["code_verifier"] = verifier,
            }));
            var body = await token.Content.ReadAsStringAsync();
            if (!token.IsSuccessStatusCode) throw new TestSiteException($"Token for {username} returned {(int)token.StatusCode} {token.StatusCode}: {body}");
            using var document = JsonDocument.Parse(body);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", document.RootElement.GetProperty("access_token").GetString());
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public Task<HttpClient> SignInAsAdministratorAsync() => SignInManagementApiAsync(AdminUserName, AdminPassword);

    /// <summary>Creates an approved member of the configured member type, in these member groups, which are created when missing.</summary>
    public IMember CreateMember(string username, string email, string name, params string[] groups)
    {
        using var scope = CreateScope();
        var members = scope.ServiceProvider.GetRequiredService<IMemberService>();
        var memberType = scope.ServiceProvider.GetRequiredService<IOptions<AccountsSettings>>().Value.MemberTypeAlias;
        var member = members.CreateMemberWithIdentity(username, email, name, memberType, isApproved: true);
        members.Save(member);
        if (groups.Length == 0) return member;
        // AddRole creates a group only when it is missing.
        foreach (var group in groups) members.AddRole(group);
        members.AssignRoles([member.Username], groups);
        return member;
    }

    /// <summary>Creates an approved backoffice user, as an administrator would, in these groups or else in Editors.</summary>
    public async Task<IUser> CreateUserAsync(string username, string email, string name, params string[] groupAliases)
    {
        using var scope = CreateScope();
        var groups = scope.ServiceProvider.GetRequiredService<IUserGroupService>();
        HashSet<Guid> keys = groupAliases.Length == 0 ? [Constants.Security.EditorGroupKey] : [];
        foreach (var alias in groupAliases)
            keys.Add((await groups.GetAsync(alias))?.Key ?? throw new TestSiteException($"There is no user group with the alias '{alias}'."));
        var created = await scope.ServiceProvider.GetRequiredService<IUserService>().CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel
        {
            UserName = username,
            Email = email,
            Name = name,
            Kind = UserKind.Default,
            UserGroupKeys = keys,
        }, approveUser: true);
        return created.Success ? created.Result.CreatedUser! : throw new TestSiteException($"Creating user {username} returned {created.Status}.");
    }

    /// <summary>Gives a backoffice user a password for the login form.</summary>
    public async Task SetPasswordAsync(IUser user, string password)
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        var identity = await manager.FindByIdAsync(user.Key.ToString()) ?? throw new TestSiteException($"User {user.Username} has no backoffice identity.");
        // Umbraco gives a created user a generated password, so set it the way a reset does.
        Check(await manager.ChangePasswordWithResetAsync(identity.Id, await manager.GeneratePasswordResetTokenAsync(identity), password), user.Username);
    }

    /// <summary>Gives a member a password for the member login form.</summary>
    public async Task SetPasswordAsync(IMember member, string password)
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IMemberManager>();
        var identity = await manager.FindByNameAsync(member.Username) ?? throw new TestSiteException($"Member {member.Username} has no member identity.");
        Check(string.IsNullOrEmpty(identity.PasswordHash) ? await manager.AddPasswordAsync(identity, password)
            : await manager.ChangePasswordWithResetAsync(identity.Id, await manager.GeneratePasswordResetTokenAsync(identity), password), member.Username);
    }

    /// <summary>Links a user or member to a Windows account by its SID, under the IdentityServer provider; the account's other links stay.</summary>
    public void LinkWindowsIdentity(Guid accountKey, string sid, string? scheme = null)
    {
        using var scope = CreateScope();
        scheme ??= scope.ServiceProvider.GetRequiredService<IOptions<IdentityServerSettings>>().Value.MemberScheme;
        var logins = scope.ServiceProvider.GetRequiredService<IExternalLoginWithKeyService>();
        logins.Save(accountKey, [
            .. logins.GetExternalLogins(accountKey).Where(login => login.LoginProvider != scheme)
                .Select(login => new ExternalLogin(login.LoginProvider, login.ProviderKey, login.UserData)),
            new ExternalLogin(scheme, sid),
        ]);
    }

    public FormsClient Forms(HttpClient client, Guid pageKey, Guid formKey) => new(client, PathBase, pageKey, formKey);

    public ReportsClient Reports(HttpClient client) => new(client, PathBase);

    /// <summary>The files an Attachments block on <paramref name="pageKey"/> shows; the record follows the query the calls carry.</summary>
    public AttachmentsClient Attachments(HttpClient client, Guid pageKey, Guid blockKey) => new(client, PathBase, pageKey, blockKey);

    /// <summary>Downloads a paper report; <paramref name="query"/> carries its parameters, such as <c>?id=1</c>.</summary>
    public Task<HttpResponseMessage> DownloadPaperReportAsync(HttpClient client, Guid nodeKey, string? query = null)
        => client.GetAsync(Http.WithQuery($"{PathBase}/api/paper-reports/{nodeKey}", query));

    /// <summary>Requests a page by its path under the site, such as <c>/orders-form/?id=4</c>.</summary>
    public Task<HttpResponseMessage> GetPageAsync(HttpClient client, string path) => client.GetAsync(PathBase + path);

    private async Task WaitForAdministratorAsync(CancellationToken ct)
    {
        string? current = null;
        for (var attempt = 0; attempt < 100 && current != AdminUserName; attempt++)
        {
            if (attempt > 0) await Task.Delay(100, ct);
            using var scope = CreateScope();
            current = (await scope.ServiceProvider.GetRequiredService<IUserService>().GetAsync(Constants.Security.SuperUserKey))?.Username;
        }
        if (current != AdminUserName)
            throw new TestSiteException($"The built-in administrator is still named '{current}', not '{AdminUserName}', 10 seconds after the install.");
    }

    private static void MapTestLogin(WebApplication app) => app.MapGet(LoginRoute + "/{id}", async (string id, string? scheme, string? name, HttpContext http) =>
    {
        var authentication = scheme switch
        {
            null or "member" => IdentityConstants.ApplicationScheme,
            "backoffice" => Constants.Security.BackOfficeExposedAuthenticationType,
            _ => null,
        };
        if (authentication is null) return Results.BadRequest("The scheme is member or backoffice.");
        await http.SignInAsync(authentication, new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, name ?? "Test user")], authentication)));
        // Umbraco marks the backoffice cookie Secure. A browser still keeps it from http://localhost,
        // but a cookie container never sends a Secure cookie over plain HTTP, so it goes without here.
        if (!http.Request.IsHttps)
            http.Response.Headers.SetCookie = http.Response.Headers.SetCookie.Select(cookie => SecureAttribute().Replace(cookie!, "")).ToArray();
        return Results.Ok();
    });

    [GeneratedRegex("; *secure(?=;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SecureAttribute();

    private static async Task ShutDownAsync(WebApplication app)
    {
        try { await app.StopAsync(); }
        finally
        {
            await app.DisposeAsync();
            ResetStatics();
        }
    }

    /// <summary>
    /// The folder of <c>&lt;name&gt;.csproj</c> for the application's assembly name, found by walking up from the
    /// assembly's folder: the first folder that holds the project file, or else the one project file of that name under
    /// the first folder with a <c>*.slnx</c>, a <c>*.sln</c> or <c>.git</c>, outside <c>bin</c>, <c>obj</c>,
    /// <c>node_modules</c> and dot folders. Null when there is none.
    /// </summary>
    public static string? FindProjectFolder(Assembly application)
        => FindProjectFolder(application.GetName().Name!, ApplicationFolder(application));

    /// <inheritdoc cref="FindProjectFolder(Assembly)"/>
    public static string? FindProjectFolder(string assemblyName, string startFolder)
    {
        var file = assemblyName + ".csproj";
        for (var folder = new DirectoryInfo(startFolder); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, file))) return folder.FullName;
            if (!folder.EnumerateFiles("*.slnx").Concat(folder.EnumerateFiles("*.sln")).Any() && !Path.Exists(Path.Combine(folder.FullName, ".git"))) continue;
            var found = ProjectFiles(folder, file).ToList();
            return found switch
            {
                [] => null,
                [var single] => single.DirectoryName,
                _ => throw new TestSiteException($"{found.Count} projects named {file} are under {folder.FullName}: {string.Join(", ", found.Select(match => match.FullName))}; set TestSiteOptions.ProjectFolder."),
            };
        }
        return null;
    }

    /// <summary>The application's <see cref="IUmbracoAppHost"/>: its <c>Program</c> class, or else the one class that implements it. Null when there is none.</summary>
    public static Type? FindHost(Assembly application)
    {
        if (application.EntryPoint?.DeclaringType is { } program && typeof(IUmbracoAppHost).IsAssignableFrom(program)) return program;
        Type?[] types;
        try { types = application.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { types = exception.Types; }
        var hosts = types.Where(type => type is { IsInterface: false, IsAbstract: false } && typeof(IUmbracoAppHost).IsAssignableFrom(type)).ToList();
        return hosts.Count <= 1 ? hosts.SingleOrDefault()
            : throw new TestSiteException($"{application.GetName().Name} has {hosts.Count} classes that implement {nameof(IUmbracoAppHost)}: {string.Join(", ", hosts)}; set TestSiteOptions.Host.");
    }

    private static string ApplicationFolder(Assembly application)
        => Path.GetDirectoryName(application.Location) is { Length: > 0 } folder ? folder : AppContext.BaseDirectory;

    private static IEnumerable<FileInfo> ProjectFiles(DirectoryInfo folder, string file)
    {
        if (File.Exists(Path.Combine(folder.FullName, file))) yield return new FileInfo(Path.Combine(folder.FullName, file));
        foreach (var child in folder.EnumerateDirectories())
            if (!child.Name.StartsWith('.') && child.Name is not ("bin" or "obj" or "node_modules"))
                foreach (var match in ProjectFiles(child, file)) yield return match;
    }

    /// <summary>
    /// Adds the project's <c>appsettings.json</c> and <c>appsettings.&lt;environment&gt;.json</c> just before the content
    /// root's, which a test may write, so environment variables and the site's settings still override them. The
    /// project's administrator password gives way to the site's.
    /// </summary>
    private static void UseProjectSettings(WebApplicationBuilder builder, string project)
    {
        var sources = builder.Configuration.Sources;
        var first = sources.ToList().FindIndex(source => source is JsonConfigurationSource { Path: "appsettings.json" });
        if (first < 0) first = sources.Count;
        var files = new PhysicalFileProvider(project);
        JsonConfigurationSource[] settings = [
            new() { Path = "appsettings.json", Optional = true, FileProvider = files },
            new() { Path = $"appsettings.{builder.Environment.EnvironmentName}.json", Optional = true, FileProvider = files },
        ];
        var projectOnly = new ConfigurationBuilder();
        foreach (var source in settings) projectOnly.Add(source);
        // Each insert reloads the configuration.
        if (!string.IsNullOrEmpty(projectOnly.Build()[ConfiguredAdminPassword.ConfigurationKey]))
            sources.Insert(first, new MemoryConfigurationSource { InitialData = [new(ConfiguredAdminPassword.ConfigurationKey, "")] });
        sources.Insert(first, settings[1]);
        sources.Insert(first, settings[0]);
    }

    private static void CopyMissingFiles(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static object? Invoke(MethodInfo method, Type host, params object?[] arguments)
        => method.MakeGenericMethod(host).Invoke(null, BindingFlags.DoNotWrapExceptions, null, arguments, null);

    private static void ResetStatics()
    {
        UmbracoTestSupport.ResetBackOfficePath();
        UmbracoTestSupport.ResetPublishedContentServices();
    }

    private static void Check(IdentityResult result, string account)
    {
        if (!result.Succeeded) throw new TestSiteException($"Setting the password of {account} failed: {string.Join("; ", result.Errors.Select(error => error.Description))}");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
