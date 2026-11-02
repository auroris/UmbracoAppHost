# One application, independent sites

This guide covers replicating the reference: publishing its application, creating sites from
it, and updating them. For Visual Studio startup or a new web project referencing the library,
see [Getting started](getting-started.md). Every site runs the same published `UmbracoAppHost.Web`
application, including both frontend shells, UmbNav, the form/report infrastructure, and
the available website views. Websites and AppHost applications differ in their database
content and instance settings. They do not have separate source projects or builds. A host in
another repository that references the library is published and replicated the same way with
`-Project`; its sites run its own application.

An application such as Aerodrome Status can live as pages inside a website: an insert form,
a C# resource implementing its operation policy, and a display component reading the latest
entry. Resources are added to the shared code and delivered to all sites by an update. Only sites
that use their pages need their data connections. An unused building block does not require
another frontend or deployment. The Aerodrome API/schema has not yet been imported.

## Reference installation

Open `UmbracoAppHost.slnx` in Visual Studio and press Play with the **Website** launch profile.
The terminal equivalent is:

```powershell
dotnet run --project src/UmbracoAppHost.Web
```

The reference runs `src/UmbracoAppHost.Web/UmbracoAppHost.Web.csproj`, which is also what gets
published for sites. `reference/instance.json` and its data folders are created automatically and
retained. The launcher prints its HTTP address through the application log. Sign in as `admin` with
the generated password in `reference/instance.json`. `UmbracoAppHost:AdminUserName` and
`UmbracoAppHost:AdminEmail` configure the built-in administrator's username and contact
email; their defaults are `admin` and `admin@localhost.local`.
Reference data and credentials are git-ignored. Shared server source and CMS views live in
the `UmbracoAppHost` library. The .NET build generates and packages the frontend automatically.
See [Getting started](getting-started.md) for the reset script and using the library from a
separate web project.

Configure the reference with the defaults wanted in new sites. Every backoffice save is exported
to `reference/uSync/`; copy the changed files into `src/UmbracoAppHost/uSync/` and commit
them to make them part of the saved export. Each new site imports the complete saved export, and
updates deliver its definition folders to existing sites. The content tree and selected templates
determine the shell: a block/report page beneath a website uses the website shell; beneath an
Application root it uses the application shell.

Upgrade Umbraco and plugins in this checkout, test the reference, then run `update-site.ps1`
for each site. Existing sites retain their content and settings; startup applies database
upgrades and explicit migrations.

The saved export is deliberately small. A production site's document types, content and server
integrations belong in its own project. The published application does not invent those schemas
or fetch production data.

## Umbraco 18 frontend

`Frontend/` contains the website and application frontend for this Umbraco 18 host.
`UmbracoAppHost` packages the built CSS and JavaScript for .NET 10. Published builds
include those assets.

The website theme uses `site.js` and `site.css`, and the application shell uses `app.js` and
`app.css` from the same build. The shared host packages these two shells; standalone applications,
branded themes and their service integrations belong in their own projects.

Build the assets and generate the host's Razor views:

```powershell
cd Frontend
npm run build
npm run templates:emit
```

The template sources use Umbraco 18 dictionary and content navigation APIs directly.
Generated views load the bundles with ASP.NET Core asset tags. CMS partials, models,
controllers, UmbNav integration and form/report backoffice extensions all target this host.

## Create a site

```powershell
pwsh new-site.ps1 -Name MyWebsite -Destination E:/Sites/MyWebsite
pwsh E:/Sites/MyWebsite/start-site.ps1
```

`new-site.ps1` publishes `src/UmbracoAppHost.Web`, or the project `-Project` names, in Release
configuration to `artifacts/builds/<timestamp>-<commit>/` and creates the site from it: `app/` is the published
application, `instance.json` holds a fresh site identity, image signing key and administrator
password, and `uSync/` receives the complete saved export. The first start installs Umbraco and
imports that folder. Publishing needs the .NET 10 SDK, Node/npm, PowerShell 7 and the sibling
`InteractiveReports` and `IdentityServer` checkouts (see [Getting started](getting-started.md));
a site needs only the ASP.NET Core 10 runtime.

A build folder is self-contained. `build.json` records the build id, Umbraco version, source
revision, the published project (`project`, its file name), the application it runs (`assembly`)
and file hashes, and the folder carries copies of the scripts. Pass `-Build <folder>`
to create or update several sites from one publish, or copy the folder to a machine without this
checkout and run the scripts inside it.

