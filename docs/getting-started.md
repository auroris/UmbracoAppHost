# Run a website

The solution has two application projects:

| Project | Purpose |
|---|---|
| `src/UmbracoAppHost` | The reusable library: Umbraco, uSync export, views, browser assets, authentication, forms and reports |
| `src/UmbracoAppHost.Web` | The website launcher, using the same startup calls as any other host |

The tests are separate. Interactive Reports, IdentityServer.Core and Pressroom.Client remain
independent dependencies; their source projects build through project references without being
listed separately in this solution. People-picker directory lookups are copied into this repository.

## Open the solution and press Play

Open `UmbracoAppHost.slnx` in Visual Studio and use the **Website** launch profile. It opens
`http://localhost:5080/`. If Visual Studio remembers another startup selection, choose
**Website** once from the Play dropdown.

The first build restores and builds both the frontend and Interactive Reports' browser assets
automatically. Building from source requires the .NET 10 SDK, Node 20 or newer, and four sibling
checkouts next to this repository's folder:

| Checkout | Provides |
|---|---|
| `..\AppBlocks` | Forms and data sources, plus the `AppBlocks.InteractiveReports` and `AppBlocks.Documents` companions. |
| `..\Interactive-Reports` | Interactive Reports, compiled through `AppBlocks.InteractiveReports` with its browser assets |
| `..\IdentityServer` | `IdentityServer.Core` for the built-in identity server, including its `ActiveDirectory` dependency. |
| `..\Pressroom` | `Pressroom.Client`, used by `AppBlocks.Documents` for paper-report rendering. |

To build against checkouts elsewhere, pass their folders as MSBuild properties, for example
`dotnet build -p:AppBlocksRoot=D:\src\AppBlocks -p:InteractiveReportsRoot=D:\src\Interactive-Reports -p:IdentityServerRoot=D:\src\IdentityServer -p:PressroomRoot=D:\src\Pressroom`.
The required people-search and account-lookup code is the site's own, in
`src/UmbracoAppHost/DirectoryAccounts/Lookup/`.
Later builds reuse the frontend outputs until their sources change.

Startup creates the local instance under `reference/`, installs Umbraco, and imports the saved
uSync export with its published welcome page. Use **Open the backoffice** on that page to reach `/umbraco/`.
The **Website** launch profile uses Development settings: sign in as **admin** with any
nonempty password. `appsettings.Development.json` enables `Umbraco:CMS:Hosting:Debug`,
and the configured debug login flow accepts any nonempty password for an existing account.

Startup configures the built-in administrator (ID `-1`) with username `admin` and email
`admin@localhost.local`, including in existing databases. `UmbracoAppHost:AdminUserName`
and `UmbracoAppHost:AdminEmail` in the website's `appsettings.json` define those values
and also apply on file saves. Set either to an empty string to stop managing that field.

Outside debug mode, the default shows the login form and requires the real password.
For a new instance, the random generated password is in `reference/instance.json`, under
`Umbraco.CMS.Unattended.UnattendedUserPassword`. Existing passwords are preserved unless
you configure a replacement.

To choose or reset the administrator password, open the website's `appsettings.json`,
enter it under `UmbracoAppHost:AdminPassword`, and save:

```json
"UmbracoAppHost": {
  "AdminUserName": "admin",
  "AdminEmail": "admin@localhost.local",
  "AdminPassword": "Your chosen password here"
}
```

The password takes effect as soon as the running site reloads the saved file.
No IIS restart is needed. It is also applied whenever the site starts,
including on an existing database. It unlocks the administrator, resets failed login
attempts to zero and removes two-factor enrolment. Saving the same valid password also
recovers a locked account without rotating its password hash. Sign in as `admin`, or the
configured `AdminUserName`.
The setting targets the original administrator even if that account has been renamed.

Leave the value empty or remove the setting to stop applying it. That leaves the account's
current password, lockout state and two-factor enrolment in place; it does not restore the
generated password.
The password must meet the site's Umbraco password rules. If it is rejected, the existing
password and lockout state remain and the application log explains what to correct. Save
a corrected value to try again.
The password itself is not written to the log.

