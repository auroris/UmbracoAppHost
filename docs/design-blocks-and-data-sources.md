# Design: themes, blocks and data sources

Status: design and implementation record, updated 2026-10-24. WP1–WP3 landed on 2026-10-23;
WP4–WP8 are implemented; verification is recorded below. The authentication platform landed on
2026-10-23 (see the README's "Single sign-on", "Who gets an account" and "Creating accounts
from the directory"). The work breakdown records delivery and verification separately.

## Purpose

UmbracoAppHost is the platform new applications are built on without compiling: editors
assemble application pages from blocks, and the data behind those pages comes from declarative
data sources rather than hand-written C#. The mental model is Oracle APEX, which the
applications come from: regions laid out on a grid, items bound to a row source, automatic row
fetch and row processing, lists of values, and interactive reports. Interactive reports and the
APEX-style application shell already exist; this design adds the rest.

Principles, as decided by the owner:

- Use what Umbraco and the sibling libraries already provide: the Block Grid, uSync, Public
  Access, the Interactive Reports data adapter. No new persistence framework.
- Keep implementations simple and general. Discuss a proposed feature restriction with the
  owner before adding it, and explain the reason for it.
- Back-office access is the trust boundary. Anyone who can edit a page may put raw HTML, CSS,
  JavaScript or SQL on it; the platform does not gate that further. It still keeps database
  credentials out of content and runs every query parameterised.
- Everything an application needs is configured in one place, the CMS, exported by uSync like
  every other definition. The configuration file stays as the easy default and as the seed.
- Twelve columns everywhere, as Bootstrap and APEX use, and as the
  Umbraco 13 site already does.
- SQLite first. Then the databases the Interactive Reports adapter speaks: SQL Server, Oracle
  (12c and 11g), PostgreSQL and SQLite, which also covers the databases Umbraco itself supports.

## Baseline before this work

- Two shells built by `Frontend/`: the application shell (`Views/App*.cshtml`, APEX-like) and the
  website shell.
- The Block Application Page (`appBlockPage`) with the Application Blocks grid data type, already
  on twelve columns, and these blocks: Two columns (`appColumns`), Fieldset (`appFieldset`), Form
  interface (`appFormInterface`), Form actions (`appActions`), Interactive Report (`appReport`),
  and the field blocks `appText`, `appTextArea`, `appNumber`, `appDate`, `appTime`,
  `appDateTime`, `appCheckbox`, `appSelect` and `appDisplay`.
- Forms run through `IFormResource` implementations registered in C# (`FormResource<TCreate,TUpdate>`,
  the sample `OrdersFormResource`) behind `/api/forms/{page}/{form}`: antiforgery, typed
  validation, concurrency tokens, idempotent creates, member page access (`AppPageAccess`), and
  publish-time validation of bindings (`AppBlocksValidator`).
- Reports come from `InteractiveReport:Reports` in appsettings plus the embedded
  `umbraco-reports.json`; connections are registered in code; `AppReportDefinitionStore` layers
  the configured catalogue with published block instances.
- The signed-in person is known in one shape (`SsoAccount`): SID, account name, username,
  email, groups. Those are the bind variables data sources need.

## 1. Themes

A theme is three things bundled per site root: the master layout, a CSS bundle, and the styling
of the shared block partials. Two ship with the platform:

| Theme | Shell | Look |
|---|---|---|
| Application | `AppMaster` | APEX Universal Theme conventions: regions with title bars, item labels above or to the left, dense twelve-column grid, toolbar with the account menu |
| Website | `WebsiteLayout` | A small public site: a top bar with the menu, a hero on the home page, clean typography, a twelve-column block grid. It has no dependencies and no one's branding, and serves as the demo |

Rules:

- A Web Site chooses its native Umbraco template: Website home supplies the website shell and
  Application site supplies APEX. Ordinary Web Pages inherit the nearest site's template;
  a nested Web Site starts its own branch. The optional `theme` property overrides the shell
  and allows registered themes; legacy `application` roots retain their default.
- Block partials emit one neutral markup with stable class hooks (`ah-grid`, `ah-region`,
  `ah-field`, and so on) and never theme-specific markup. Each theme's CSS styles those hooks.
  Every block therefore renders in both themes, and a block is finished only when it looks right
  in both.