`-Project <project file>` publishes another web project that references the library instead of
`src/UmbracoAppHost.Web`: a downstream host, as [Getting started](getting-started.md#use-the-library-in-another-repository)
describes. `-Assembly <file>` names its application when that is not the project's name with
`.dll`. `-MsBuildProperties` passes MSBuild properties to `dotnet publish`, for example
`-MsBuildProperties '-p:InteractiveReportsRoot=D:\src\Interactive-Reports'` when the sibling
checkouts are elsewhere; they come after the script's own, so `-p:Configuration=Debug` replaces
the Release configuration. The three apply only when the script publishes, not with `-Build` or
inside a build folder. The build's `app/Views/` holds this checkout's copies of the views the
library ships, as a start would write them, whatever the project folder held; otherwise a site's
first start would rewrite a stale copy and its next update would refuse the changed file.

A site records its application in `.apphost/instance.json`, and `start-site.ps1` runs that file;
sites and builds made before the scripts recorded it run `UmbracoAppHost.Web.dll`. When the
published `appsettings.json` sets `UmbracoAppHost:Samples` to
`false`, the site's `uSync/` folder starts empty, and its first start fills it with the library's
export without the samples and the host's own export, which the publish puts beside it in
`app/uSync/`.

A site consists of:

| Location | Ownership |
|---|---|
| `app/` | The published application, identical in every site created from the same build |
| `app/appsettings.json` | Host configuration the operator can edit, preserved during updates; `InstanceRoot` points at the site folder |
| `instance.json` | Site identity, credentials and settings |
| `data/` | Umbraco database, report stores, sample data, persistent sign-in keys |
| `media/` | Uploaded media |
| `uSync/` | The site's uSync folder: the saved export at creation, then the site's own exports and refreshed definitions |
| `start-site.ps1` | Kestrel launcher; IIS does not use it |
| `.apphost/` | Installed-build inventory, launch lock and update backups |

The normal permissive development password checker is disabled in sites. Default SQLite paths
are resolved relative to the site at runtime, so an offline copy stays portable. Explicit
external database connection strings, identity-server settings and secrets belong in
`instance.json` or environment overrides. Create every site with `new-site.ps1`; copying an
already-used site also copies its identity, members, data and credentials.

For IIS, install the .NET 10 Hosting Bundle and point the application's physical path at
`app/`; its `appsettings.json` already sets `InstanceRoot` to the parent folder, so the site
finds its own data without further configuration. Stop the application pool while updating.
The PowerShell launcher is for Kestrel; IIS manages its own process lifetime. The application
holds a lock under either host, preventing concurrent starts and updates while it
is running. Media and databases remain outside the application's physical path.

To choose or reset the administrator password, set `UmbracoAppHost:AdminPassword` in
`app/appsettings.json` and save. The running site applies it without an IIS restart.
A valid value also unlocks the account, resets failed login attempts and removes its two-factor
enrolment. An empty or missing value leaves the current password, lockout state and enrolment
alone. `BackOfficeLogin:DefaultFlow` defaults to `Auto`, which prefers the configured identity server and falls back to local
password sign-in when SSO fails. Explicit `IdentityServer` and `Form` choices
remain available. For emergency access blocked by lockout or two-factor authentication,
explicitly enable the Debug flow with `Umbraco:CMS:Hosting:Debug=true` and
`BackOfficeLogin:DebugFlow=Debug`, then restart. Debug accepts any nonempty password for an
existing account. Restore normal settings after repairing access. See
[Getting started](getting-started.md) for the JSON example, password rules and recovery steps.

## Update a site

```powershell
pwsh update-site.ps1 -Site E:/Sites/MyWebsite -WhatIf
# Stop the running site, then:
pwsh update-site.ps1 -Site E:/Sites/MyWebsite
pwsh E:/Sites/MyWebsite/start-site.ps1
```

`update-site.ps1` publishes the reference (or takes `-Build`), checks that the site's shipped
files are unmodified, stages the new application, carries `app/appsettings.json` and files
created through Umbraco forward, moves the old application to `.apphost/backups/` and switches
the staged one in. It refuses conflicts instead of overwriting them; templates are compared
without incidental BOM/newline changes. Site settings, databases, uploaded media, sign-in keys
and content are preserved. Keep custom source out of `app/`; shared code changes belong in the
reference checkout.

It then copies the saved export's definition folders (`ContentTypes`, `DataTypes`, `Dictionary`,
`Languages`, `MediaTypes`, `MemberTypes`, `RelationTypes` and `Templates`) into the site's
`uSync/` folder and lists what changed. Files the site added, and files the reference no longer
ships, are left in place, with one exception: the paths in `deployment/appblocks-definitions.txt`,
the definitions AppBlocks and its companions install themselves, are removed and listed. They are
in that folder because earlier exports shipped them and because uSync's export on save writes them.
Removing them is housekeeping: a uSync import refuses those keys in any case, so an old copy cannot
put an old definition back. AppBlocks applies pending tracked package migrations at startup,
including its initial schema installation. Existing definitions otherwise stay as the site configured them.
The host’s refreshed uSync definitions are not imported automatically: start the site, open
**Settings → uSync** in its backoffice, run **Report** to see what would change, then **Import**.
`Content/` and `Domains/` are not copied; pages belong to the site after creation.

