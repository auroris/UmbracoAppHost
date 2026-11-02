# UmbracoAppHost

A reusable Umbraco 18 library and a small website launcher. Open the solution and press Play,
or reference the library from another web project to create an independent site. Every site
gets the website and application shells, UmbNav, forms, reports and shared building blocks.
The launcher can also be published once and copied to independent instances.

Reuse of original project material requires prior written permission from the relevant
copyright holder; see [LICENSE](LICENSE). Third-party software and assets retain their
own terms and attribution. Public availability does not grant a general reuse license.

UmbracoAppHost is the reference installation: customize it here, replicate it, and keep the copies
updated from it. Backoffice edits are exported by uSync and saved with the source. Three scripts
cover the loop: `new-site.ps1` publishes the application and creates a site that imports the saved
export on its first start, `update-site.ps1` installs the current build into an existing site and
refreshes the shared definitions in its uSync folder for a backoffice import, and `reset.ps1`
starts the reference over.

See [Design: themes, blocks and data sources](docs/design-blocks-and-data-sources.md) for what
the application-building blocks and the declarative data sources will look like, and the work
breakdown to get there.
See [Getting started](docs/getting-started.md) for Visual Studio startup, resetting the reference,
and referencing the library from another repository. See [Deployment and shared frontend](docs/deployment.md) for
publishing, site creation, updates, and the Umbraco 18 frontend build.

## What a site gets