- Umbraco's default grid stylesheet (`umbraco-blockgridlayout.css`) does the column arithmetic;
  the themes restyle gutters, breakpoints and region chrome on top of it.
- The frontend build emits one CSS bundle per theme (it already emits both shells).
- A branded theme is a Razor class library of its own, with its own frontend build. It registers a
  `SiteTheme` for its layouts and a `ThemeLibrary` for its embedded `Views/` and `uSync/` from an
  `IComposer`, so a site only references it. Under the alias `website` it replaces the website theme
  without touching stored content. Its layouts use their own view names, because the library
  rewrites the views it ships, and its static web assets use their own paths for the same reason.
  Its stylesheet can import `Frontend/src/scss/components/_forms.scss` and
  `Frontend/src/scss/site/_blockgridlayout.scss`, the block styles every theme shares, and it
  must define the `--ah-*` tokens they read.

### Web Site and Web Page authoring

The intended application structure is a public Web Site for an overview, support and
documentation, with a nested Web Site using APEX for the working application. Native Umbraco
Public Access protects that branch. A splash page will be added during the v13 migration; the
site model works beneath it without a fixed fallback document ID.

Web Site and Web Page share their blocks, sidebar, CSS and JavaScript through the Web Page
Content composition. Navigation Menu is a separate document-type composition holding the
UmbNav property; initially only Web Site composes it. Menus and badges use nearest-ancestor
fallback. CSS and JavaScript accumulate from root to page. Ordinary Web Pages use the inherited
site template; explicitly selected templates such as Site sign in remain available for service
pages. See [Web Sites and Web Pages](application-blocks.md#web-sites-and-web-pages) for setup.

## 2. Layout blocks

The grid stays at twelve columns. Layout blocks are element types with areas; the owner's
six-column spec maps onto twelve exactly:

| Block | Areas (column spans) | Notes |
|---|---|---|
| Full width | 12 | One area |
| Two columns | 4 + 8, 6 + 6 or 8 + 4 | The existing `appColumns`, given a `split` setting instead of three separate blocks |
| Three columns | 4 + 4 + 4 | New |
| Region | 12, with a title bar | APEX's region: optional title, optional collapse, one area. The existing Fieldset keeps its form semantics (legend, required marker) |

The original Block Application Page palette accepts content blocks, fieldsets and regions
inside columns. The general Web Site/Web Page grid also accepts nested column layouts and
independent forms and reports within layouts. Columns stack below the theme's breakpoint.
The element types and the Forms container's grid ship in AppBlocks' `Schema/` folder, the page and
website grids in the saved uSync export (`src/UmbracoAppHost/uSync/`), and the partials in AppBlocks'
`Views/Partials/ApplicationBlocks/`, which renders block pages with the form context the field
blocks need; Umbraco's generic `Views/Partials/blockgrid/` partials stay with the website's own
block grids.

## 3. Content blocks

- **Rich text** (`appRichText`): the built-in Tiptap editor on an element type; the theme styles
  the output.
- **Raw HTML** (`appHtml`), **CSS** (`appCss`) and **JavaScript** (`appScript`): a code editor
  property (`Umb.PropertyEditorUi.CodeEditor`, with the language set). HTML renders where the block
  sits; CSS is collected once into the page head, de-duplicated by content; JavaScript renders at
  the end of the body, de-duplicated the same way, and carries the request's Content Security
  Policy nonce (`HttpContext.Items["CspNonce"]`) when the site sets one. No publish-time gate:
  back-office access is the trust boundary, and the README says so.
- **Form elements**: the existing field blocks gain `hidden` (bound and submitted, rendered as a
  hidden input, for keys and foreign keys), `defaultValue` (a literal or a bind variable, resolved
  when the page renders and applied by the form runtime to the fields a new row leaves empty),
  `width` (column span on the twelve-column grid) and `placeholder`.
  `required`, `readOnly`, label and help text already exist. Hidden fields are written; read-only
  fields are bound but never written.
- Later, not in the first cut: show-or-hide conditions on another field's value, client-side
  validation patterns, cascading selects.

## 4. Data sources

### 4.1 The concept

APEX gives a form region a row source (a table, or a SELECT), a primary key and two automatic
processes: fetch the row by key, write it back. The previous attempt put all of that in C#
resources, which is safe but means every new table needs code. The platform replaces that with
one declarative concept, the **data source**, and one engine that executes it. A data source
serves three uses with one authoring experience: the row source of a form, the query behind a
report, and a list of values for a select field.

### 4.2 The nodes

Data sources live as content nodes under a **Data sources** root (`dataSources`, no template,
never routed), so they are versioned, published, exported by uSync, shared across pages and
protected by the same user group permissions and start nodes as any content. The saved export
ships the sample Orders report and form as nodes.

| Node type | Fields |
|---|---|
| `sqlDataSource` | `alias`; `connection` (one of the connections registered in code, by alias); `source`: a table name or a SELECT; `keyColumns`; `mapping` (field alias to column; by name when omitted); `rowRestriction` (a predicate with bind variables, added to every fetch and write); `allowInsert`, `allowUpdate`, `allowDelete`; `concurrencyMode`: LastSaveWins or Checksum (a checksum of the fetched row); `insertDefaults` (column to literal or bind variable); `auditColumns` (created/updated by/at, optional); `readGroups`, `writeGroups` (member groups; empty means the page's own access); `customStatements` (insert, update, delete with bind variables; advanced, off by default) |
| `reportDefinition` | `alias`; `dataSource` (picker) or inline `connection` + `sql` for the migration period; the Interactive Reports options the appsettings definition has today: title, column labels, column help, controls, default and maximum page size, link columns |
| `listOfValues` | `alias`; `dataSource` or inline SQL; `displayColumn`, `valueColumn`; optional bind parameters for cascading later |

Connections stay registered in code with their connection strings in appsettings or
`instance.json`, exactly as reports do today: credentials never enter content, and the database
account each connection uses is chosen by a developer with least privilege. A SQL data source can
only do what its connection may do.

### 4.3 Bind variables

Statements and predicates use named bind variables, bound as parameters, never substituted into
text: `:USER_SID`, `:USER_NAME`, `:USER_KEY`, `:USER_EMAIL`, `:USER_GROUPS` (for `IN` lists where
the dialect allows), `:NOW`, `:PARAM.<name>` for a URL parameter of the page, and `:FIELD.<alias>`
for another field's current value. These are APEX's `:APP_USER` and friends in Umbraco terms.

### 4.4 The engine

- `SqlDataSource` is an `IFormResource`, so the form endpoints, the antiforgery and concurrency
  handling, the field blocks and the pickers need no change: the registry lists data sources
  beside the C# resources, and the field picker lists a data source's columns.
- Its field descriptors come from schema metadata (table columns, or the result set of the
  SELECT), mapped to the field kinds the blocks know: text, number, date, time, date and time,
  boolean. The contract version is a hash of the definition and the schema, so a changed
  definition invalidates loaded forms the way a changed C# contract does today.
- Reads go through the Interactive Reports connection factory and its dialects. Writes are
  generated from the mapping by a small DML builder per dialect: identifier quoting, parameter
  prefix, and how a new key comes back (`RETURNING` on SQLite and PostgreSQL, `OUTPUT INSERTED`
  on SQL Server, `RETURNING INTO` on Oracle, sequences on Oracle 11g). Identifiers are always
  validated against the introspected schema; editor text never reaches a statement except as a
  parameter value or, for custom statements, as the whole statement with bound parameters.
- Lost-update protection is the source's `concurrencyMode`: **LastSaveWins** by default, or **Checksum**, which
  compares the checksum the fetch returned with the row locked in the write transaction, as APEX does.
- SQLite and SQL Server reads and writes are exercised locally. PostgreSQL and Oracle builders
  and metadata support are implemented; their live-provider tests run when test connections
  are configured.

### 4.5 Authorization and safety

- Page access stays Umbraco Public Access, re-checked on every form call as today.
- A data source adds its own `readGroups` and `writeGroups`, the `rowRestriction` predicate bound
  to the signed-in person, and an `IDataSourcePolicy` hook in C# for rules that do not fit a
  predicate. Back-office administrators keep the access they have today.
- Every write is audited as form operations are now, and the optional audit columns record who
  and when in the application's own table.
- Result sizes and query lengths are bounded, as the directory lookups are.

### 4.6 Reports and lists of values as nodes

`AppReportDefinitionStore` gains a third layer: published `reportDefinition` nodes, resolved by
alias beside the configured catalogue and the block instances. A node with the same alias as a
configured definition replaces it, so a site adjusts a shipped report without a deployment; the
collision is logged once per process when the catalogue is loaded. The report picker lists both.
A `listOfValues` node supplies `appSelect` options through a picker. Deliberately configured
resource rules still validate saved values; the sample removes duplicate static choices so its
database lists control the available values.

### 4.7 Where definitions live

Both. The CMS is the primary authoring surface and the single place an application's
configuration is kept; appsettings remains supported for the built-in reports and as a seed for
new sites, and anything in appsettings can be moved into nodes without a code change. A
dedicated back-office section with its own tree was considered and deferred: a content folder
gives versioning, permissions, uSync and pickers for free, and can be promoted later if the tree
gets noisy.

### 4.8 Authoring experience

- The Form interface block picks either a C# resource (as today) or a data source node; the field
  picker then lists the data source's columns with their types.
- The data source editor has a **Test** action that runs the fetch with sample bind values and
  shows the columns and a row, so a mistake is seen before publishing.
- Publishing validates bindings against the data source's columns (`AppBlocksValidator`), the
  presence of key columns when writes are allowed, and the compile of every regular expression
  and predicate.

### 4.9 Paper reports

A paper report is a content node (`paperReport`) with its own Umbraco template: the Razor view is
the report-specific print layout, the role APEX's report layouts and the RTF templates played.
The node holds the data source and its parameters (bound as forms bind them: `:PARAM.id` from the
query string, `:USER_SID` from the signed-in person), the page options (size, orientation,
margins, header and footer templates), the output file name pattern and the culture.

Rendering goes through Pressroom, the shared HTML-to-PDF service (built as a separate product
in the sibling `Pressroom` repository), at the address a site configures in `Pressroom:Url`: a download
route checks Public Access on the node like any page, renders the template to a self-contained
HTML string (inline CSS from the theme's print stylesheet, images and fonts as `data:` URLs),
posts it to Pressroom through its client library and streams the PDF back. When `Pressroom:Url`
is not set, Pressroom cannot be reached, or the site runs on localhost, the route delivers the
self-contained HTML file itself instead of a PDF, so template work needs no service and an outage
degrades to a printable page rather than an error; `?format=html` asks for the HTML explicitly. The template's model
is typed: the data source's rows and columns, the parameters, the person and the culture, so a
report author writes Razor over data and never SQL in a view. Report and form blocks get a Print
action that links to a paper report with the row key.

## 5. Work breakdown

Each package is a brief for one agent; the dependencies say what must merge first. Sizes are
relative to the authentication packages that just landed.

| Package | Scope | Done when | Depends on | Size |
|---|---|---|---|---|
| WP1 Grid and layouts (built 2026-10-23; see [Layouts](application-blocks.md#layouts)) | `split` on Two columns; Full width, Three columns, Region element types; grid data type allowances; partials with neutral markup and class hooks; theme CSS for both shells; saved export updated; docs | A demo page in the saved export renders all layouts correctly in both themes; publish validation passes; existing tests green | none | medium |
| WP2 Content blocks (built 2026-10-23; see [Content blocks](application-blocks.md#content-blocks); the demo is the Content blocks page, since WP3 rebuilds the Orders form) | Rich text, HTML, CSS, JavaScript blocks with the code editor; collected CSS and nonce-aware scripts; field properties `hidden`, `defaultValue`, `width`, `placeholder`; validator and docs | The sample Orders form uses a hidden key field and a default value; raw blocks render in both themes; tests cover Initialize defaults and hidden submission | WP1 for the `width` styling only | medium |
| WP3 Data source engine | `dataSources` root and `sqlDataSource` node types in the export; `SqlDataSource : IFormResource` with schema introspection, bind variables, concurrency, row restriction, policy hook; DML builders for SQLite and SQL Server; registry integration; tests on SQLite (and SQL Server when a LocalDB is present) | The sample Orders form runs on a `sqlDataSource` node instead of `OrdersFormResource`, with the existing forms tests passing against it. **Built 2026-10-23** (`src/UmbracoAppHost/DataSources/`, README "Data sources"): mapping overrides (kind, choices, label, lengths, ranges) remain available; WP4 adds list nodes and forwards page query parameters; the row restriction still refuses `:PARAM` and `:FIELD` | none | large |
| WP4 Reports and lists as nodes (implemented 2026-10-24) | `reportDefinition` and `listOfValues` node types; the node layer in `AppReportDefinitionStore`; report picker and select binding; sample Orders report as a node | The Welcome dashboard and the Orders report resolve from nodes; `appSelect` takes its options from a list node | WP3 | medium |
| WP5 Authoring experience (implemented 2026-10-24) | Form interface picker for data sources; column-based field picker; Test action in the data source editor; publish validation messages; README and `docs/application-blocks.md` | An editor builds the Orders page from scratch in the back office without touching code | WP3, WP4 | medium |
| WP6 Themes (implemented 2026-10-24) | `theme` property on the roots; APEX theme polish (regions, label positions, toolbar); website theme styling of every block; demo pages | Both themes pass a visual check of every block; the 13 site's layouts port onto the twelve-column grid | WP1, WP2 | medium |
| WP7 Other dialects (implemented 2026-10-24; live PostgreSQL/Oracle verification pending) | PostgreSQL and Oracle (12c, 11g) DML builders and tests behind the adapter's dialects | The data source tests run against a PostgreSQL and an Oracle container where available | WP3 | small |
| WP8 Paper reports (implemented 2026-10-24) | `paperReport` document type with a template picker, data source and parameters, page options and file name; typed print view model; self-contained HTML renderer (inlined CSS and assets); download route with Public Access and the HTML file as a download; Pressroom client that delivers the HTML file when Pressroom is unset, unreachable or the site runs on localhost; Print actions on report and form blocks; a sample Orders paper report | The Orders report prints as a Letter PDF from a content node through the service, in both languages | WP3, the rendering service | medium |

Separate and small: move the test projects of both repositories from the deprecated xunit 2
packages to xunit.v3 (test projects become executables; `IAsyncLifetime` and
`BeforeAfterTestAttribute` change shape).

### Delivery notes, 2026-10-24

- WP4 uses published report/list nodes with the existing report compiler and form endpoints.
  Unavailable published report overrides fail closed. List choices are rechecked on writes;
  a stored value removed from a list remains visible and can be retained during an unrelated
  edit. The sample's duplicate static region/status choices were removed by owner decision;
  deliberately configured resource rules still apply.
  Page URL parameters reach embedded and standalone reports, including downloads, separately
  from the report adapter's paging and export options. This includes a small companion client
  URL fix in the sibling `InteractiveReports` repository.
- WP5 tests current draft values, including a new unsaved node, under the same Umbraco
  permissions as editing/creating it. The source and field pickers show schema information.
- WP6 adds explicit root theme selection, third-theme registration, form label positions,
  and website/application galleries. Both shells were checked at desktop and narrow widths.
- WP7 adds PostgreSQL and Oracle metadata, statement builders, returned keys and row locks;
  Oracle 11g can use a `keySequences` mapping. SQL/type tests run without those databases.
  The separate provider test project skips its three live tests unless its documented test
  connections are configured; those live-provider checks remain outstanding here.
- WP8 uses the actual sibling Pressroom client and supports report-specific typed Razor,
  self-contained HTML fallback and Print actions. The published English and French Orders
  reports were rendered by real Pressroom/Chromium and visually checked as one-page Letter
  PDFs. Source credentials remain in configuration.

The solution builds with zero errors and existing dependency warnings. Local verification:
160 application tests passed, with three live PostgreSQL/Oracle tests
skipped because test connections were not configured. Frontend verification passed 29 tests
and 55 smoke checks; the companion report-client URL change passed 37 focused tests. Browser
checks covered both shells at desktop/mobile widths, published pickers, existing and unsaved
SQL previews, and row/page printing. One earlier full run stalled in Umbraco's SQLite
post-publish relation update; the focused test and complete rerun passed.

The saved uSync export carried the new schema and samples at the time; AppBlocks' schemas carry
those definitions now. Existing sites received these changes through an explicit uSync import. The separate xUnit migration and section 6 remain
later work.

### Website authoring follow-on, 2026-10-24

Web Site and Web Page now support a public root and nested APEX application structure.
The saved export includes the Web Page Content and Navigation Menu compositions, native site
and sign-in templates, and a Website authoring sample. Existing installations need an explicit
uSync import; configure native Public Access separately because uSync does not export its rules.

The HTTP integration test passed for inherited templates and navigation, multiple forms and
nested reports, English/French bindings, ancestor CSS/JavaScript ordering, member sign-in and
return navigation, and revoked access. Protected static pages and their login/denied responses
retain `no-store` even on conditional requests. Field pickers resolve their enclosing form,
including a newly added field, and temporary report state stays with its language.

Full solution verification passed 164 application tests, with the three live provider tests
skipped because their database connections were not configured. Initial full runs exposed
SQLite contention between first-boot import and server-election writes. The isolated test
hosts now declare a single-server role, and the reference-login fixture awaits startup completion
before checking readiness. The fresh full run passed; production server-role defaults are unchanged.

Frontend verification passed 32 tests and 55 smoke checks. Browser inspection of these follow-on
changes remains outstanding: the available Windows browser could not be controlled because
the computer-use tool does not support its URL policy enforcement. The earlier WP6 browser
checks above predate this follow-on work.

### Sign-in follow-on, 2026-10-24

Backoffice login now defaults to `Auto`, which first tries the configured identity server.
The development override stays `Debug` by owner decision. Member login also starts the
configured identity server automatically when a page needs sign-in; public pages stay public.
Discovery failures, failed Windows authentication in the embedded server, and returned SSO
errors lead to the native username/password flow without an automatic retry loop. The original
destination, query parameters, and native two-factor checks are retained. Explicit SSO retry
remains available from the local form.

New SSO, directory, and roster accounts receive generated passwords. Administrators set usable
local passwords for fallback access; subsequent SSO and roster synchronization retain them.
The implementation uses Umbraco's account and password handlers. Existing configuration choices,
including explicitly hiding local backoffice login, remain available.

If an external identity server becomes unreachable after the browser has left this application,
the application cannot receive that browser network error. The backoffice provider chooser at
`/umbraco/logout` and the member login page with `?sso-local=1` provide direct local access.

Verification covered all 195 application tests: 194 passed in the full solution run, and the
remaining layout test passed after selecting local login explicitly instead of expecting the
old immediate form response. Real HTTP tests cover password and two-factor fallback, preserved
destinations, failed and successful Windows authentication, and virtual paths. Password tests
cover administrator resets and rollback/retry when initialization fails. The three live database
provider tests remain skipped without configured connections. Frontend checks passed 32 tests
and 55 smoke checks.

### Debug recovery follow-on, 2026-10-24

Debug is both a development convenience and an explicitly enabled emergency-access mode.
For an existing backoffice account, it accepts any nonempty password and bypasses account
lockout and two-factor checks. Activation still requires `Umbraco:CMS:Hosting:Debug=true`
and an effective `BackOfficeLogin` flow of `Debug`; the same settings can be used to recover
a deployed site in the Production environment. Normal login retains its password, lockout
and two-factor checks. The development default stays `Debug`.

The recovery path uses the native backoffice principal, cookie, login notifications and session
lifetime. It records the last-login date without clearing lockout or changing the password,
security stamp or two-factor enrollment. The administrator can repair the account through Users.

Verification passed 40 focused authentication checks across the test run and targeted rerun.
The real HTTP recovery test signs into a locked account with configured two-factor authentication,
completes authorization and token exchange, opens Users and unlocks the account through its native
API. It verifies preserved credentials and enrollment, rejection of unknown/empty credentials,
and restored lockout and two-factor enforcement under Form and normal Production settings.

### Review follow-on, 2026-10-25

The 2026-10-24 commits were reviewed against the owner's sign-in requirements and the work
breakdown. Sign-in corrections:

- An IIS site with Anonymous Authentication only could not run on the default configuration.
  The embedded identity server registers the Negotiate handler, whose
  options throw when IIS reports Windows Authentication disabled; the handler's startup filter
  builds those options as the site starts, so the site did not start, and every request would
  have built them again. `WindowsAuthenticationAvailability` now reads the server's report: the
  throwing post-configuration is skipped, the handler is swapped for one that answers "not
  authenticated", the login screens
  omit the embedded identity server and start nothing
  automatically, `/connect/authorize` refuses with `access_denied`, and the startup log says
  why. An external identity server stays available. `AnonymousHostingTests` covers it with a
  stub of the service IIS registers.
- The configured administrator password (`UmbracoAppHost:AdminPassword`) also removes the
  administrator's two-factor enrolment, as the owner's break-glass rule requires, beside the
  lockout and failed-attempt reset; `AdminPasswordTests` covers it.
- Verified rather than changed: a failed SSO attempt lands on Umbraco's standalone login page,
  which never starts a provider on its own (only the backoffice app's signing-in state does), so
  the fallback cannot loop; members return to their original page through Umbraco's native
  external-login callback; the directory client matches the external server's `/directory` API.

Two independent reviews of WP4 to WP8 and the website authoring follow-on found no high-severity
defect and the saved export internally consistent. Corrections made:

- Published report and list nodes are read and compiled once per published version through the
  data source runtime's definition cache, so a broken definition is logged once rather than on
  every request. Inline-SQL report nodes refuse the `{{RowRestriction}}` marker at publish unless
  a callback handles the alias, and refuse `contextParams` and `sourceName` in their options, the
  latter because it would have applied another report's read groups. Unknown controls and page
  sizes are reported at publish with the platform's own messages, checked against Interactive
  Reports' feature list. Page parameters are now tested on the download route too. The Oracle
  output-parameter naming was confirmed against the decompiled ODP.NET driver.
- Paper reports package assets best-effort: an unreadable asset keeps its reference and is
  logged instead of failing the download; the site's own assets come from disk only; other
  assets are fetched without credentials, cookies or redirects, within ten seconds and eight
  megabytes, and never from loopback or link-local addresses. Rows default to 5000 and may not
  exceed 50000; exceeding the limit still fails the download. Parameters bind as forms bind them.
  Pressroom configuration errors and the client's own rejections fall back to the HTML file.
  Publishing checks the literal culture, the template's view and the print key column, and
  refuses a Print link to a node that is not a paper report. Anonymous downloads answer a
  problem response that says who may download.
- The website footer shows the page's own modified date again; ancestor dates only feed the
  cache validators. Menus and cards skip paper report nodes, an emptied menu no longer stops the
  ancestor lookup, the APEX shell announces the page's culture, and the nested-site title, badge
  text and label position comparisons were corrected. The two shipped views without copies in
  the launcher got them, and the saved template exports no longer declare parents Umbraco does
  not store.
- Documentation now says that the second language is `fr` and what the saved export actually
  contains. Pages behind Public Access now admit a backoffice Administrator without a member
  session, as the form, report and paper-report endpoints do: the base decorates Umbraco's
  public-access check with the same rule, and a member session still decides when both exist.

Left for the owner: the companion client change in the sibling InteractiveReports checkout is
uncommitted there although the build compiles it in; whether paper report assets should be
limited to configured hosts; whether directory lookups should impersonate the signed-in person
(neither server does today); the generated layouts the launcher does not track; and the live
PostgreSQL and Oracle verification of WP7, which the provider tests still skip without connections.

## 6. Later

Show-or-hide conditions and dynamic actions, cascading lists of values, dialog forms, tabular
(multi-row) forms, file uploads bound to a column, server-side computations on save, and
notifications or workflow on row changes.

Tabular forms were built on 2026-10-30 (the Inn plan's U5): the Tabular form block edits the
rows of a child data source that belong to one record, saved as one batch applied whole or not
at all through the engine; see [Tabular forms](../README.md#tabular-forms).

## 7. Decisions recorded

- Raw HTML, CSS and JavaScript blocks are not gated beyond back-office access.
- SQLite first; then the adapter's dialects; no Entity Framework.
- Twelve columns, matching the Umbraco 13 site and the frameworks the themes build on.
- Definitions live in the CMS as nodes under one Data sources root, with appsettings as the
  default and seed; reports are nodes too.
- Paper reports are content nodes with report-specific Razor templates, rendered to PDF by
  Pressroom, the shared rendering service built separately; without it, or on localhost, the
  HTML file is delivered instead.
- Layouts and content blocks (rich text, HTML, CSS, script) stand at the page level of a Block
  Application Page as they do on a website page (2026-10-30), so a page of reports carries its
  own help and script; a page still holds at most one form, wherever it sits, and fields and
  actions still belong to a form.