The same `appsettings.json` defines the default login flow:

```json
"BackOfficeLogin": {
  "DefaultFlow": "Auto",
  "DebugFlow": "Debug"
}
```

Both values accept `Auto`, `Form`, `IdentityServer`, or `Debug`. `Auto` prefers the
configured identity server and falls back to local sign-in if it is disabled or fails. `DebugFlow` overrides
`DefaultFlow` only while `Umbraco:CMS:Hosting:Debug` is true; set it to `Form` to require
real passwords during development, or `null` to use `DefaultFlow`. `Debug` outside debug
mode falls back to `Form`. Debug bypasses password, account lockout and two-factor checks for
an existing backoffice account, accepting any nonempty password.
`IdentityServer` attempts automatic sign-in and requires the provider's
`Enabled` and `BackOfficeEnabled` settings to be true. Restart after changing login flows
or provider settings. See the [authentication settings](../README.md#never-locked-out)
for provider configuration and logout behavior.

For emergency access on a deployed site, set `Umbraco:CMS:Hosting:Debug` to `true` and
`BackOfficeLogin:DebugFlow` to `Debug`, then restart. This works in the Production environment
too. Signing in lifts the account's lockout but leaves its password and two-factor enrolment
alone. Repair the account in **Users**, then restore the normal settings and restart.

Member login also prefers the identity server and falls back to the native password form.
New SSO/directory accounts receive random passwords; administrators must set usable local
passwords in **Users** and **Members** when people need manual access. SSO never overwrites
those passwords. The development backoffice default remains `Debug`.

uSync imports English (`en`, default), French (`fr`), the starter dictionary, document types,
the shared building blocks and the sample content. The sample pages are English and no domains
are assigned, so `/` serves the first root page, the Sample Application. Use a culture-variant
page's culture selector in the backoffice to edit each language. Repeated starts keep the
existing database and editor-owned content. Existing sites receive definition changes through an
explicit uSync import.

The equivalent command is:

```powershell
dotnet run --project src/UmbracoAppHost.Web
```

## Reset the reference

Stop the website, then run this in PowerShell 7 from the repository:

```powershell
.\reset.ps1
```

This deletes the reference instance's database, uploaded media, sign-in keys, generated
identity/administrator credentials, working uSync exports and Umbraco temporary caches, then
copies the saved uSync export from `src/UmbracoAppHost/uSync/` into `reference/uSync/`.
Source files and project configuration are untouched. Press Play again to import the saved
site into a fresh database.

Before deleting anything, the script compares `reference/uSync/` with the saved export.
Backoffice edits that were exported but never copied into the saved export are listed, and
the script asks whether to discard them. Answer **No**, copy the files you want to keep into
`src/UmbracoAppHost/uSync/`, and run it again. `-Force` skips the question;
`-WhatIf` only shows what would happen. A running instance or an external database is rejected.

Reset is for reference development; created sites use normal startup and updates. See
[uSync exports and the saved export](../README.md#usync-exports-and-the-saved-export) for the
two folders and the steps to save a change.

## Use the library in another repository

A downstream host is a web project in its own repository that references the library. The
library brings Umbraco, both shells, the blocks and their document types, the saved export and
the views; the host adds its own content, templates, code and settings. What the library ships is
replaced when upstream changes, never edited in the host: a change it needs is made in this
repository and brought down. `deployment/test-downstream.ps1` creates a disposable host with
everything below and runs it through each step; its project is a worked example.

### The project

```text
MyHost\
  .gitignore
  MyHost.slnx
  src\MyHost.Web\
    MyHost.Web.csproj         the project reference to the library
    Program.cs
    appsettings.json          InstanceRoot, UmbracoAppHost:Samples, the host's settings
    uSync\                    the host's committed export: its content, dictionary keys and templates
    Views\                    the host's own templates, beside the copies of the library's views
    Schema\sqlite\, Schema\sqlserver\   the host's tables, when it has a database of its own
  instance\                   git-ignored: instance.json, data\, media\ and the working uSync\ folder
```

The project file is the reference launcher's, with its reference pointing at the library in the
UmbracoAppHost checkout:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <CompressionEnabled>false</CompressionEnabled>
    <RazorCompileOnBuild>false</RazorCompileOnBuild>
    <RazorCompileOnPublish>false</RazorCompileOnPublish>
    <CopyRazorGenerateFilesToPublishDirectory>true</CopyRazorGenerateFilesToPublishDirectory>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\UmbracoAppHost\src\UmbracoAppHost\UmbracoAppHost.csproj" />
  </ItemGroup>
</Project>
```

Templates are compiled when they are first rendered, as Umbraco needs for templates edited in the
backoffice, and published as files. In Visual Studio, add the library to the host's solution with
**Add > Existing Project** so that it builds with the host. The library's own dependencies,
Interactive Reports, the IdentityServer directory libraries and the Pressroom client, build
through its project references and are not added.

`Program.cs` is the reference launcher's:

```csharp
using UmbracoAppHost;

var builder = WebApplication.CreateBuilder(args);
builder.AddUmbracoAppHost();

await using var app = builder.Build();
await app.UseUmbracoAppHostAsync();
await app.RunAsync();
```

A host with registrations of its own keeps them on its `Program` class, which implements
`IUmbracoAppHost` and is passed to both calls, so that `Program.cs` and a test site (see
[Test a host](#test-a-host)) apply the same ones. `ConfigureServices` registers services before the
library's, `ConfigureConnections` is the connections callback, and `ConfigureMiddleware` and
`ConfigureEndpoints` add to Umbraco's pipeline; a member the host leaves out does nothing. A host
with tables of its own registers its database in `ConfigureConnections`, with idempotent schema
scripts in `Schema/` (see [Application databases](../README.md#application-databases)):

```csharp
using UmbracoAppHost;
using AppBlocks.Connections;
using AppBlocks.Databases;

var builder = WebApplication.CreateBuilder(args);
builder.AddUmbracoAppHost<Program>();

await using var app = builder.Build();
await app.UseUmbracoAppHostAsync<Program>();
await app.RunAsync();

public partial class Program : IUmbracoAppHost
{
    public static void ConfigureConnections(WebApplicationBuilder builder, AppBlocksBuilder connections)
        => connections.AddApplicationDatabase("MyHostDb", ApplicationSchema.FromDirectory(Path.Combine(builder.ProjectRoot(), "Schema")));
}
```

`builder.ProjectRoot()` is the folder the host's own files are read from: the content root, unless
`UmbracoAppHost:ProjectRoot` names another folder, as a test site does.

A connection registered this way appears in Settings → AppBlocks → Connections as read-only, and
forms, data sources and reports all use it by name; a host needs no Interactive Reports type for it.
A host that customizes the report engine itself, with a row restriction callback for instance, does
so from a composer: `builder.AddAppBlocksInteractiveReports(reports => reports.UseRowRestrictions(...))`.

The scripts are not content files; add `<None Update="Schema\**\*.sql" CopyToPublishDirectory="PreserveNewest" />`
to an `ItemGroup` of the project file so that a published site carries them.

`appsettings.json` holds what can be committed:

```json
{
  "InstanceRoot": "../../instance",
  "UmbracoAppHost": {
    "Samples": false
  },
  "Attachments": {
    "Connection": "MyHostDb"
  }
}
```

`InstanceRoot` is resolved against the project folder. `../../instance` puts the instance at the
root of the repository, outside the project folder, so that builds and publishes never pick up
its files. `UmbracoAppHost:Samples` set to `false` leaves out the sample content, the
`sample.orders` resource and the `orders` report. `Attachments:Connection` keeps
[record attachments](../README.md#record-attachments) in the host's own database, so that records
and their files share one backup; without it they go to `Attachments.sqlite.db` in the instance.
The host's `appsettings.json`, environment variables and command line override the library's
defaults. Secrets and connection strings for a server go in that server's `instance.json`.

`.gitignore`:

```gitignore
bin/
obj/
.vs/
instance/
umbraco/
appsettings-schema*.json
umbraco-package-schema.json
```

### Build and press Play

The library's project file finds AppBlocks, Interactive Reports, IdentityServer and Pressroom in
sibling checkouts next to the UmbracoAppHost checkout (`..\AppBlocks`, `..\Interactive-Reports`,
`..\IdentityServer`, `..\Pressroom`). When they are elsewhere, pass their folders to every build:

```powershell
dotnet build src\MyHost.Web -p:InteractiveReportsRoot=D:\src\Interactive-Reports
```

`AppBlocksRoot`, `IdentityServerRoot` and `PressroomRoot` do the same for the other three. Visual Studio passes no
such properties; set them as environment variables before starting it, or keep the checkouts side
by side.

Press Play, or run `dotnet run --project src\MyHost.Web`. The first start creates the instance
folder, installs Umbraco and imports the working uSync folder. Sign in at `/umbraco/` as `admin`
with the password in `instance/instance.json`, under `Umbraco.CMS.Unattended.UnattendedUserPassword`;
the administrator settings and login flows above apply unchanged. Enable **Launch browser** in the
launch profile if it is off.

### The host's export and the library's

The working folder, `instance/uSync/`, has no `usync.config` before the first start, and the host
fills it before uSync's first-boot import:

1. The library's export, the saved export of this repository, without the sample content when
   `UmbracoAppHost:Samples` is `false`.
2. The host's committed export, the folder `UmbracoAppHost:HostExport` names (default `uSync`,
   relative to the project folder), for every path the library does not ship. A host file at a
   path the library ships is ignored.

The first boot imports the result. The host's `uSync/` folder holds only the host's own files: its
content, its dictionary keys (prefix them with the host's name) and the registrations of its own
templates. Make them in the backoffice, where uSync writes every save to `instance/uSync/`, and
copy the files into the project's `uSync/` at the same relative paths; `usync.config` and the
library's definitions stay out. A template is its `Templates/` registration in the export plus its
view in `Views/`. The base keeps the paper report type's allowed templates equal to the installed
templates, so a host's paper report template needs no change to that type.

At every start the views the library ships are written to the host's `Views/` folder and
rewritten when they differ, so an upstream change to a view arrives with the rebuild. The host's
other views are never touched. Leave the copies as they are and commit them with the upstream
change that brought them. A publish with the site scripts takes the library's own copies, so a
stale copy in the project folder never reaches a site.

### Bring an upstream change down

Nothing upstream is merged or migrated in the host; it is replaced:

1. Pull the UmbracoAppHost checkout, and its siblings when they changed, and rebuild the host.
   Code, browser assets and the library's export arrive through the project reference, and the
   views the library ships are rewritten at the next start.
2. From the UmbracoAppHost checkout, run `.\refresh-definitions.ps1 -Instance <repository>\instance`.
   It copies the definition folders (`ContentTypes`, `DataTypes`, `Dictionary`, `Languages`,
   `MediaTypes`, `MemberTypes`, `RelationTypes` and `Templates`) of the library's export in the
   host's build output over the working folder, removes the folder's copies of the definitions
   AppBlocks installs itself, and lists what changed. `Content/` and the host's
   own files are left alone. `-WhatIf` lists without changing anything. The host can be running.
3. Start the host, open **Settings > uSync** in the backoffice, run **Report** to review the
   changes, then **Import**.
4. Run the host's tests. Where an upstream change broke a page, a property removed or a block
   renamed, fix the page.
5. Redeploy (see below).

`refresh-definitions.ps1` reads `instance/.apphost/runtime.json`, which every start writes, so the
host must have started once on the instance. The host's own database is outside uSync and outside
this routine; its schema scripts add what is missing in place.

### Reset the instance

Stop the host, then run this from the UmbracoAppHost checkout:

```powershell
.\reset.ps1 -InstanceRoot <repository>\instance
```

As for the reference, it first lists the working files that differ from what the next start would
copy. A file at a path the host's export ships is compared with the host's copy, any other with the
library's export, and a file that matches neither is listed as changed or added. A host file
missing from the working folder is listed as removed; a missing library content file is not, since
it may be a sample the host left out. The script asks before discarding them, and `-Force` and
`-WhatIf` work as for the reference. It then deletes the database, media, sign-in keys,
`instance.json`, the working uSync folder and Umbraco's temporary caches, and copies nothing: the
next start fills the working folder from both exports as the first start did, installs a fresh
database and creates new administrator credentials.

### Publish it as a site

`new-site.ps1 -Project` publishes the host instead of the reference launcher, into a
self-contained build folder with the application, the scripts and both exports together in
`app/uSync/`, and creates a site from it:

```powershell
.\new-site.ps1 -Project <repository>\src\MyHost.Web\MyHost.Web.csproj -Name MyHost -Destination E:/Sites/MyHost
```

`-Assembly` names the application's file when it is not the project's name with `.dll`, and
`-MsBuildProperties '-p:InteractiveReportsRoot=D:\src\Interactive-Reports'` passes the sibling
checkouts' folders to the publish. Because the host turns the samples off, the site's uSync folder
starts empty and its first start fills it from both exports. The copy of `new-site.ps1` inside the
build folder creates more sites from it, on any machine with a copy of the folder. Update a site with
`update-site.ps1 -Site <site> -Project <project file>`, or with `-Build` and a newer build of the
same project. See [Deployment](deployment.md).

## Test a host

`tests/UmbracoAppHost.Testing` boots a host the way its `Program.cs` does, on a free port with a
fresh database and instance folder under a temporary directory, and waits for the unattended
install. It signs in members and backoffice users, drives forms and reports over HTTP, and reads
a saved uSync export. The library has no test-framework dependency, so any runner works; failures
are thrown as `TestSiteException`.

`TestSite.StartAsync` takes the host project's own files from its folder, which it finds by
walking up from the host assembly's location in the test output: the first folder that holds
`<assembly name>.csproj`, or else the one project of that name under the first folder above with a
`*.slnx`, a `*.sln` or `.git` (outside `bin`, `obj`, `node_modules` and dot folders).
`TestSiteOptions.ProjectFolder` names it instead. From that folder the site:

- reads `appsettings.json` and `appsettings.<EnvironmentName>.json` (`Development` unless
  `TestSiteOptions.EnvironmentName` says otherwise) just before the content root's, so environment
  variables still override them. The project's `UmbracoAppHost:AdminPassword` is ignored, so the
  administrator keeps `TestSiteOptions.AdminPassword`. The site's own settings come after: the
  instance folder, the Umbraco database, HTTPS and uSync's startup import and export.
  `TestSiteOptions.Settings` comes last. Any other setting, such as a connection string in the
  development settings, applies as written;
- sets `UmbracoAppHost:ProjectRoot` to the project, so `UmbracoAppHost:HostExport` (default
  `uSync`) is the project's committed export, imported on first boot, and paths the host takes
  from `builder.ProjectRoot()` are the project's;
- copies the project's `Views/` into the content root, which is a temporary folder, so the
  host's templates and partials render while the library's views are written beside them;
- applies the host's `IUmbracoAppHost` registrations, from the `Program` class or else the one
  class in the host's assembly that implements it (`TestSiteOptions.Host` names another), through
  `AddUmbracoAppHost<THost>` and `UseUmbracoAppHostAsync<THost>`. The host's `RunAsync` never runs.

The unattended install and uSync's first-boot import take most of a new site's time, so a test
process does them once for each set of options and gives every site its own copy, which is deleted
with the site. What a test changes stays in its copy. A site that names `TestSiteOptions.Root` or
`ConfigureConnections`, or sets `UseInstallSnapshot` to `false`, installs for itself; use that for a
test of first boot. `ConfigureBuilder` runs only on the copy, so a registration that the first
import depends on also needs `UseInstallSnapshot = false`.

Add `public partial class Program { }` at the end of the host's `Program.cs` so tests can name its
assembly; a host with registrations of its own already has it, as `IUmbracoAppHost`. The test
project references the host and the library:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsTestProject>true</IsTestProject>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.2" />
    <ProjectReference Include="..\MyHost\MyHost.csproj" />
    <ProjectReference Include="C:\path\to\UmbracoAppHost\tests\UmbracoAppHost.Testing\UmbracoAppHost.Testing.csproj" />
  </ItemGroup>
</Project>
```

Use the .NET 10 SDK and select Microsoft Testing Platform in the repository's `global.json`
(merge the `test` property into the file if it already contains SDK settings):

```json
{
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

Run `dotnet test` from the solution directory, or use `dotnet test --project path/to/MyHost.Tests.csproj`
for one project. xUnit v3 supplies the test runner and Visual Studio Test Explorer integration.
The host defaults exclude `xunit.*` assemblies from Umbraco's plugin discovery: xUnit ships
runner-only dependencies beside the test executable that are not application dependencies.

A fixture starts one site for a class of tests. This one signs in a member, saves an order through
the sample Orders form and reads the `orders` report:

```csharp
using AppBlocks.Blocks;
using UmbracoAppHost.Testing;
using Xunit;

public sealed class SiteFixture : IAsyncLifetime
{
    public TestSite Site { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Site = await TestSite.StartAsync(new TestSiteOptions
    {
        Application = typeof(Program).Assembly,
    });

    public async ValueTask DisposeAsync() => await Site.DisposeAsync();
}

[Collection("Sites")]
public sealed class OrdersTests(SiteFixture fixture) : IClassFixture<SiteFixture>
{
    // The page and its form block; a host's own come from its saved export.
    private static readonly Guid OrdersFormPage = AppBlockSchema.Key("sample:orders-form");
    private static readonly Guid OrderForm = AppBlockSchema.Key("sample:form");

    [Fact]
    public async Task A_member_saves_an_order_and_reads_the_report()
    {
        var site = fixture.Site;
        var member = site.CreateMember("pilot", "pilot@example.invalid", "Test pilot");
        using var browser = await site.SignInMemberAsync(member);

        var form = site.Forms(browser, OrdersFormPage, OrderForm);
        Assert.True((await form.LoadAsync()).IsSuccess);
        var saved = await form.CreateAsync(new { customer = "Test order", region = "NORTH", status = "NEW", amount = "12.50", orderDate = "2026-10-29" });
        Assert.True(saved.IsSuccess, saved.Body.GetRawText());

        var rows = await site.Reports(browser).RowsAsync("orders");
        Assert.NotEmpty(rows.EnumerateArray());
    }
}
```

`TestSiteOptions` sets the virtual path, the first-boot uSync group, extra configuration in
`Settings`, and callbacks for services before the host's registrations, for further report
connections after the host's, and for test-only endpoints after `UseUmbracoAppHostAsync`.
`site.ProjectFolder` is the folder the site was booted from, for reading other project files. `SignInManagementApiAsync` returns a client with a management API
token, `site.Attachments(browser, page, block)` drives an Attachments block's files the way its
runtime does (list, upload, download, replace, remove, with the record's key in the query),
`site.Tabular(browser, page, block)` lists a Tabular form block's rows and saves a batch of
changes the same way (`ListAsync(query)`, then `SaveAsync(changes, query, submissionKey)` with
the list's token, answering with the rows or the failing change's `Index` and `Errors`), and
`USyncExport.ContentByAlias` reads a key from an export without a running site.
Umbraco keeps some state in static fields, which starting and disposing a site reset, so a test
process runs one site at a time: put every class that starts one in the same collection.

## Leftover folders from the project simplification

After stopping Visual Studio, these folders can be deleted if they still exist:

| Folder in this repository | Reason |
|---|---|
| `src/FourWing.Frontend` | Retired packaging project; only generated build output remains |
| `src/UmbracoAppHost.Web/Sample` | Empty; sample resources moved into the library |
| `src/UmbracoAppHost.Web/Publishing` | Empty; publishing helpers moved into the library |
| `artifacts/startup-validation` | Optional cleanup of disposable verification sites and local test packages |
| `artifacts/releases` | Output of the retired `release.ps1`; publishes now go to `artifacts/builds/` |

The root `templates` folder is already absent. Keep `Frontend` and `src/UmbracoAppHost`;
they contain the active frontend sources and reusable library.