The shared host also supplies backoffice-authored form and report blocks, with member page
permissions, and forms that read and write either through a C# resource or through a
[data source](#data-sources) defined as a content node. See [Application forms and report blocks](docs/application-blocks.md)
for authoring, resource registration, the Orders example, and verification. Their pages are laid
out on a twelve-column grid with the Full width, Two columns (4/8, 6/6 or 8/4), Three columns and
Region blocks, which render in both the application and the website shell; see
[Layouts](docs/application-blocks.md#layouts) and the Sample Application's **Layouts** page.
Rich text, HTML, CSS and JavaScript blocks sit beside the fields, the CSS collected once into the
page's head and the JavaScript placed at the end of its body with the site's Content Security
Policy nonce, and field blocks can be hidden, start with a default value taken from the signed-in
person or the address, take a width and show a placeholder; see
[Content blocks](docs/application-blocks.md#content-blocks) and the **Content blocks** page.
There is no publish-time check of that code: back-office access is the trust boundary, so anyone
who can edit a block page can put HTML, CSS and JavaScript on it.

The common host's `Program.cs` uses these registration calls:

```csharp
using UmbracoAppHost;

var builder = WebApplication.CreateBuilder(args);
builder.AddUmbracoAppHost();

await using var app = builder.Build();
await app.UseUmbracoAppHostAsync();
await app.RunAsync();
```

`AddUmbracoAppHost` on the web builder creates an independent instance and registers Umbraco,
the shared services, starter import, views and sample resources. Set `UmbracoAppHost:Samples`
to `false` to leave the sample content, the `sample.orders` resource and the `orders` report out of
a downstream host. Its optional callback registers the site's own connections with AppBlocks. The
lower-level overload on `IUmbracoBuilder` remains available for hosts that manage startup themselves.
`UseUmbracoAppHostAsync` boots Umbraco and builds the
pipeline: the path base, the backoffice and website middleware with report authentication, the
workarounds in `Overrides/`, and the report endpoints under `/api/reports` and `/api/download`.
Its two optional callbacks add a site's own middleware and endpoints inside Umbraco's pipeline.

Umbraco, UmbNav, uSync and Interactive Reports come with the library. A web host needs only
the library reference and the startup calls above.

## Reference installation and sites

The launcher is `src/UmbracoAppHost.Web/`. `src/UmbracoAppHost/` contains the shared
services, blocks, uSync export, views and browser assets for .NET 10.
`Frontend/` is the source for the Umbraco 18 website and application shells.
Frontend generation writes the shared layouts into the library's `Views/` folder. The library
embeds them for every project that references it. At startup the views the library ships are
written to the host's `Views/` folder and rewritten when they differ; a host's other views are
untouched.

`src/UmbracoAppHost/uSync/` holds the shared schema, dictionary, and website/application
starter content. uSync imports the complete saved export on first boot, including titles,
language variants and schema folders. A host's own committed export, the folder
`UmbracoAppHost:HostExport` names (default `uSync`, relative to its project root, which is the
content root unless `UmbracoAppHost:ProjectRoot` names another folder), is copied into
the working folder after the library's on first boot, for every path the library does not ship.
Edit website titles in the backoffice or the saved export; the former `UmbracoAppHost:Title`
setting no longer changes content.
`Authentication/ConfiguredAccounts.cs` provisions configured accounts and groups, which are
outside this export. AppBlocks uses Umbraco’s tracked package migrations for its own definitions;
the host does not add a separate schema or content migration runner. The sample database is
initialized on first use. AppBlocks supplies the rendering, access and publishing-validation code.

In **Settings → Structure**, the starter's document types and element types live under
**Document Types → Application**. Its data types live under **Data Types → Application**,
with **Forms**, **Interactive Reports** and **Data sources** subfolders for their dedicated editors. Shared
editors and the application block grid sit directly in **Application**, and the connection picker and
the Forms container's own types in **AppBlocks**. Web Site, Web Page,
their reusable compositions, and the general website grid live under **Website**. These locations come
from AppBlocks' schemas and the saved export. Sites installed earlier keep the folders they have.
Existing sites receive the export's definition changes through an explicit uSync
import. AppBlocks installs missing definitions through Umbraco’s package importer and applies
released schema changes through tracked package migrations. Ordinary restarts preserve existing
definitions except for changes explicitly carried by a pending migration.

Fresh installations start with English (`en`, the default) and French (`fr`, falling back to
English). Umbraco's installer creates English so its content cache can initialize before uSync runs;
uSync imports the saved language definitions, including French. Hosts can override
the standard `Umbraco:CMS:InstallDefaultData:Languages` settings.
The saved export assigns no domains and its pages have English content only. `/` serves the first
root page by sort order, which is the Sample Application; uSync imports siblings in no fixed order,
so keep the sort orders when adding root pages. Assign domains to a Web Site to publish its
language variants at their own addresses.
Existing sites keep their content and domain assignments until an editor changes them or imports an export.

**Web Site and Web Page authoring.** A Web Site can be the public root for an application
overview, support and documentation, with a nested Web Site using APEX for the application.
Select **Website home** or **Application site** through Umbraco's template selector; ordinary
Web Pages inherit the nearest site's selection. Configure **Public Access** on the application
branch to protect it while leaving the overview public. Public Access is what protects a page's
blocks. Without it, this host's report blocks still need an account, but a form's C# class decides
for itself, even under a template that shows a sign-in form. **Site sign in** is available as an
explicit Web Page template for its login page. Menu entries can name the signed-in person and
offer their profile page and sign-out through replacement tokens such as `{{user.name}}`; see
[Web Sites and Web Pages](docs/application-blocks.md#web-sites-and-web-pages).

Web Site and Web Page share page blocks, sidebar content, CSS and JavaScript through the
**Web Page Content** composition. **Navigation Menu** is a separate composition attached to
Web Site. Navigation and badges use ancestor fallback without a fixed content ID; page code
accumulates from root to page. These sites can later sit beneath a splash page. See
[Web Sites and Web Pages](docs/application-blocks.md#web-sites-and-web-pages) for setup and
the **Website authoring** sample. Public Access rules are configured per installation; the
installed uSync version does not export them.

The starter dictionary in [`uSync/Dictionary/`](src/UmbracoAppHost/uSync/Dictionary/)
covers the keys currently used by the templates and block views. English values use the existing labels,
and every key, including the application and contact labels, has a French (`fr`) translation. `Search URI` is
`#` in both languages until the site's search address is supplied. uSync imports these entries for new databases.
Edit labels in the backoffice Dictionary and preserve an updated export when changing the
defaults for future sites. Existing sites receive dictionary changes through an explicit uSync import.

Open `UmbracoAppHost.slnx` in Visual Studio and press Play (the **Website** profile), or run
`dotnet run --project src/UmbracoAppHost.Web`. The reference keeps its settings, database, media
and uSync exports under `reference/`; its first-run administrator credentials are in
`reference/instance.json`.

Four scripts cover the whole loop. Run them in PowerShell 7 from this checkout:

| Script | Purpose |
|---|---|
| `new-site.ps1 -Name MyWebsite -Destination E:/Sites/MyWebsite` | Publishes the reference application and creates an independent site from it; `-Project <csproj>` publishes another host that references the library instead |
| `update-site.ps1 -Site E:/Sites/MyWebsite` | Publishes the reference application, or the `-Project` host, installs it into an existing site and refreshes the site's shared uSync definitions |
| `refresh-definitions.ps1 -Instance <folder>` | Copies the shared uSync definitions from a rebuilt host's build output into the working uSync folder of the instance it runs from its project, for a backoffice import |
| `reset.ps1` | Deletes the reference's local data so the next Play imports the saved uSync export into a fresh database |

Each publish lands in `artifacts/builds/<timestamp>-<commit>/`. Pass `-Build <folder>` to reuse
one build for several sites, or run the copies of the scripts inside that folder on a machine
without this checkout. Sites need only the ASP.NET Core 10 runtime. Their settings, data, media and
uSync folder sit outside `app/` and survive updates. Upgrade Umbraco and plugins in this checkout,
verify them in the reference, then update the sites; their database upgrades run at the next start.

AppBlocks, Interactive Reports, IdentityServer and Pressroom remain independent sibling checkouts; the build
compiles their libraries and published sites carry the resulting assemblies. IdentityServer supplies
`IdentityServer.Core` for the embedded OpenID Connect server, with its `ActiveDirectory` dependency.
The people-picker lookup code is in `src/UmbracoAppHost/DirectoryAccounts/Lookup/`; it is compiled locally
and needs no separate directory project.
The default locations are `..\AppBlocks`, `..\Interactive-Reports`, `..\IdentityServer` and `..\Pressroom`;
`AppBlocksRoot`, `InteractiveReportsRoot`, `IdentityServerRoot` and `PressroomRoot` override them. No separate
IdentityServer process or deployed sibling checkout is needed. The public frontend is built once and includes
both shells, with Razor templates targeting Umbraco 18 directly. A branded site brings its theme as
a library of its own; see [Themes](docs/design-blocks-and-data-sources.md#1-themes).

Forms, interactive reports, their reusable blocks and database-source runtime now live in
[AppBlocks](../AppBlocks/README.md). AppHost consumes the base package and its companions, and supplies its own authentication
policy, page shells, branding and sample content. AppBlocks itself uses native Forms container
and Interactive Report blocks on ordinary content pages, with report settings stored on the blocks.

The application and website grids include the current AppBlocks fields, including
[Shuttle](../AppBlocks/docs/shuttle.md), [Color picker](../AppBlocks/docs/color-picker.md) and
[Anti-spam](../AppBlocks/docs/anti-spam.md). Schedules support markers, colours, descriptions and
the Jump to menu; see the [Schedule guide](../AppBlocks/docs/schedule.md). Both shells map their
theme colours to the runtime's tokens. After updating AppBlocks, rebuild: Umbraco applies pending
package migrations at startup. Form Interface now uses the native Single Block editor. Its schema
migration updates the package’s data type, but old saved Block List values must be recreated and
republished. The saved samples use Single Block; reset the disposable reference to import them
fresh, preserving any edits you want to keep first.

## uSync exports and the saved export

The reference launcher's `appsettings.json` sets `InstanceRoot` to `../../reference`.
`InstanceConfiguration` sets uSync's `RootFolder` to `<InstanceRoot>/uSync`, so every save in
the reference backoffice exports the item's XML to `reference/uSync/` (`ExportOnSave: All`).
The host applies this location after loading configuration; changing only
`uSync:Settings:RootFolder` in `appsettings.json` does not override it.

| Folder | Purpose | Reset behavior |
|---|---|---|
| `reference/uSync/` | Working exports of the reference instance; ignored by Git | Deleted and replaced with the saved export by `reset.ps1` |
| [`src/UmbracoAppHost/uSync/`](src/UmbracoAppHost/uSync/) | The saved export in source control: what new sites import and what updates ship | Left alone |

The saved export is an ordinary uSync 18.1.2 export. `usync.config` sits alongside `Content/`,
`ContentTypes/`, `DataTypes/`, `Dictionary/`, `Domains/`, `Languages/`, and the other standard
uSync folders. Files keep their exported names, paths and keys, so you can copy an export
directly into `uSync/` and uSync can consume that folder as-is.

The export holds what is the host's: page types and templates, its website grid, the Umbraco defaults,
languages, dictionary and content. It does not hold AppBlocks' definitions: the form, field, layout and
content blocks, the Interactive Report block, the data source, list and report definition types and their
data types come from the `Schema/` folders in AppBlocks and its companions. Change those there; a copy
in this export fails `DefinitionOwnershipTests`, and uSync would refuse to import it anyway. Before any
uSync import, the first boot's included, `AppBlocksDefinitionImport` ensures missing definitions
exist, so imported content has its types even if the package migrations have not run yet. It honors
`Umbraco:CMS:PackageMigration:RunSchemaAndContentMigrations=false`; when automatic schema
migrations are disabled, the required definitions must be installed before importing content.

Two definitions overlap on purpose, listed and explained in `AppBlocksDefinitions.HostOwned`:
`DataTypes/ApplicationApplicationBlocks.config` is AppBlocks' Forms container grid with the blocks a page
grid also offers here (Interactive Report, HTML, CSS and JavaScript), and
`ContentTypes/paperreport.config` gives Documents' type this host's default template and place in the
tree. When a schema changes either of them, the tests fail until the copy here carries the change.

Every new site receives the complete snapshot. It currently includes the shared definitions,
the Sample Application with its About, Orders, Orders form, Orders interactive, Layouts, and
Content blocks pages and its English and French Orders paper reports, the Website theme demo
with its layouts, content blocks and Website authoring pages, and the Data sources root with the
Orders data sources, the Orders and Welcome report definitions and the region and status lists.
It assigns no domains. The saved export defines the starting content; there are no separate
starter presets.

Template markup continues to live in the library's `Views/` files. User and member accounts,
business/report databases and uploaded media files are separate from this XML snapshot.
The library packages the saved export and supplies a working copy when a uSync folder has
no `usync.config`; `new-site.ps1` and `reset.ps1` copy it explicitly. The library's working copy
leaves the sample content out when `UmbracoAppHost:Samples` is `false`, and on that first boot the
host's own committed export under `UmbracoAppHost:HostExport` (default `uSync`) is copied after
it, for every path the library does not ship.

uSync's built-in `ImportOnFirstBoot` is enabled with `FirstBootGroup: All`.
The saved XML determines the initial content; no C# content builders are needed.
The host supplies the Razor files the library ships before uSync imports template registrations,
and rewrites any that differ at every start; a host's other views are untouched.
uSync records its first boot in the database, so
ordinary restarts and updates never reimport anything on their own. `ImportAtStartup` stays `None`.
Existing sites keep their definitions until `update-site.ps1` refreshes their uSync folder and
someone imports it from the backoffice, which covers the export's definitions and not AppBlocks'. Failed first-boot imports use uSync's standard error reporting; fix the exported
definitions and reset the reference to run first boot again.

To save a backoffice change for new sites and updates:

1. Make the change in the reference backoffice. uSync writes the item's XML to `reference/uSync/`.
2. Copy that file into `src/UmbracoAppHost/uSync/` at the same relative path, or copy the whole
   folder and leave out what `deployment/appblocks-definitions.txt` lists, which uSync also writes
   there. Delete a file there when its item was deleted on purpose; an overlay copy
   alone leaves old files behind. A change to one of AppBlocks' definitions goes into its schema instead.
3. Review and commit the change with any related code or view changes.

`reset.ps1` compares `reference/uSync/` with the saved export before deleting anything. Files
that were changed, added or removed in the reference but never copied are listed, and the
script asks before discarding them, so an edit is noticed rather than lost. Copies of AppBlocks'
definitions are not compared.

To deliver a saved change to an existing site, run `update-site.ps1`. It copies the saved
export's definition folders (document, media and member types, data types, templates,
dictionary, languages and relation types) into the site's `uSync/` folder and lists what
changed, and removes the site's copies of what AppBlocks now installs, which an import would
otherwise offer to put back. Then start the site, open **Settings → uSync** in its backoffice, run **Report** to
review, and **Import** to apply. `Content/` and `Domains/` are copied only when a site is
created; pages belong to the site afterwards. Copy a content file by hand when a site really
should receive it.

A host in another repository runs its instance from its own project rather than from a site. After
rebuilding it against a newer library, run `refresh-definitions.ps1 -Instance <its instance folder>`
from this checkout: it copies the same definition folders from the export in the host's build
output into the instance's working uSync folder, removes the copies of AppBlocks' definitions and
lists what changed, leaving the instance's content and the host's own files alone. Then import from the backoffice as above. See
[Use the library in another repository](docs/getting-started.md#use-the-library-in-another-repository).

## Never locked out

Local backoffice sign-in uses the account's username. The shared default
`Umbraco:CMS:Security:UsernameIsEmail` is `false`, so Users exposes separate username and
email fields. The built-in administrator (ID `-1`) is configured as username `admin`, with
contact email `admin@localhost.local`. Other accounts keep their existing usernames.

Set `UmbracoAppHost:AdminPassword` in the website's `appsettings.json` to choose or reset
the original administrator's password. Saving the file applies it to a running site without
an IIS restart; startup also applies it to new and existing databases. A valid configured
password unlocks that account, clears failed login attempts and removes its two-factor
enrolment, even if it matches the current password: while the password is configured,
nothing stands between it and the account, so remove the setting before enrolling the
administrator in two-factor authentication again. Empty, missing, or rejected passwords leave
its password, lockout state and enrolment alone. New instances receive a random generated
password, recorded under `Umbraco:CMS:Unattended` in `instance.json`; existing passwords are
retained when no password is configured. `AdminUserName` and `AdminEmail` control the administrator's
identity on startup and configuration reload. Set either to an empty string to manage
that field independently in Users.

The website's `appsettings.json` defines the initial login flow:

```json
"BackOfficeLogin": {
  "DefaultFlow": "Auto",
  "DebugFlow": "Debug"
}
```

| Flow | Behavior |
|---|---|
| `Auto` | Prefer the configured identity server; show local sign-in when it is disabled or the SSO attempt fails. |
| `Form` | Show local sign-in and require the account's real password. |
| `IdentityServer` | Attempt automatic sign-in through the enabled backoffice identity server provider. |
| `Debug` | Development and emergency recovery: accept any nonempty password for an existing account and bypass account lockout and two-factor checks, while `Umbraco:CMS:Hosting:Debug` is true. |

`DefaultFlow` defaults to `Auto`. `DebugFlow` overrides it while Umbraco debug mode is
enabled; set it to `Form` for real password checks during development, or `null` to use
`DefaultFlow`. A `Debug` selection outside debug mode uses `Form`. Unknown flows and
explicit `IdentityServer` selections whose backoffice provider is disabled fail startup with a configuration error.
Restart after changing login flows or SSO settings. Enabled SSO providers remain available
as buttons; automatic selection applies to the initial sign-in, while Umbraco offers its
provider selection after logout or session timeout. `DenyLocalLogin` still controls whether
an SSO provider hides the local form; leave it `false` to retain password fallback.

Discovery failures and errors returned by the identity server open the native password form,
preserving the authorization request and its two-factor requirements. A failed attempt does not
automatically retry SSO. If an external server becomes unreachable after the browser has left
this site, the browser cannot return the error to us; `/umbraco/logout` opens provider selection
without another automatic attempt. This path also works beneath a configured virtual path.

The reference website's Development settings enable Umbraco debug mode. With Visual
Studio's **Website** profile and the defaults above, sign in as `admin` with any nonempty
password. The account must exist; Debug bypasses its password, lockout and two-factor checks.

Debug also provides emergency backoffice access on a deployed site. Set
`Umbraco:CMS:Hosting:Debug` to `true` and `BackOfficeLogin:DebugFlow` to `Debug`, then restart
the application. Sign in to the existing account with any nonempty password, repair the
credentials or two-factor setup in **Users**, and restore the normal settings and restart.
The hosting environment name does not control this: the same settings work in Production.
Recovery records the successful login and lifts the account's lockout, because Umbraco signs a
locked account out at its authorize step. It does not change the password or two-factor
enrollment; those repairs remain explicit actions in **Users**.
See [Getting started](docs/getting-started.md) for password recovery instructions.

For development with embedded IdentityServer SSO, set
`Accounts:SeedCurrentWindowsUserAsAdministrator` to `true` to create
`<your Windows user>@localhost` as an administrator on first run.

## Single sign-on

The supported authentication mechanisms are:

- **Local forms authentication**: Umbraco's built-in username/password login, including its
  lockout and two-factor flows. The optional development Debug mode described above remains available.
- **Embedded IdentityServer SSO**: `IdentityServer` with an empty `Authority` (the default)
  uses the generic `IdentityServer.Core` hosted by this site. It authenticates Windows accounts
  at `/connect/authorize` and returns an OpenID Connect login to Umbraco.
- **Remote IdentityServer SSO**: `IdentityServer` with an `Authority` uses another OpenID
  Connect identity server. The remote server performs authentication.

Embedded and remote SSO use the same provider registration and Umbraco account-linking flow.
There is no direct Windows login provider. Windows Integrated Authentication is used inside the
embedded identity server; it is not a separate Umbraco authentication mechanism.

When a page needs member sign-in, it first tries the configured identity server automatically.
Failed attempts return to Umbraco's built-in member username/password form, preserving the
original page and query parameters. The local form also offers an explicit SSO retry; adding
`?sso-local=1` to a login page selects it directly. Public pages remain public. When no identity
server is enabled for members, the local form and any other configured provider buttons are
shown immediately. Umbraco's Public Access is the baseline: a page it protects admits the members it
names and a backoffice user who may edit the page (Umbraco's **Update** permission on it, within
their start nodes), and nobody else, so any other backoffice user who needs that page signs in there
with a member account it names. The editor rule is this host's own, `Authentication/EditorPageAdmission.cs`,
registered with AppBlocks as an `IProtectedPageAdmission`: the seam AppBlocks asks on a protected
page once Umbraco has refused and there is no member session. AppBlocks itself admits nobody Umbraco
refuses, and the page and its block endpoints follow the same answer. On a page it leaves open, a signed-in backoffice user of any group
needs no member account, and whatever is behind each block decides: the sample's Orders form accepts
users in **Administrators**. The account menu identifies which account the application is using and
provides **Sign out**. If both sessions exist, the member session decides a protected page: a
member Umbraco refuses is never replaced by the backoffice account, however much that account may
edit, so the editor signs the member out first. The account menu uses the member identity, and block
endpoints evaluate member access first too. Sign-out ends both application sessions. See
[Application forms and report blocks](docs/application-blocks.md).

How the pieces fit:

- `IdentityServerExtensions.cs` registers the OpenID Connect handler for each audience
  (`/signin-oidc/backoffice` and `/signin-oidc/member`). It passes the validated principal
  to Umbraco unchanged. Umbraco handles account creation, linking, sign-in and two-factor checks.
- `src/UmbracoAppHost/IdentityServer/` adapts the sibling `IdentityServer.Core` library with
  a separate OpenIddict service container and exact client/callback validation. The same client
  code serves embedded and remote identity servers.
- `SsoRefusal.cs` and `BackOfficeSsoFallback.cs` return protocol failures to the native
  local login flow. They do not replace Umbraco's account-linking decisions.
- `SsoProviders.cs` generates the backoffice `authProvider` manifest from the configured
  provider name. Manual linking follows `IdentityServer:BackOfficeAutoLink:AllowManualLinking`.

### Who gets an account

OpenID Connect uses Umbraco's native external-login managers. Auto-creation and auto-linking
are enabled by default for both backoffice users and members. Umbraco finds a stored provider
link first; when auto-linking is enabled, it can match an existing account by email or create a
new account from the provider's email and name claims. Missing required claims are handled by
Umbraco. The host does not rewrite the principal or apply a custom provisioning veto before
Umbraco receives it.

Manual linking and unlinking remain available in the backoffice. Hosts can customize the
standard named `BackOfficeExternalLoginProviderOptions` and `MemberExternalLoginProviderOptions`,
including `OnAutoLinking` and `OnExternalLogin`, through Umbraco's normal options pipeline.
See [Umbraco's external login provider options](https://apidocs.umbraco.com/v18/csharp/api/Umbraco.Cms.Api.Management.Security.ExternalSignInAutoLinkOptions.html).

| `IdentityServer:BackOfficeAutoLink` setting | Meaning |
|---|---|
| `AutoLinkExternalAccount` | Enable native account creation and linking on first login; defaults to `true`. Turning it off still permits already-linked accounts to sign in. |
| `AllowManualLinking` | Allow users to link and unlink the provider in Umbraco; defaults to `true`. |
| `DefaultUserGroups` | User group **aliases** for newly created users. Defaults to empty, so auto-creation grants no backoffice groups. |
| `DefaultCulture` | Culture for new users; unset uses Umbraco's configured default. |

| `IdentityServer:MemberAutoLink` setting | Meaning |
|---|---|
| `AutoLinkExternalAccount` | Enable native member creation and linking; defaults to `true`. |
| `DefaultIsApproved` | Approval of new members; defaults to `true`. |
| `DefaultMemberTypeAlias` | Type for new members; defaults to `Member`. |
| `DefaultMemberGroups` | Groups for newly created members; defaults to empty. |
| `DefaultCulture` | Culture for new members; unset uses Umbraco's default. |
| `ExternalOnly` | Use Umbraco's lightweight external-member storage; defaults to `false`. |

Groups are managed in Umbraco unless a host explicitly supplies native callbacks to map claims.
The legacy `Accounts:BackOfficeUsersManagedBy`, `MembersManagedBy`,
`ActiveDirectoryGroupPattern` and `LinkByUserNameOnFirstSignIn` settings apply only to
explicit callers of the optional `AccountProvisioning` helper; they do not control OpenID
sign-in. Directory pickers still let administrators create and link accounts explicitly.

Local passwords and password recovery use Umbraco's account-management flow. Accounts created
by directory import or the configured roster receive generated passwords; administrators can
set or reset them in **Users** and **Members**. OpenID sign-in does not replace existing passwords.

| `Accounts` setting | Meaning |
|---|---|
| `EnsureMemberGroups`, `EnsureUserGroups` | Groups created when missing; user groups initially get the Content section. |
| `EnsureBackOfficeUsers` | Users created when missing (`Email`, `Name`, `UserGroups`). |
| `EnsureMembers` | Members created when missing (`Email`, `Name`, `MemberGroups`). |
| `ReportAdministratorUserGroups` | Backoffice groups whose users administer Interactive Reports, besides Umbraco's `admin` group and AppBlocks' **Interactive Reports Administrator** group (`interactiveReportsAdministrator`), which always do. The defaults name `admin` and this host's own **Interactive Report Administrators** group (`interactiveReportAdministrators`), which `ConfiguredAccounts` creates. |
| `MemberTypeAlias` | Type for directory-created and roster members. Native OpenID creation uses `MemberAutoLink:DefaultMemberTypeAlias`. |
| `RetireDeletedUsers`, `RetireDeletedMembers` | Retire used accounts instead of removing them; both default to `true`. |

### Creating accounts from the directory

The Users section's list of users and the Members section's list of members have an **Add from
directory…** button beside Umbraco's own. It opens a dialog that searches the directory as you
type, from two characters of a name, username or email, and lists the people found with their
Windows account name, email and title or department, marking those who already have an account
of that kind, disabled or retired. Choose a person, then their user groups (at least one), or
their member type (when there is more than one) and member groups, and select **Create user** or
**Create member**. The dialog closes, the list shows the new account, and a notification confirms
it. The account is approved and filled in from the directory:

| Field | Value |
|---|---|
| Username | The Windows account name without its domain, in lower case: `DOMAIN\Test.User` becomes `test.user`. A site with `UsernameIsEmail` gets the email instead. |
| Name | The directory's display name |
| Email | The directory's email, or `username@localhost` when it has none, as for a sign-in |

The person's SID is linked to the account at once under the configured IdentityServer scheme
for its audience, including when SSO is temporarily disabled. Native OpenID sign-in finds that
link directly. The browser names the person by SID only; the site reads everything else from the directory
again. Nothing is created, and the dialog says what to do, when the person already has an account
of that kind (linked to their SID, or retired under it) or another account holds the username or
email. The audit log records each creation.

The dialog uses this site's management API, which a script with a backoffice token can call too.
The user endpoints need access to the Users section and the member endpoints to the Members
section, as Umbraco's own do:

| Endpoint | Body or query | Answer |
|---|---|---|
| `GET /umbraco/management/api/v1/directory/users/search`, `.../members/search` | `query`, `take` (default 20, at most 50) | `results` (`sid`, `accountName`, `userName`, `displayName`, `email`, `department`, `title`, `enabled`) and `existing`, the accounts by SID (`kind`, `key`, `username`, `state`: `active`, `disabled` or `retired`) |
| `POST /umbraco/management/api/v1/directory/users` | `sid`, `userGroupKeys` | 201 with `key`, `username`, `name`, `email`; 409 when the person or the name already has an account |
| `POST /umbraco/management/api/v1/directory/members` | `sid`, `memberTypeAlias` (empty means `Accounts:MemberTypeAlias`), `memberGroupNames` | As for users |

A search the directory refuses, such as a term shorter than two characters, answers 400 with the
reason; a directory that cannot be reached answers 503, and the dialog shows both. Searches cover
the whole directory, include the accounts the telephone directory hides (no service number, plus
accounts) and leave out disabled ones.

Where the people come from is decided at startup, which the log reports as "Accounts are created
from ...". Nothing asks the directory anything until someone searches, so a directory that is down
never stops a site from starting.

- **Active Directory, queried by the site**, when it signs in through its embedded identity server
  (`IdentityServer:Authority` empty). The site queries the domain as its process account, through
  the site's own lookup in `DirectoryAccounts/Lookup`,
  under the `Directory` section's `UserRootGlobal`. In IIS the application pool identity therefore
  needs read access to the directory there; `ApplicationPoolIdentity` has it on a domain-joined server.
- **The external identity server's directory API** (`{Authority}/directory/users`) when
  `IdentityServer:Authority` is set. The site sends `IdentityServer:DirectoryApiKey`, one of the
  server's `Directory:ApiKeys`, or without one authenticates as its own Windows identity, which the
  server must admit (see its `Directory:RequireGroup`). `AcceptAnyServerCertificate` applies. This
  requires a host that exposes the directory API; generic IdentityServer.Core
  provides authentication only.
- **The computer's local accounts** on a development machine that is not in a domain, when the
  site signs in through its embedded identity server and so signs people in with those accounts.
  The whole flow then works without a domain controller: the people are the computer's enabled
  local accounts, matched on name, full name and description; they have no email, and their
  display name is the full name or the account name. `Directory:UseLocalAccounts` set to `true`
  forces this; `false` forbids it.

| `Directory` setting | Meaning |
|---|---|
| `UserRootGlobal` | The LDAP path searched for people, such as `LDAP://OU=People,DC=example,DC=invalid`. Empty, the default, searches the whole domain. |
| `UserRoot` | The lookup's local-scope root; account creation uses `UserRootGlobal`. |
| `GlobalSearchMinLength`, `GlobalSearchSizeLimitBuffer`, `DefaultTake`, `MaxTake` | The people-lookup limits, defaulting to 2, 20, 20 and 50. |
| `UseLocalAccounts` | Search the computer's local accounts instead of a directory. Unset decides as described above. |

### Retiring accounts

Deleting an account would take the person's name off everything that refers to it, and Umbraco
refuses to delete a backoffice user who has signed in, because audit trails and content
versions refer to it. Delete in the Users and Members sections therefore retires an account that
has signed in, or is linked to a Windows identity, instead of removing it:

- Its username becomes the Windows SID it was linked to (`S-1-5-21-...`), or `retired-` and the
  account key when it had none, and its email becomes that username at `retired.invalid`, a
  domain reserved never to deliver.
- It is disabled, its external logins remain attached and its sessions end: its backoffice tokens
  are revoked and its security stamp changes.
- Its name, groups, start nodes, permissions and history stay, so pages, versions and audit
  entries still name the person. An audit entry records the username and email it had.

The backoffice reports the delete as done and keeps the account, disabled, under its new
username. Its external links still lead to the disabled account, so Umbraco's native login
checks refuse it instead of auto-creating a replacement for the same identity. A new person
with a different identity can use the freed username and email. To restore the retired person,
enable the account and give it back its username and email; its existing link remains usable.

Accounts that never signed in and were never linked are still deleted, the built-in
administrator is never retired, and Umbraco's own checks still apply: nobody deletes
themselves. Set `RetireDeletedUsers` or `RetireDeletedMembers` to `false` to turn retirement
off; Delete then behaves as in Umbraco, refusing a user who has signed in and deleting a member
outright. A change applies without a restart.

### Identity server provider

| `IdentityServer` setting | Meaning |
|---|---|
| `Enabled`, `BackOfficeEnabled`, `MemberEnabled` | Offer the provider at all, and to which audience. |
| `Name` | Button label, `Domain` in the shared defaults. Its letters and digits also form the scheme names (`Domain` gives `Umbraco.Domain` and `UmbracoMembers.Domain`). Existing installations must explicitly retain their original name in instance settings so stored external-login links continue to match. |
| `Authority` | Empty (the default) signs in through this site's [embedded identity server](#embedded-identity-server), which must then be enabled; startup fails with a configuration error otherwise. Any other value is the address of an external identity server, which must list this site's host among its `Hosts`. |
| `ClientId`, `ClientSecret` | The OpenID Connect client registration, `umbraco-backoffice` by default. With the embedded server the site registers this client itself; leave the secret empty, which makes it a public client protected by PKCE, as client and server run in the same process. |
| `Scopes` | Defaults to `openid profile email roles`. |
| `CallbackPath` | Path prefix for the callbacks; added to Umbraco's reserved paths automatically. |
| `RequireHttpsMetadata` | External identity server only: off only for one on a trusted intranet without TLS. The embedded server's metadata is read in process. |
| `AcceptAnyServerCertificate` | Skip certificate validation on the back-channel calls, which with the embedded server is the site calling its own token endpoint. Development or an isolated intranet with a self-signed certificate only. |
| `DirectoryApiKey` | External identity server only: the key the site sends in the `X-Api-Key` header when it searches the server's directory API to [create accounts from the directory](#creating-accounts-from-the-directory); one of the server's `Directory:ApiKeys`. Empty authenticates as the site's own Windows identity, which the server must then admit. |
| `DenyLocalLogin` | Hide the local backoffice form; leave `false` to retain password fallback. |

An external identity server may be anywhere the browser and this site can reach, including an
HTTP-only sub-application such as `https://host/IdentityServer`; the authority then includes
that path and no rewrite rule is needed in front of it. The client returns by GET with
`SameSite=Lax` cookies, so it does not depend on cross-site cookies and works even when
this site is served over plain HTTP (the backoffice itself still requires HTTPS); the
correlation and nonce cookies are marked `Secure` only over HTTPS for the same reason.

The claims the site reads: `sub` (the stable identity key; a Windows SID for the embedded server), `name`, the Windows account name, `email`, and one role per directory group.
That is what OpenIddict-WindowsAuth and the embedded identity server issue; another OpenID
Connect server works if it sends the same.

In IIS, a site that signs in through its embedded identity server authenticates people itself,
so it needs **Windows Authentication** and **Anonymous Authentication** both enabled (see
[Embedded identity server](#embedded-identity-server)). With Anonymous Authentication only,
Windows authentication is unavailable to the site: it says so in the log at startup, the login
screens omit the embedded identity server, nothing starts
automatically, and people sign in with a username and password, or through an external
identity server (`IdentityServer:Authority`), which authenticates them itself and needs no
Windows Authentication here. Other applications cannot sign in through such a site's embedded
server: its authorize endpoint refuses them with `access_denied`.

The library defaults turn the provider on against the embedded identity server, and sites
created by `new-site.ps1` inherit that default. Set `IdentityServer:Authority` and the client
settings in a site's `instance.json` to use an external identity server instead, or set
`IdentityServer:Enabled` to false to offer no single sign-on. Only enabled providers appear on
the login screen.

### Embedded identity server

The platform hosts the generic `IdentityServer.Core` library from the sibling IdentityServer
checkout in the same process. It serves `/connect/authorize` and `/connect/token`, authenticating
with Windows Integrated Authentication (Kerberos or NTLM on Kestrel, IIS Windows Authentication
in IIS). The shared library owns claims, token issuance and key storage; the adapter in
`src/UmbracoAppHost/IdentityServer/` supplies the site's client policy and local-login fallback.
The site's own identity server provider is one of its clients; other applications are configured
in `EmbeddedIdentityServer`. The client-side `IdentityServer` settings remain separate.

Applications discover it at `/.well-known/openid-configuration` (under the virtual path, if the
site has one), which lists `/connect/authorize` and `/connect/token` as the authorization and token
endpoints, and `/.well-known/jwks` as the signing keys. The issuer is the address the request
used: scheme, host and virtual path, the way Umbraco's own OpenIddict endpoints work under every
host name a site answers to, so an application must use the same address the site is published
under. Behind a reverse proxy, configure forwarded headers so the site sees its public address.
Discovery and signing keys belong to the shared server. Umbraco's own authentication options,
keys, endpoints and database stay separate.

| `EmbeddedIdentityServer` setting | Meaning |
|---|---|
| `Enabled` | Serve `/connect/authorize` and `/connect/token`. On by default. Off removes both endpoints; the identity server provider then needs an external `Authority`. |
| `Clients` | Other applications allowed to sign in: `ClientId`, `ClientSecret` (empty for a public client), `DisplayName`, `RedirectUris`, `PostLogoutRedirectUris`. Read at startup; removing an entry and restarting revokes client access. No client records are added to Umbraco. |
| `Groups` | Case-insensitive .NET regular expressions matched against direct AD group names, without the domain prefix. Empty or missing sends no roles, following IdentityServer.Core. Local machine accounts have no directory roles. |
| `PersistKeys` | Keep signing and encryption keys across restarts (default). Off generates new keys at every start, invalidating previously issued tokens and outstanding authorization codes. |
| `DataPath` | Directory for the shared server's key files. Defaults to `data/keys/identity-server/` in the instance folder; relative paths use the content root. |
| `AccessTokenLifetime`, `IdentityTokenLifetime`, `AuthorizationCodeLifetime` | Token lifetimes for this server's clients (defaults one hour, one hour and five minutes, as in OpenIddict-WindowsAuth). They configure only the shared server; Umbraco's token lifetimes do not change. |

To let another application sign in, add it to the site's `instance.json` (or the host's
`appsettings.json`) and restart:

```json
"EmbeddedIdentityServer": {
  "Clients": [
    {
      "ClientId": "reports-portal",
      "ClientSecret": "a long random secret",
      "RedirectUris": [ "https://reports.example/signin-oidc" ]
    }
  ],
  "Groups": [ "^Report ", "^Domain Admins$" ]
}
```

The application then uses this site's address as its authority, the authorization code flow
(`response_type=code`) with PKCE (`S256`; required of every client), and the scopes `openid`,
`profile`, `email` and `roles`. Redirect addresses must match exactly: no wildcards, and no
host-only matching as in OpenIddict-WindowsAuth's `Hosts`. A client with a secret authenticates
with it at the token endpoint; one without is public. There are no refresh tokens (requests for
`offline_access` are refused), no userinfo endpoint and no sign-out endpoint: a Windows sign-in
has no session to end, and `PostLogoutRedirectUris` are recorded for completeness only. Client
ids that belong to Umbraco (`umbraco-back-office`, `umbraco-member`, `umbraco-swagger`,
`umbraco-postman`) or to this site (`IdentityServer:ClientId`) are rejected at startup, as are
regular expressions that do not compile and redirect addresses that are not absolute.

The claims, by requested scope, are those of OpenIddict-WindowsAuth, under the long .NET claim
types its clients read:

| Scope | Claims |
|---|---|
| `openid` | `sub`, the Windows SID (always present), and `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name`, the display name from Active Directory or the account name |
| `profile` | `.../windowsaccountname` (`DOMAIN\user`), plus `.../givenname` and `.../surname` and `.../homephone` when the directory has them |
| `email` | `.../emailaddress`, the directory email or `user@localhost` |
| `roles` | `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`, one per group selected by `Groups`, without the domain prefix |

Every claim goes into both the identity token and the access token. IdentityServer.Core reads
**direct AD memberships**, without expanding nested groups; local machine accounts have no roles.
The profile scope also includes the home phone claim when supplied by Active Directory. Narrow
`Groups` to keep tokens small; the shared server logs large role sets and token responses.

IdentityServer.Core persists 2048-bit RSA signing and encryption keys as `signing-key.bin` and
`encryption-key.bin` in `DataPath`, protected with Windows machine DPAPI. Restrict that folder's
permissions to the service account and administrators. Preserve it across deployments on the same
machine; copying it to another machine does not migrate the keys. Unreadable keys fail startup.
`reset.ps1` deletes the default key directory with the rest of `data/`.

**Upgrading from the former built-in server:** this starts a new set of keys, so previously issued
tokens and outstanding codes need a fresh sign-in, and external clients must refresh cached JWKS.
Existing Umbraco login cookies are independent. Remove `EmbeddedIdentityServer:KeyPath` and
`EmbeddedIdentityServer:UseActiveDirectory` if set; startup validation explains their replacements.
The old Data Protection signing-key file is left untouched. Configure `Groups` explicitly if
clients need roles, and review any rules relying on nested or local Windows groups. Existing
`Clients`, exact `RedirectUris`, secrets, PKCE, enabled flags and token lifetimes remain supported.
Old embedded-client database records cannot use Umbraco's own protocol endpoints.

When this site signs in through its own server, it redeems the authorization code by calling
its own `/connect/token` over HTTP at the address the browser used, as any client would. The
server must therefore reach its own public host name: DNS, IIS bindings, firewalls and proxies
must allow that loopback call, and `AcceptAnyServerCertificate` covers a certificate the server
itself does not trust. The issuer and signing keys are taken from the server in the same process
rather than downloaded, so a forged Host header can never point the sign-in at someone else's
server. The site's client may return only to its enabled backoffice/member callback at the exact
scheme, host, port and virtual path used for that request; no host names are registered in a database.
Restricting `AllowedHosts` to the site's real host names is still good practice.

The two servers stay apart. Umbraco's clients are refused at `/connect/`, this server's clients
are refused at Umbraco's own authorize and token endpoints, and access tokens issued here are
refused by Umbraco's APIs: they are meant for other applications. Umbraco's grant types, PKCE
requirement, token formats and lifetimes are unchanged.

In IIS, enable both **Windows Authentication** and **Anonymous Authentication** for the site:
the token and discovery endpoints must answer without a Windows challenge, and the authorize
endpoint issues its own challenge when nothing has authenticated yet. On Kestrel the Negotiate
handler does the exchange itself. With Anonymous Authentication only, the endpoints stay
reachable but every authorization request is refused with `access_denied`, the login screens
fall back to username and password, and the startup log says why.

Troubleshooting:

- **The browser keeps prompting for credentials, or `/connect/authorize` answers 401 forever.**
  Browsers send Windows credentials silently only to sites in the Local Intranet zone (Edge,
  Chrome) or listed in `network.negotiate-auth.trusted-uris` (Firefox). Negotiate needs HTTP/1.1,
  which browsers fall back to by themselves. In IIS, check that Windows Authentication is enabled.
- **`/connect/authorize` fails with a server error in IIS mentioning `IConnectionItemsFeature`.**
  Windows Authentication is off for the site, so the Negotiate handler tries to do the exchange
  itself, which only Kestrel supports.
- **A domain account signs in with NTLM instead of Kerberos.** The host name has no SPN for the
  account the application pool runs as: `setspn -S HTTP/host.domain DOMAIN\account`. NTLM still
  works, and is normal for local accounts.
- **`invalid_request` naming the client id or the redirect address.** The client is not
  registered here, uses an endpoint that is not its own, or sent a redirect address that is not
  registered exactly. The log names which check failed.
- **Sign-in through the site's own server ends in an error after `/signin-oidc/...`.** The site
  could not reach its own token endpoint at the address the browser used; see the loopback call
  above. The log records the failure.

## Login screen and backoffice

The login screen is Umbraco's own unless a site or theme brands it. The **Back Office Experience**
package in the base's `wwwroot/App_Plugins/BackOfficeExperience/` supplies the Welcome dashboard.
A site or theme library can add a login appearance through Umbraco's supported hooks rather than a
custom page, and marks its package `allowPublicAccess` so the login page loads it before sign-in:

- **Logo and panel image**: Umbraco resolves `Umbraco:CMS:Content:LoginLogoImage`,
  `LoginLogoImageAlternative` and `LoginBackgroundImage` under `wwwroot/umbraco/`.
  `services.AddLoginScreen(configuration, logoImage, panelImage)` in `LoginScreen.cs` points the
  three settings at a library's files unless the site configures its own. The "background"
  setting is the picture inside the rounded panel on the left, not the page background.
- **Greeting**: a `localization` extension per culture overrides the seven `login_greeting`
  keys. Umbraco 18's own defaults are "Welcome" and "Bienvenue" every day.
- **Page background and sign-in panel**: an `appEntryPoint` extension can load a stylesheet that
  sets the login layout's custom properties on the document root, such as
  `--umb-login-background`. The sign-in panel sits inside the layout's shadow root, out of reach
  of page CSS, so a script adopts a stylesheet into that root, and the same rule paints the host,
  because the sign-in view inside the backoffice app uses a different element that ignores the
  background property.
- **Welcome dashboard**: the Content section opens on **Welcome**, ahead of Redirect URL
  Management. Its Interactive Report lists recently changed pages, newest save first, with
  10 rows per page by default. It shows the page name, full content tree path, last editor,
  and save time, with an **Edit page** link to the document in the same backoffice tab.
  Draft changes are included; recycled pages are excluded. Search, sorting, filters and
  page size use the standard Interactive Reports controls. The report requires a backoffice
  Content user and respects content start nodes and inherited Browse permissions.
  `backoffice.js` continues to exclude Umbraco's news dashboard.

The 13 site's `config/lang/*.user.xml` overrides and AngularJS customizations have no role
in 18 and were not carried over.

## Interactive Reports

`src/UmbracoAppHost/Reports/InteractiveReportsSite.cs` registers the package with code-registered
connections (`ReportsDb`, a SQLite file for saved reports and authorization; `UmbracoDb` for
Umbraco's own database) and maps the JSON and download clients under `/api/reports` and
`/api/download`. A site adds its own connections to AppBlocks through the callback of
`AddUmbracoAppHost`, as the common host does for its sample orders database; AppBlocks passes them
on to Interactive Reports. Anything else about the report engine, such as a row restriction
callback, is set from a composer with `AddAppBlocksInteractiveReports`.

Report definitions are in a site's `appsettings.json` under `InteractiveReport:Reports`. Three
come with the base, from the embedded `Reports/umbraco-reports.json`, which has the same shape;
a site's definition of the same name replaces the built-in one. All read Umbraco's database
through the `UmbracoDb` connection, which follows `ConnectionStrings:umbracoDbDSN` and its
provider, and support SQLite and SQL Server:

- `umbraco-recent-pages` supplies the Welcome dashboard. It includes the latest saved version
  of every accessible, non-recycled document, with full content paths at any tree depth.
  The built-in definition selects the database's path aggregation and date formatting syntax.
- `umbraco-members` lists the website's members.
- `umbraco-media` audits the media library: every media item except folders, with its
  folder path in the library (up to ten levels deep), media type, file path, extension,
  size, image dimensions, who created it and when, who last saved it and when, and whether
  it sits in the recycle bin. The file path is a link to the file, and the row's pencil
  opens the item in the backoffice media section, which shows its place in the media tree.
  Like the members report it is for report administrators only.

Umbraco's database stores media paths without the virtual path, and the backoffice route
has none either, so definitions are written as if the site were at the root: SQL writes
`{SiteRoot}` where a URL it builds starts, and the pencil's `editLink` template is
root-absolute. `Reports/ReportLinksUnderVirtualPath.cs` substitutes
`Umbraco:CMS:Hosting:ApplicationVirtualPath` into both when the options are built, and
changes nothing when no virtual path is configured.

Authorization bridges Umbraco's two identities:

- The Welcome dashboard uses `/umbraco/reports` and `/umbraco/report-download`, which
  authenticate with backoffice cookies exclusively, including when a website member is also
  signed in. Its report policy requires access to Content; its row restriction applies
  Umbraco's start nodes and inherited Browse permissions before querying or exporting.
- Requests to the report endpoints authenticate with the member cookie first, then the
  cookie Umbraco issues to expose a backoffice login to non-management endpoints
  (`UMB_UCONTEXT_EXPOSED`), so both audiences reach the package. This runs inside
  Umbraco's middleware block, because middleware appended after `WithEndpoints` never
  sees matched endpoints.
- A caller is a **report administrator** when they are an approved, not locked-out
  backoffice user in Umbraco's `admin` group, in AppBlocks' **Interactive Reports
  Administrator** group (`interactiveReportsAdministrator`, if the host creates it),
  or in one of `ReportAdministratorUserGroups` or `AppBlocks:ReportAdministratorGroups`;
  or a member whose sign-in key (the Windows SID, from either provider; or, before the
  first backoffice sign-in, email) matches such a backoffice user. `Reports/ReportAccessPolicy.cs`
  replaces AppBlocks' own policy to add that member link; AppBlocks never relates the two
  account stores, so a plain AppBlocks site has no such link.
- Everyone else is an ordinary report user: public reports and their own saved reports.
- Saved reports are owned by the one identity that created them, the same one AppBlocks
  resolves (`DefaultReportAccessPolicy.OwnerKey`): the member when one is signed in,
  otherwise the backoffice user.

The uSync baseline supplies the **Report Page** document type and its template registration. The library
supplies `Views/ReportPage.cshtml`, and Umbraco registers an editable copy in the host's Views
folder. The content tree selects its layout. It embeds `<interactive-report>`
for a signed-in member or backoffice user and shows one sign-in button per enabled provider
otherwise, so backoffice users need no member record on a page that Umbraco Public Access leaves
open or that they may edit; on any other page it protects, its rule decides before the view runs. The base's `SiteVisitor` service resolves that once per request (member,
exposed backoffice login, or nobody) for any view that injects it. Any content page of type
Report Page renders the report named in its `reportName` property; the saved export includes
one for its sample report.

## Data sources

A data source lets reports, lists, tabular forms and schedules read a table or a SELECT;
tabular forms can also write through it. Under a **Data sources** root (document type `dataSources`: at the
root, no template, never shown on the site) each **SQL data source** (`sqlDataSource`) node defines
one. Being content, data sources are versioned, published, exported by uSync, and guarded by the
same user group permissions and start nodes as pages. Blocks select a published data source by
alias or key. Form Interface selects a registered C# form class, whose code owns its persistence
and authorization. The data source engine lives in `AppBlocks/DataSources/`
and its design in [section 4 of the design](docs/design-blocks-and-data-sources.md#4-data-sources).

| Property | Format | Meaning |
|---|---|---|
| Alias | text | What a report, list, tabular form or schedule selects, such as `orders`. Must not repeat another data source or a C# resource |
| Connection | text | A connection from Settings → AppBlocks → Connections: `ReportsSampleDb`, `UmbracoDb`, or one a site adds through the callback of `AddUmbracoAppHost`. Credentials stay in configuration; the data source can do only what the connection's database account may |
| Row source | table name or SELECT | `ORDERS`, `dbo.ORDERS`, or a SELECT without `ORDER BY`. A SELECT writes to the one table its key columns come from; its other columns are read only |
| Key columns | comma-separated | The primary key or a unique key; required when writes are allowed. A key of several columns travels in URLs as its values joined by `\|`, with `\|` and `%` inside a value written `%7C` and `%25` |
| Key sequences | JSON, optional | Oracle sequence names for generated keys, including 11g: `{ "ORDER_ID": "ORDERS_SEQ" }`. Names are resolved against the database catalog |
| Mapping | JSON, optional | Field alias to column, or to settings: `{ "client": "CUSTOMER", "orderDate": { "kind": "date" }, "region": { "choices": ["NORTH", "SOUTH"] } }`. Settings are `column`, `label`, `kind`, `choices`, `readOnly`, `required`, `maxLength`, `min` and `max`; an entry without `column` matches its column by name |
| Allow insert, update, delete | toggles | What the engine performs; the rest is refused with 403 whatever a page shows |
| Concurrency | column, optional | The version column: a `NOT NULL` whole number the engine increments, or a SQL Server `rowversion`. Empty compares a checksum of the fetched row instead, as APEX does |
| Insert defaults | lines `COLUMN = value` | A bind variable, a `'quoted'` or plain literal, or `NULL`. A new record starts with these values, and an insert uses them for fields the form does not send |
| Audit columns | lines, optional | `createdBy`, `createdAt`, `updatedBy`, `updatedAt` = a column. The engine writes the username and the server's local time; forms cannot change them |
| Row restriction | predicate, optional | Added to every fetch and write, such as `OWNER_SID = :USER_SID` or `REGION IN (:USER_GROUPS)` |
| Access | required choice | **Public** allows anonymous reads where the page permits them; **Not public** requires a signed-in Member or backoffice User; **Specific groups** requires at least one read group. An empty value is an error. The saved Orders sources use **Not public** |
| Read groups, Write groups | member group pickers | Read groups apply in **Specific groups** mode; write groups may also read. Empty write groups allow signed-in readers to write, subject to allow flags and policies. Anonymous readers cannot write. Member groups refuse backoffice Users, including Administrators. The groups in `Accounts:EnsureMemberGroups` are created before every uSync import so exported group selections resolve on a fresh site |
| Custom statements | sections, advanced | Lines `-- insert`, `-- update` and `-- delete` start statements that replace the generated ones; each runs whole with bound parameters |

**Fields.** Every column of the row source is a field, named after its column (`ORDER_DATE`
becomes `orderDate`, `OrderDate` too) unless the mapping names it. The kind comes from the column
type: text, number, date, time, date and time, or checkbox; binary columns are left out. SQLite
keeps dates as text, so such columns take a `kind` in the mapping. A `NOT NULL` column is required;
a column with a database default, an insert default or an engine-filled value need not be sent on
create. Text without a declared length accepts 4000 characters unless the mapping allows more.
Read only, and never written, are the keys the database assigns, computed columns, the version and
audit columns, a SELECT's columns from other tables and its expressions, mapped `readOnly` fields,
and every field when neither insert nor update is allowed. A column a SELECT renames is read only
too unless the mapping sets `"readOnly": false`, because a second copy of the key table in a
self-join (`p.NAME AS PARENT_NAME`) looks the same in the schema. Any other bound field is written,
including one a form renders as a hidden input, such as a foreign key. An update writes only the
fields whose values differ from the stored ones; a stored value it keeps is not checked again, so
legacy data the rules would refuse does not block saving another field.

**Bind variables** are bound as parameters, never substituted into the text:

| Variable | Value | Row source | Restriction | Defaults, custom statements |
|---|---|---|---|---|
| `:USER_SID` | The Windows SID the account is linked to (its external login key); NULL when the account has no external identity link | yes | yes | yes |
| `:USER_NAME`, `:USER_EMAIL` | The member's or backoffice user's username and email | yes | yes | yes |
| `:USER_KEY` | The application's key for the account: the member id, or `backoffice:` and the user id | yes | yes | yes |
| `:USER_GROUPS` | The current Member or backoffice User's groups, only as `IN (:USER_GROUPS)` | yes | yes | statements only |
| `:APP.name` | A server-computed value from a registered `IBindVariableProvider`, such as the page's tenant | yes | yes | yes |
| `:NOW` | The web server's local date and time | yes | yes | yes |
| `:PARAM.name` | A query parameter of the form request | yes | no | yes |
| `:FIELD.alias` | The record's current or submitted value of a field | no | no | yes |

The restriction refuses `:PARAM` and `:FIELD` because the caller controls them, so those values
cannot establish who may access a row. The form runtime forwards the page's query parameters on
reads and writes. `/api/forms` retains its existing bound of 50 names and 4000 characters each.
Queries or restrictions using `:USER_*` require sign-in even when Access is Public.
Application bind providers also supply field defaults and receive the page/block context;
see [application bind variables](../AppBlocks/docs/business-rules.md).

**Dialects.** SQLite, SQL Server, PostgreSQL and Oracle (12c and 11g) have read and write builders.
SQLite and SQL Server drivers come with Umbraco. A consuming web project must reference
`Npgsql` for PostgreSQL or `Oracle.ManagedDataAccess.Core` for Oracle; AppBlocks no longer
ships those optional drivers. The provider test project references both explicitly.
The engine reads through the Interactive Reports
connection factory and takes the connection's dialect from it. Writes are generated from the
schema: `INSERT` returning a new key through `RETURNING` (SQLite) or `OUTPUT INSERTED` into a table
variable (SQL Server, which also works on tables with triggers), `UPDATE ... SET changed columns
WHERE key AND version AND EXISTS (the row through the restriction)`, and `DELETE` likewise; each
must change exactly one row, counted without what triggers change (SQL Server reads `@@ROWCOUNT`). A
write transaction holds the row: SQLite begins it `IMMEDIATE`, SQL Server takes `UPDLOCK, HOLDLOCK`
on the row first. PostgreSQL uses `RETURNING` and `FOR UPDATE`; Oracle uses named output
parameters with `RETURNING INTO`, `FOR UPDATE`, and optional sequences for generated keys.
A save that changes nothing writes nothing. Provider integration tests for PostgreSQL and Oracle
run when a test database is configured; passing SQL builder tests alone does not verify a live provider.

**Safety.**

- Every value is a parameter. Names come from the catalog and are quoted by the dialect; the
  definition's names are only compared with what the schema reports. Editor SQL enters a statement
  only whole: the row source as a derived table, the restriction as one parenthesized predicate,
  and custom statements as they are written. Back-office access is the trust boundary for that SQL,
  as for code blocks; the connection's account is the limit of what it can do.
- Every operation re-checks the caller: a member Public Access admits to the page, or on a page it
  leaves open a current backoffice user; then the read and write groups, read from Umbraco on each request; then the allow
  flags. A fetch outside the restriction is not found (404), and a write that would leave it, such
  as an insert or an update that hands a row to someone else, is rolled back and refused (403).
- `IDataSourcePolicy` (C#, registered in DI) is asked last for every operation, with the current
  and proposed values; any refusal returns 403 and writes nothing. Updates and deletes ask it
  inside their transaction with the row locked, and a create inside its transaction before the
  insert. The request carries the engine's open connection and, for a write, its transaction, so a policy
  can read other rows in the same snapshot with its own command (`command.Transaction =
  request.Transaction`); it never commits, rolls back or disposes them.
- A stale version, rowversion or checksum returns 409. Creates are idempotent per submission key
  for fifteen minutes, in memory. Each operation is logged by the form endpoints, and the audit
  columns record who and when in the application's own table. A data source whose database or
  schema cannot be read answers 503 and is tried again a few seconds later; a statement the
  database refuses answers 422 with the details in the log.
- Sizes are bounded: a fetch reads at most two rows (two means the key is not unique, which is an
  error), a row source has at most 200 columns, a statement at most 2000 parameters, a key 1000
  characters, and the row source, the restriction and each custom statement 20000, 4000 and 20000
  characters.

**Publishing** a data source checks it against its database: the connection is registered, the row
source and the restriction compile (a zero-row execution), every named column exists, the key
columns are the primary key or a unique key of the written table when writes are allowed, the
version column holds whole numbers, every bind variable is known and allowed where it is used, and
custom statements compile without running (`EXPLAIN` on SQLite, `SET NOEXEC` on SQL Server).
Publishing a page bound to a data source checks its field blocks against the data source's
columns. Data source nodes get URL segments of their own keys, so one named like a page (Orders)
never takes that page's URL.

**Testing a draft.** The SQL data source's **Test** action uses unsaved editor values and sample
bind parameters, shows its columns and one matching row, and leaves content and application rows
unchanged. For example, enter `{"PARAM.id":"4","USER_NAME":"pilot"}`. The Test action uses
the same node permissions as editing; publishing still performs its own validation. See the
[authoring walkthrough](docs/application-blocks.md#testing-and-publishing-a-data-source).

**Reports and lists of values.** The same Data sources root holds published `reportDefinition`
and `listOfValues` nodes. A report selects a SQL data source, or supplies inline SQL and a
registered connection while migrating configuration. Its title, controls and page sizes have
individual properties; **Options** accepts the existing `ReportDefinition` JSON settings for
column labels, help, links and other report options. A published report replaces the configured
definition of the same alias. An unavailable published override stays unavailable instead of
silently falling back to the seed. Appsettings remains supported for defaults. Publishing refuses
an unknown control name, a default page size above the maximum, `contextParams` in the options
(server-owned bindings come from a data source), and inline SQL containing `{{RowRestriction}}`
that no row-access callback decides; only the Welcome report and configured reports that use the
marker have one. Each published version of a report or list, and of the data source it selects,
is read and checked once rather than on every request.

A list selects its data source or inline SQL, display column and stored-value column. Its
**Maximum rows** defaults to 1000 and can be increased on the node. Select the list on an
`appSelect` block; the form checks new or changed choices against the current list. An unchanged
stored value can remain after its option is retired. List and data source changes invalidate
loaded forms. Shared queries apply the source's read groups,
row restriction and `IDataSourcePolicy` with `DataSourceAction.Query`, including direct report
aliases, which have no host-page context. Report blocks also keep their page's Public Access. This
host's `ReportAccessPolicy` also requires an account for every report, so an anonymous visitor is
refused even on a page without Public Access, unlike a plain AppBlocks site. Data source forms,
tabular forms, schedules and lists of values also need an account.

**The sample.** The saved export's Data sources root holds **Orders**: alias `orders`, connection
`ReportsSampleDb`, table `ORDERS`, key `ORDER_ID`, version column `VERSION`, the four audit columns
`CREATED_BY`, `CREATED_AT`, `UPDATED_BY` and `UPDATED_AT` (which the sample database gains in
place), insert defaults `NORTH`, `NEW`, `0` and `:NOW`, and a mapping that gives the order date
its kind. Orders and Welcome reports also ship as nodes. The **Orders form** page selects the C#
form class `sample.orders` (`Sample/OrdersFormResource.cs`) and the published region and status
lists: a form binds a registered class, while a data source backs tabular forms and report nodes.
uSync imports content by level and sort order, and the data source sorts first, so a new site
publishes it before the pages that validate against it.

The sample has no duplicate static region/status choices. Where an application explicitly
configures choices or business validation on a resource, those rules still apply: a page's
list changes the choices offered by that page, without overriding the resource's rules.

## Paper reports

The document engine is supplied by the optional `AppBlocks.Documents` package, which AppHost
references from the AppBlocks repository. AppHost supplies its site branding and sample templates.

A **Paper report** content node selects a published SQL data source and an Umbraco Razor
template. The template receives `PaperReportModel`: rows and columns, bound parameters, the
signed-in person, culture, generation time and theme. The saved export includes English and
French Orders paper reports and `Views/OrdersPaperReport.cshtml` as an example.

The node's **Parameters** JSON maps query names to literals or bind variables, for example
`{"id":":PARAM.id"}`; empty forwards the query parameters, bounded as forms bound them (at most
50 names, one value each, without `appCulture` or `format`). **Page options** accepts Pressroom's
`RenderOptions` JSON, including `pageSize`, `landscape`, `margins`, `headerTemplate` and
`footerTemplate`. **Culture** accepts a culture such as `fr-CA` or `:PARAM.culture`; publishing
refuses a literal that is not a culture. **File name** supports parameter names such as `{id}` and
`{date:yyyy-MM-dd}`. **Maximum rows** bounds the result: empty means 5000, publishing refuses more
than 50000, and a query that returns more rows than the limit fails the download with a 422.

Administrators add, edit and remove named Pressroom configurations in
**Settings → AppBlocks → Documents**. Each has a URL, API key, timeout and output: PDF or a printer
discovered through Pressroom's API. Printer discovery and printing require a key; PDF may use
anonymous rendering when the server permits it. Template document links use the first PDF
configuration, or HTML when none exists. Keys are protected with the site's Data Protection keys.
Existing single-server settings migrate to the collection; removing every configuration keeps
Pressroom disabled even when configuration-file defaults exist. Before settings are saved,
`AppBlocks:Documents:Pressroom:*`, or legacy `Pressroom:*` settings, provide a PDF default.
Documents owns the shared `IDocumentOutputService` gateway used by companions, including its
stream/print-receipt results and printer-failure handling. It uses `Pressroom.Client` without
installing a browser runtime; `PressroomRoot` can override the source dependency location.

`/api/paper-reports/{node-key}` rechecks Public Access and the data source's rules, renders the
selected template, embeds stylesheets and assets, and streams the PDF. A paper report always
renders for a person, so the download needs someone signed in, even for a public node: a member
Public Access admits, a backoffice user who may edit the node, or on a node it leaves open any
backoffice user. Anyone else receives a 403
problem response asking them to sign in. Without a configured service, when it cannot render, or with `?format=html`, it opens
the same self-contained HTML inline for browser printing. Print actions open a new window/tab;
both HTML and PDF are delivered inline. Pressroom failures use Umbraco's normal logging pipeline.
A configured service is also used on localhost.
Embedding is best effort: an asset that cannot be read keeps its reference and is logged. Site
assets come from disk only; other http(s) assets are fetched without credentials or redirects,
within 10 seconds and 8 MB, and never from loopback or link-local addresses. The theme supplies
its print stylesheet. Selecting a paper report on a report or Form block adds Print; row actions
and loaded forms pass the record key. The base keeps the paper report type's allowed templates
equal to the installed templates, so a host adds a template by registering it: a uSync `Templates/`
file plus its view in `Views/`. Publishing checks that the template's view exists.

## Record attachments

Files that belong to a record are kept as blobs in a database table, with their metadata, their
revisions and the links to the records they belong to. Nothing is written to the instance folder
or to Umbraco media, which is public by URL. The code is in `src/UmbracoAppHost/Attachments/`.

An **Attachments** block (`appAttachments`, in the block catalogue's **Content** group) placed in a
form shows the files of the record the form is editing. Placed on its own, on a Web Page, it names
the record: **Owner resource** is a C# resource or a published data source by alias, and **Owner
key** is `:PARAM.name` for a parameter of the page's address, `:FIELD.alias` for a field of the
record the enclosing form loaded (a stay page showing the files of the stay's block, say), or the
key itself. **Purposes**, one per line, file each upload under a label such as Invoice; **Largest
file** and **Accepted types** narrow the site's limits and never widen them, which publishing
checks; **Read only** shows and downloads the files and refuses every change. The sample Orders
form carries one, and the Website content blocks page shows the same block in the website shell.

The two tables, `APP_ATTACHMENT` (file name, content type, size, SHA-256, the bytes, who
uploaded it and when, and the revision it replaced) and `APP_ATTACHMENT_LINK` (owner alias, owner
key, the current file, purpose, label, notes, the audit columns and the soft removal), are
created on first use in the database of the connection `Attachments:Connection` names, through
the application database helper (`Attachments/Schema/`). SQLite and SQL Server are supported.
The default, `AttachmentsDb`, is a SQLite file `Attachments.sqlite.db` in the instance data
folder, which the base registers and `reset.ps1` deletes with the rest of `data/`. A host that
registers its own connection in the connections callback of `AddUmbracoAppHost` names it here
instead, so its records and their files live in one database and one backup.

| `Attachments` setting | Meaning |
|---|---|
| `Connection` | The registered connection whose database holds the two tables. Default `AttachmentsDb`. |
| `MaxFileSize` | The largest file any block accepts, in bytes. Default 26214400 (25 MB). |
| `AllowedExtensions` | Extensions such as `.pdf` every block is limited to. Empty, the default, allows any; a block then chooses its own. |
| `RetentionDays` | How long a removed file stays before the purge deletes it. Default 30. |

The endpoints are `/api/attachments/{page}/{block}`: `GET` lists the record's files with their
earlier revisions, whether the caller may write, the block's purposes and limits, and the
antiforgery token the writes carry in the `RequestVerificationToken` header; `POST` uploads a
multipart body with the parts `file`, `purpose`, `label` and `notes` and answers 201 with the
item; `GET /{id}` downloads the current file or an earlier revision with its stored name and type,
`Content-Disposition: attachment`, `nosniff` and `no-store`; `PUT /{id}` replaces the current
file with a new revision when the body has a `file` part and changes the details with the text
parts, where an omitted part keeps a value and an empty one clears it; `DELETE /{id}` removes
the link, which leaves the lists at once and keeps its rows for the purge. A declared length over
the limit is refused with 413 before the body is read, and the request body is capped at the limit
so an undeclared one is cut off; a type the block does not accept is 415.

Every call is authorized the way a form call is: the page's access first (a member Public Access
admits, a backoffice user who may edit the page, and with no Public Access any backoffice user or
an anonymous visitor reaches the resource too), then the owning record's resource must load the record for the caller, which is what
grants reading. Writing is allowed to whoever is signed in and may read, unless the resource
implements `IAttachmentOwner` and says otherwise. An anonymous visitor writes only when the resource
implements it and allows them. The data source engine implements it, so a data source's write
groups write and its read groups only read. Every file and revision id
is looked up among the resolved owner's live links, so a readable record paired with another
record's file is not found. A read-only block refuses writes with 403. Each operation is logged
like a form operation, under `ApplicationAttachments`.

The form runtime dispatches `app-form:loaded` and `app-form:saved`, bubbling events whose
`detail.record` is the record shown, so a block inside a form follows it: a new record shows
"Save the record before adding files", and the first save loads the list with the new key. A
page script can listen for the same events. The block dispatches `app-attachments:changed` after
an upload, a replacement or a removal.

Removed files are deleted by hand: `POST /umbraco/management/api/v1/attachments/purge` with a
backoffice token of an Administrator, and an optional body `{ "retentionDays": 30 }` that replaces
the configured retention, deletes every link removed longer ago than that with every revision
behind it and answers with the counts. Earlier revisions of files still in place are never purged.

## Tabular forms

A **Tabular form** block (`appTabular`, in the block catalogue's **Content** group) shows the rows
of a child data source that belong to one record and lets staff add, change and delete several of
them, saved together: the lines of an order, the guests of a booking. The code is in
`src/UmbracoAppHost/Tabular/` and `DataSources/SqlDataSource.Rows.cs`; the partial is
`Views/Partials/ApplicationBlocks/Tabular.cshtml`.

**Data source** names a published data source (a C# resource cannot be edited as a table) and
**Link field** its field that holds the record's key, such as `orderId`. Placed in a form, the
block follows the record the form is editing; placed on its own it names the record as an
Attachments block does, with **Owner resource** and **Owner key** (`:PARAM.name`, `:FIELD.alias`
or the key itself). **Columns** lists the field aliases to show, separated by commas, in order;
empty shows every field but the link field. **Without adding**, **Without deleting** and **Read
only** narrow what the data source allows and never widen it; **Row limit** (1 to 1000, 200 unless
set) is the most rows listed for one record, beyond which the block says so rather than show part
of them; **Empty message**, **Add label** and **Save label** word the block. Publishing checks that
the data source is published and readable, that the link field is a field it writes (not a key the
database assigns or a read-only field), that every column is one of its fields and not the link
field, the owner rules of an Attachments block, the row limit, and, unless the block never adds
rows, that every required field is a column, the link field or has a default, so a new row can be
completed.

The endpoints are `/api/tabular/{page}/{block}`, called with the page's query string so the
form's key parameter (`id` by default) names the record. `GET` answers with the owner, the data
source, `canAdd`, `canUpdate`, `canDelete`, `maxRows`, `fields` (the columns, each with its key,
label, kind, `required`, `readOnly`, `maxLength`, `minimum`, `maximum`, `choices` and
`hasServerDefault`), `rows` (each with its `key`, `version`, `values` of the columns only and
`capabilities`), `newRow` (the defaults a new row starts from) and the antiforgery token. Numbers
arrive as invariant text, as the form endpoints send them. `POST` carries that token in the
`RequestVerificationToken` header and a body `{ "submissionKey": "<guid>", "changes": [...] }`,
each change being `{ "operation": "create", "values": {...} }`, `{ "operation": "update", "key",
"version", "values": {...} }` with only the changed columns, or `{ "operation": "delete", "key",
"version" }`, at most 200 of them. Only the block's columns may be sent; the link field is dropped
and set by the server.

A batch is applied in one transaction, in order, through the data source engine with the same
checks a single form operation makes: the allow flags, the write groups, the policies, the field
rules, the version, the audit columns and the row restriction. The first change the engine refuses
stops the batch and nothing is written; the answer has that change's HTTP status (403, 404, 409
for a stale version, 422, 503) and `{ "status", "index", "errors" }`, where `index` is the
position of the change and `errors` is keyed by field alias, `""` for the change as a whole. A
successful batch answers 200 with the list as it is afterwards and a fresh token. The submission
key makes a retry harmless: the same key with the same changes answers with the rows and writes
nothing again, and the same key with other changes is refused.

Every call is authorized as an Attachments call is: the page's access, then the owner's resource
must load the record for the caller. The rows are then read and written through the child data
source for the caller, with its own read and write groups, policies and restriction; a row whose
link field does not hold the record's key is not found, whatever its key, a new row is linked to
the record whatever the request says, and an update cannot move a row to another record. A read
only block refuses saves with 403, and without adding or deleting refuses those changes. Each
list and save is logged under `ApplicationTabular`.

The runtime draws one table row per row and one input per cell by kind (text, number, date, time,
date and time, checkbox, or a select for a field with choices); a read-only field, or a row the
caller may not change, shows its value as text. **Add row** appends a row from `newRow`,
**Delete** marks a row (and **Undo** clears the mark), and **Save rows** sends every new row, the
changed cells of every other row and every marked row as one batch, after the browser's own
checks. A refused batch lists its errors by row and column, marks the row and the cells and keeps
every value entered; a stale row offers to reload the rows. The inputs carry `form=""` and
`data-column`, never `data-field`, so the enclosing form neither collects nor submits them. Inside
a form the block follows `app-form:loaded` and `app-form:saved` like Attachments, showing "Save the
record before adding rows" for a new record; after a save it dispatches `app-tabular:saved` with
`detail.rows`.

The sample adds an `ORDER_LINES` table (`Sample/Schema/*/002-order-lines.sql`, seeded with one to
three lines for each of the first 120 orders), the **Orders lines** data source (`orders.lines`,
under Data sources) and an **Order lines** block in the Orders form, after its Attachments block;
the Website content blocks page carries the same block in the website shell. As for a schedule's
sources, the data source node has a lower sort order than the pages that use it, since a first
import publishes the nodes of one level in sort order and publishing a page checks its data
source. In tests,
`site.Tabular(client, pageKey, blockKey)` lists and saves as the runtime does, with
`ListAsync(query)` and `SaveAsync(changes, query, submissionKey)`.

The block does not yet offer lists of values per column: a select's choices come from the data
source's mapping. Nor does it page, sort or filter the rows; it shows a record's rows in key order.

## Schedules

A **Schedule** block (`appSchedule`, in the block catalogue's **Content** group) shows resources
against time: a tape chart of rooms and the stays in them, an activity calendar, vehicles and
their allocations. It reads two sources and renders one table on the server, with the rows of
the first source down the side and the bars of the second across a window of days or hours. The
code is in `AppBlocks/Schedules/`; the partial is
`Views/Partials/ApplicationBlocks/Schedule.cshtml`.

**Rows source** and **Bars source** each name a published report definition that selects a data
source, or a data source, by alias. Both are read through the data source engine, as lists of
values and paper reports are, so the source's read groups, row restriction and policies decide
what the signed-in person sees; a report definition with inline SQL is not accepted, since its
own authorization would not apply. The rows need the columns `key`, `label` and `group`; the
bars need `key`, `row`, `start`, `end`, `label`, `kind` and `link`, where `row` is the key of a
row. Column names and field aliases match in any letter case, so `ROOM_NO` mapped to the field
`key` serves. Publishing checks both sources against the catalogue and names the columns that
are missing, as it refuses a source that is not published or cannot read its schema. A saved
export's first import publishes the nodes of one level in sort order, so give the source nodes
a lower sort order than the schedule's page, as the sample does, or the page stays unpublished.

Both sources may also supply `color`, `textColor` and `description`. A rows source's
`orientation` column can turn its bars into vertical markers across the schedule; descriptions
open in a details popover. The Jump to menu offers months and quarters. Anonymous readers can
see a schedule only when both sources are Public and neither requires `:USER_*` values.
The sample's sources remain Not public. See the current [Schedule guide](../AppBlocks/docs/schedule.md).

The window comes from the page's address, `?from=2026-06-01&to=2026-06-30`, or else from the
block's **Days before today** and **Days after today** (7 and 14 unless set); both dates are
inclusive, an end before the start becomes the start, and a window is cut at 400 days, or 31
days of hour columns. The resolved dates reach both sources as `:PARAM.from` and `:PARAM.to`,
so a bars query can select the stays that touch the window. The block offers Earlier, Today and
Later links that move the window by its own length and keep the address's other parameters.
**Columns** chooses one column per day or one per hour of every day in the window.

Each row of the table is one row of the rows source, under a heading for its `group` when it
has one; groups appear in the order they are first seen, rows in the order the source returned
them. Each bar is a link in the cell where it starts, spanning its columns; a bar's `end` is the
last day or hour it covers. Bars of one row that overlap in time are packed into sub-rows, each
bar taking the first sub-row with its columns free (`Schedules/ScheduleLanes.cs`), so every bar
stays visible and reachable with the keyboard. A bar with no `end` runs to the window's edge
with an open marker; a bar that began before the window or ends after it is cut at the edge and
marked. A bar's `link` is where it leads; when it is empty the **Link template** is used, with
`{key}` and any other bar column in braces replaced by the bar's values, URL-encoded, and a
template starting with `/` taken from the site's root. The header has a row of months (or days)
over a row of days (or hours); weekends are tinted and today's column is marked. Bars take a
colour by `kind`, and a legend lists the kinds in the order they appear; a theme can set
`--ah-schedule-kind-1` to `-8`, and every bar carries `data-kind` for a host's own CSS.

The table is complete without script: sticky header rows and first column, the sub-rows as
heights, the bars as positioned links. The runtime only scrolls the window to today once and
shows a bar's details, which are inside the link for screen readers, as a tooltip on hover and
focus. A source that cannot be read, or that the person may not read, leaves a short message in
the block's place rather than failing the page.

The sample ships an **Orders schedule** page under the Sample Application and a **Website orders
schedule** page under the Website theme demo: the customers of the first 48 sample orders as rows, grouped by region, with each
order as a bar around today, pending orders open-ended, and the bars linking to the order form.
Its bars source (`orders.schedule-bars`, read through the `orders-schedule` report definition)
reads the view `ORDERS_SCHEDULE_BARS`, which the sample's schema scripts define for SQLite and SQL
Server because date arithmetic differs between them.

## Databases

The common host keeps SQLite data outside app/, under the instance data directory:

| File | Holds | Configured in |
|---|---|---|
| `data/Umbraco.sqlite.db` | Umbraco: users, members, content | Common host default; override `ConnectionStrings:umbracoDbDSN` in instance settings |
| `data/App_Data/Reports.sqlite.db` | Saved reports and report authorization grants | the base's `Reports/InteractiveReportsSite.cs`, connection `ReportsDb` |
| `data/App_Data/ReportsSample.sqlite.db` | Sample orders for the `orders` report, the Orders data source and the `sample.orders` resource | the common host's `Sample/SampleReport.cs`, connection `ReportsSampleDb`, schema `Sample/Schema/` |
| `data/App_Data/Attachments.sqlite.db` | Record attachments: the files, their revisions and their links, unless `Attachments:Connection` names another connection | the base's `Attachments/`, connection `AttachmentsDb` |

## Application databases

An application with its own tables registers its database from the `configureConnections` callback of
`AddUmbracoAppHost`:

```csharp
builder.AddUmbracoAppHost(connections => connections.AddApplicationDatabase("InnDb",
    ApplicationSchema.FromDirectory(Path.Combine(builder.ProjectRoot(), "Schema"))));
```

The connection is registered with AppBlocks under its name, so forms, data sources and reports use
it like any other. Settings → AppBlocks → Connections lists it as read-only, without a connection
string, and report blocks can choose it and its tables. Listing its tables opens it, which ensures
its schema, as any open does wherever its connection string comes from; only the connection test
leaves the schema alone. Its connection string is `ConnectionStrings:InnDb` with
`ConnectionStrings:InnDb_ProviderName`, `Microsoft.Data.Sqlite` or `Microsoft.Data.SqlClient`; as
for Umbraco, a connection string without a provider name is SQL Server. Without a connection string
the database is a SQLite file in the instance's `App_Data` folder, `InnDb.sqlite.db` unless the
third argument names another file.

The schema has a folder per dialect, `Schema/sqlite/`, `Schema/sqlserver/`, `Schema/postgresql/`
and `Schema/oracle/`, of `.sql` files that run in file-name order (`001-tables.sql`,
`002-views.sql`); `ApplicationSchema.FromEmbedded(assembly, "Schema")` reads the same layout from
embedded resources of a project that sets `GenerateEmbeddedFilesManifest`. Scripts ensure rather
than create: `CREATE TABLE IF NOT EXISTS` on SQLite,
`IF OBJECT_ID(...) IS NULL` and `IF COL_LENGTH(...) IS NULL` on SQL Server. On SQL Server a line
holding only `GO` ends a batch, so a `CREATE VIEW` can start its own. On SQLite each statement runs
alone, and an `ALTER TABLE ... ADD COLUMN` whose column is already there counts as applied, because
SQLite has no `ADD COLUMN IF NOT EXISTS`; any other error fails the script. PostgreSQL runs a file
whole, and Oracle splits it on lines holding only `/`. `Then((connection, dialect) => ...)` adds C#
steps that run after the scripts, such as a seed.

The first time the process opens a connection to the database, the schema is ensured on a separate
connection before the open returns. The version table `APP_SCHEMA` (script file name, SHA-256,
time applied) is created if missing, each script runs in its own transaction together with its
row, then the steps run. Every process start runs every script once, and later opens skip the
work. A failing script rolls back, fails the open with its name in the message, and is tried again
at the next open. Script file names identify the rows, so two schemas that share a database need
different names. For a connection registered some other way,
`ApplicationDatabase.EnsureAsync(connection, schema)` does the same on an open connection; the
record attachments store does this for the connection `Attachments:Connection` names.

The sample database uses it: `Sample/Schema/` holds its SQLite and SQL Server scripts, embedded in
the library, and `SampleData.Schema` adds the seed of 500 orders. Setting
`ConnectionStrings:ReportsSampleDb` moves the whole sample: the `orders` report, the Orders data
sources and schedule, and the `sample.orders` resource all use that connection.

The sample is for SQLite and SQL Server. It has no scripts for Oracle or PostgreSQL, so a saved
`ReportsSampleDb` connection to either is expected to fail when it opens, at the seed's first query,
because the tables do not exist; the seed and the `sample.orders` resource also write `@` parameters,
which Oracle does not take. That comes from reading the code: the sample has never been run on either.

## Moving to SQL Server later

Treat a database-provider change as a separate migration, with a backup and rehearsal.
uSync transfers supported schema and content and imports the working XML on first boot of
a new database. Imports on ordinary restarts are disabled. Instance exports live outside `app/` in the instance's `uSync/` folder;
include that folder in the instance's backup policy.

1. Create an empty SQL Server database and grant the application login `db_owner`.
2. Point `ConnectionStrings:umbracoDbDSN` at it with provider name
   `Microsoft.Data.SqlClient` and keep `Umbraco:CMS:Unattended:InstallUnattended` true.
3. Start the site. Unattended install creates the schema and the local administrator.
   Verify the first-boot import and explicitly import any additional required uSync exports. Copy the instance's `media/`
   folder if the site has media.
4. Members are not part of uSync's default export. Native OpenID auto-creation can create new
   accounts at first sign-in; migrate existing member data, group assignments and history when needed.
5. For Interactive Reports, either keep `ReportsDb` on SQLite or point the `ReportsDb`
   connection at SQL Server; the package creates its tables. Move saved reports with the
   administration page: **Download** each report's JSON on the old site and
   **Upload JSON…** on the new one, then publish as needed.

## Hosting under a sub-path

`Umbraco:CMS:Hosting:ApplicationVirtualPath` (for example `/portal`) hosts the whole site,
backoffice included, under a prefix. Umbraco strips it from incoming URLs before content
routing and prepends it to the links it generates. The base's `VirtualPathHosting.cs` also applies
it with `UsePathBase`, which strips the prefix when the request still carries it and is a no-op
when the host has already moved it into the path base, as an IIS child application does, and
prefixes the three `Umbraco:CMS:Security:AuthorizeCallback*PathName` settings so OpenIddict
registers the prefixed OAuth callback. The URLs that report definitions emit get the prefix as
well (see [Interactive Reports](#interactive-reports)). One setting covers both hosting styles.

Umbraco 18.1.1 assumes the backoffice lives at `{origin}/umbraco` in several places, and
the base's `Overrides/` closes each gap:

| Upstream gap | Symptom | Workaround |
|---|---|---|
| The rewrite that serves the cache-busted backoffice assets matches `{virtual path}/umbraco/backoffice/{hash}/` against a request path that has already had the path base removed | Every backoffice script and stylesheet returns 404 and the page stays blank | `BackOfficeAssetsUnderPathBase` puts the virtual directory back onto those requests before Umbraco's pipeline sees them |
| The import map is emitted with root-absolute entries, and the hashed prefix is only applied when the virtual path is `/` | Core modules are imported unhashed from `/umbraco/backoffice/...`, package modules from `/App_Plugins/...`, and uSync's modules load twice and fail to register | `BackOfficeImportMapUnderVirtualPathComposer` decorates `IPackageManifestService` so every root-absolute entry carries the virtual path before Umbraco adds the hash |
| The backoffice SPA hard-codes `window.location.origin` plus `/umbraco` for its management API, OAuth callback and post-login navigation | Sign-in leaves the prefix: the callback lands on `/umbraco/oauth_complete` and the token exchange fails | `BackOfficeAppUnderVirtualPath` adds the `server-url` and `backoffice-path` attributes the app element accepts to the served page, because this project's Razor setup cannot override the precompiled view |

Tested under `/wing` on Kestrel over HTTPS with only that setting: the content page,
the backoffice login, IdentityServer SSO, the content editor, the management API, SignalR and
`/wing/api/reports` all work under the prefix with no console errors. Two requests still use
root-absolute URLs compiled into Umbraco's scripts: the login page fetches the public package
manifests from `/umbraco/management/api/...`, and the SPA loads its logo from
`/umbraco/backoffice/assets/`. Behind Kestrel, or a reverse proxy that also forwards the root,
both succeed; in an IIS child application they would not, which costs the logo and, untested,
possibly the login page. All workarounds do nothing when no virtual path is configured and become
redundant once the gaps are fixed upstream. The asset symptom is also reported for 17.2.1 on the
[Umbraco forum](https://forum.umbraco.com/t/umbraco-backoffice-17-not-working-when-deployed-as-an-application-under-main-site-in-iis/7677).

## Telemetry

Umbraco posts a daily report to telemetry.umbraco.com from a recurring background job. Its
lowest level, Minimal, still sends the site identifier; there is no off switch. The base's
`Overrides/DisableTelemetryComposer` removes the job's registration before the container is built
and fails boot if the registration is not found, so an upgrade that changes how the job is
registered cannot quietly turn telemetry back on. The Gravatar lookup that earlier versions
made for backoffice avatars is gone in 18: avatars are either an uploaded file or nothing.