`update-site.ps1` takes the same `-Project`, `-Assembly` and `-MsBuildProperties` as
`new-site.ps1`. It refuses a build whose application differs from the site's, so a site created
from a downstream project is updated with `-Project` and that project, or with `-Build` and a
build of it.

`refresh-definitions.ps1 -Instance <folder>` does the same refresh for an instance that a host
runs from its own project rather than from a site's `app/` folder, such as a downstream host's
development instance after the host was rebuilt against a newer library. It reads the instance's
`.apphost/runtime.json`, copies the definition folders of the export in the host's build output
over the instance's `uSync/` folder, removes the same list of AppBlocks' definitions and lists what
changed; `-WhatIf` only lists. The host may be
running, and the script takes no lock. Import from the backoffice as above. Build folders carry a
copy of it with the other scripts.

The updater rejects Umbraco downgrades and major Umbraco upgrades, which need their own plan.
It does not restart services or migrate a live database; startup applies pending migrations.
Reinstalling an older build does not undo a database migration; use the matching database
backup when rollback is necessary. Take a backup before updating a production site.

## Schema evolution

New databases receive the host’s definitions from the saved uSync export during uSync’s built-in
first boot. AppBlocks installs its own definitions through Umbraco’s package importer and tracked
package migrations; the host also ensures missing definitions exist before each uSync import.
Existing sites receive changes to the host’s definitions through `update-site.ps1` and a backoffice
import, while AppBlocks applies its pending package migrations at startup. Automatic AppBlocks
schema work honors `Umbraco:CMS:PackageMigration:RunSchemaAndContentMigrations=false`.
The host adds no separate schema or content migration runner. Form Interface’s switch to Single
Block updates its data type but does not convert old saved values or historical versions. Recreate
and republish affected interfaces, or reset a disposable reference to import the updated samples.
Other content conversions need an explicit migration appropriate to the site.
External Oracle/SQLite business databases remain separate from uSync.
uSync first-boot imports are enabled; imports on ordinary restarts remain disabled so an
old export cannot overwrite editor changes. The library includes the saved export in its assembly
and build/publish output. Initial content is seeded once and is thereafter owned by the site.

## Verifying the scripts

`deployment/test-sites.ps1` publishes a build (or takes `-Build`), creates two disposable sites,
boots both, updates one, and checks what the update preserves and refreshes.
`deployment/test-startup.ps1 -Project src/UmbracoAppHost.Web/UmbracoAppHost.Web.csproj -ContentRoot <temp folder> -Reset`
exercises a built host and the reset script against a disposable instance.
`deployment/test-downstream.ps1` creates a disposable downstream project in a temporary folder,
with its own export, template, SQL data source and paper report, builds it against this
checkout's library and boots it. It checks that no sample content is imported, downloads the
paper report as HTML as the administrator, changes a library view and dictionary definition
(restoring both afterwards), rebuilds, runs `refresh-definitions.ps1` and checks that both
arrive, then resets the instance and boots it again. The temporary folder keeps the project and
the host's output of each start. `test-sites.ps1` and `test-downstream.ps1` take
`-MsBuildProperties`, for example
`-MsBuildProperties '-p:InteractiveReportsRoot=D:\src\Interactive-Reports'`.
