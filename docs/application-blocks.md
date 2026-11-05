# Websites, pages and application blocks

uSync imports the **Block Application Page** document type and its block catalogue on first
boot. These pages are available beneath Website, Application and Application Page. The saved
export uses stable keys. Ordinary restarts preserve editor changes; definition updates arrive
through an explicit uSync import.

The saved uSync export includes **Orders interactive** and **Orders form** beneath Sample
Application, and the **Orders** data source beneath the Data sources root. The Orders form runs on
the C# form class `sample.orders`, which every installation with the samples registers: a form
selects a registered class, not a data source. Orders interactive reads the `ORDERS` table of the
`ReportsSampleDb` connection directly, and a report block's source is a table or SQL query rather
than a report name. All of them use the same orders table as the configured report: in the SQLite
sample database, or on SQL Server when `ConnectionStrings:ReportsSampleDb` names one. Its
migration adds the version column the C# resource compares, the audit columns the data source fills, and its
submission receipts without replacing existing rows.

## Web Sites and Web Pages

**Web Site** can be a root or a nested site. It has the page content fields plus a site title,
badge, optional external link, and a template selection. **Web Page** holds the page's own
content and inherits its containing site's template and visual identity. Both compose
**Web Page Content**, which provides page blocks, sidebar content, CSS, JavaScript, and the
generated-navigation options. Existing Website content keeps its `appHostWebsite` alias.

**Navigation Menu** is a separate reusable document-type composition containing the UmbNav
**Top navigation menu** field. Web Site composes it; other document types can opt in later.
A page uses the nearest nonempty ancestor menu or badge. There is no fixed fallback content
ID. Without an authored menu, navigation is generated from the containing site's pages.
Page content itself is not inherited.

Menu entries can speak to the person looking at the page. A label or link address may carry
`{{user.name}}`, `{{user.username}}` and `{{user.email}}` (the signed-in member or backoffice
Administrator, as Umbraco knows them), `{{user.profile}}` (the **Profile page** picked on the
nearest Navigation Menu, for members), `{{user.backoffice}}` (the backoffice, for backoffice
users) and `{{user.signout}}` (the application's sign-out, which both shells render as a form
button because signing out is a POST). An entry whose label or address has a token with no value
for this visitor is left out with its children, so "Signed in as {{user.name}}" and the sign-out
entry appear only to someone signed in, and a profile entry only once a profile page is chosen. A
misspelt token stays visible as typed. UmbNav's own display settings, hide when logged in and
hide when logged out, are enabled on the menu data type and apply as well, to backoffice
Administrators included. The **Website theme demo** menu carries the three entries as an example.

A typical application tree is:

```text
Web Site — Website home template (public site)
  Overview / support / documentation — Web Pages
  Sign in — Web Page with Site sign in template
  Access denied — Web Page
  Web Site — Application site template (APEX)
    Application pages, forms and reports
```

Choose **Website home** for the website shell or **Application site** for APEX in the Web Site's
normal Umbraco template selector. Ordinary Web Pages keep their **Web page** placeholder
template: routing replaces it with the nearest Web Site's selected template. A nested site
starts its own template scope. An explicitly selected page template, such as **Site sign in**,
or an alternate template remains selected. A future splash page can sit above these sites
without changing this resolution.

Use Umbraco **Public Access** on the nested application Web Site to choose the allowed member
groups, login page and access-denied page. Its descendants inherit that protection; the public
overview remains accessible. Public Access admits, besides the members it names, a backoffice user
who may edit the page: Umbraco's **Update** permission on it, through their user groups and within
their start nodes. That is this host's own rule, `EditorPageAdmission`, registered with AppBlocks
as an `IProtectedPageAdmission`; AppBlocks by itself admits nobody Umbraco refuses. Any other
backoffice user gets its login page, and the form, report and
paper-report endpoints answer the same way, so such a user signs in there with a member account
the rule names. When both sessions exist, the member session decides: a member Umbraco refuses
is never replaced by the backoffice account, however much it may edit, so the editor signs the
member out first. Block endpoints evaluate member access first as well, so viewing the
page does not grant access to every block's data or operations.
The shell choice does not grant or remove access. Public Access rules must be configured for
each installation: the installed uSync version does not export them. Keep the login and
access-denied pages outside the protected branch.

The website block grid supports content and layouts directly, including nested regions and
layouts, multiple independent forms, and reports within layouts. Fields and form actions need
a form context. Public text and embedded HTML/JavaScript render without signing in. Without Public
Access, a C# form class decides for an anonymous visitor, so one with no check of its own is public.
A class that refuses, report blocks, the data source engine, tabular forms and schedules show a
sign-in notice in place of the block. This host's report policy requires an account for every report.
The application templates show a sign-in form instead of their blocks, but that is presentation
only: a form's endpoints follow the same rule, so use Public Access to protect a branch.
Language-specific page bindings are retained by form and report API calls.

CSS and JavaScript accumulate from the outermost ancestor down to the page, followed by block
assets. They use the shared nonce-aware asset collector. An ancestor edit invalidates inherited
page cache validators. Pages containing interactive data blocks and responses protected by
Public Access use `no-store`, including login and access-denied responses at a protected URL.

The saved **Website authoring** page beneath **Website theme demo** demonstrates a public
overview/support page, inherited navigation, two-column content, and a guide filter implemented
with the page's CSS and JavaScript fields.

## Authoring a Block Application Page

1. Create a Block Application Page.
2. Add an **Interactive Report**, or a **Form**. A page can contain several reports and one form.
3. For a report, select its configured or published source. Set the heading, initial page size, visible
   controls, and empty message as needed. Report administrators can use **Configure view**,
   make changes in the native report UI, and choose **Use this view**. Ordinary editors can
   retain, clear, or supply a report-state document. Publication validates its structure;
   the report engine validates its expressions and columns when it executes.
4. For a form, add its **Form Interface** using Umbraco’s native Single Block editor, select a registered C# form class, and choose its
   record-key query parameter and return page. Add field blocks to the fields area. Field choices
   come from the selected class. Arrange them with the layout blocks and fieldsets described under
   [Layouts](#layouts), with an optional Form actions block for button labels, and add rich
   text, HTML, CSS and JavaScript beside them as described under [Content blocks](#content-blocks).
5. To connect a report to a form, select the form page and specify the report's record-key
   column, such as `ORDER_ID`. Use the same query parameter as the Form Interface, normally
   `id`. The report's create link opens the page without a key; its edit link opens
   `orders-form?id=4`.
6. Publish, then use Umbraco **Public Access** on the page or its ancestor to select the
   member groups that can enter it. A protected page also admits a backoffice user who may edit
   it, unless a member session is present, in which case the member's own access decides. Form,
   report and paper-report endpoints answer the same way, then apply their own data
   permissions. On an open page, a signed-in backoffice user reaches the blocks without a
   member account, and whatever is behind each block decides.

Member sign-in first tries the configured identity server. If SSO fails, it returns to
Umbraco's username/password form without retrying automatically. The form is also available
directly with `?sso-local=1`, and appears immediately when no member identity server is enabled.
New SSO accounts have generated passwords; administrators can set usable local passwords in
**Members** for fallback access. Backoffice users can instead sign in at `/umbraco` and return to
the application's pages that Public Access leaves open, and to the protected pages they may edit;
they need no member account there. Any other page Public Access protects needs a member account it
names. The sample's Orders class looks for **Administrators** in particular, so other backoffice
groups, including Interactive Report Administrators, do not get it.

The account menu at the top right identifies the active account as **Member account** or
**Backoffice account**, and includes **Sign out**. When both sessions exist, the menu shows the
member account, and its access to a protected page decides, not the backoffice editor's permission. Sign out ends both application sessions so
the next request cannot silently switch to the backoffice account.

The available fields are text box, text area, number, date, time, date and time, checkbox,
select, shuttle, color picker, and display value. Shuttle stores ordered choices as one CSV
string; Color picker stores a six-digit hex colour. See AppBlocks' [Shuttle](../../AppBlocks/docs/shuttle.md)
and [Color picker](../../AppBlocks/docs/color-picker.md) guides. The [Anti-spam](../../AppBlocks/docs/anti-spam.md)
block is also available inside forms. Labels and help text can be overridden on a page. Resource
validation is always enforced; a block can additionally require a value or make a field
read only, and hide it, give it a default value, a width and a placeholder (see
[Field properties](#field-properties)). Dates are date-only ISO values, times are local times without a zone, and date/time
inputs use the browser's local zone and submit an ISO timestamp. Numeric input is sent as an
invariant decimal string to preserve precision.

Control names are `search`, `columns`, `rename`, `columnSettings`, `filter`, `sort`,
`pagination`, `controlBreak`, `highlight`, `aggregate`, `compute`, `groupBy`, `pivot`,
`chart`, `savedReports`, and `download`. Blank block controls inherit the source's choices.

## Layouts

The nearest site's optional **Theme** override chooses `application`, `website`, or a registered
custom theme. Leaving it empty uses the selected site template (or the existing document-type
default for legacy application roots). Both themes style the
same block markup; the saved **Website theme demo** gallery shows layouts and content in the
website theme. Forms can put labels above their fields or to the left with **Label position**.
The left arrangement stacks on narrow screens.

A host can register another theme without introducing a root document type:

```csharp
builder.Services.AddSingleton(new UmbracoAppHost.Themes.SiteTheme(
    "custom", "CustomMaster.cshtml", "CustomSignIn.cshtml"));
```

The layouts supply the theme's CSS and use the shared `ah-grid`, `ah-region` and `ah-field`
hooks. Set the root's Theme to the registered alias.

Block pages sit on a twelve-column grid, as Bootstrap and APEX do.
Four blocks arrange the content of a form:

| Block | Areas | Notes |
|---|---|---|
| **Full width** | One, 12 columns | A row across the form |
| **Two columns** | Two, split 6/6, 4/8 or 8/4 | Choose the split in the block's **Settings**. Without a choice the columns split 6/6, so pages saved before the setting existed are unchanged |
| **Three columns** | Three, 4 + 4 + 4 | |
| **Region** | One, 12 columns, under an optional title bar | APEX's region. **Collapsible** in its settings lets visitors fold it from the title bar; it starts open and needs a title |

The block catalogue has two groups. **Layouts** holds Full width, Two columns and Three
columns; **Content** holds Region, Fieldset, Form actions and the field blocks. A form's
fields area takes both groups. Every area of a column layout, a region or a fieldset takes
Content only, so column layouts never nest and both themes can style them without guessing at
depth: put regions and fieldsets in the columns, not columns in the regions. These rules are
the area allowances of the **Application: Application blocks** data type; the backoffice editor
enforces them (Umbraco does not check them again on the server). A new content block joins the
**Content** group to become available in every layout. Layout blocks stand at the page level
too, beside or instead of a form, with their areas holding reports, content blocks, attachments
and tabular forms that name their owner; a page still holds at most one form, wherever it sits.

A block's span inside an area counts that area's columns, as the backoffice draws it: a block
that fills a Three columns area spans 4, one that fills a Two columns area spans 6. The split
widens or narrows the two areas on the page, while the backoffice always draws them 6 + 6, and
the blocks inside keep their proportions. Content blocks offer spans of 12, 8, 6 and 4, so two
or three of them can also share a row of a fieldset or region.

The partials in `src/UmbracoAppHost/Views/Partials/ApplicationBlocks/` (`Items.cshtml` for every
grid of blocks, `Layout.cshtml`, `Region.cshtml`) emit neutral markup: Umbraco's own grid classes
and custom properties, which Umbraco's `umbraco-blockgridlayout.css` turns into columns, plus
stable hooks that the themes style. Neither carries theme-specific markup.

| Hook | Element |
|---|---|
| `ah-grid`, `ah-grid__item` | A grid of blocks: the page, and the inside of every area; `data-grid-columns` gives its width |
| `ah-layout`, with `ah-layout--full-width`, `ah-layout--two-columns` or `ah-layout--three-columns`, and `ah-layout--split-4-8`, `-6-6` or `-8-4` | A column layout |
| `ah-area`, `ah-area--left`, `ah-area--middle`, `ah-area--right`, `ah-area--content` | An area of a column layout; `data-area-col-span` gives its span |
| `ah-region`, `ah-region--collapsible`, `ah-region--untitled`, `ah-region__header`, `ah-region__title`, `ah-region__body` | A region. A collapsible one is a `details` element whose `summary` is the title bar, so it works without script |

Each shell's bundle carries Umbraco's grid stylesheet and its own theme of the hooks:

| Shell | Stylesheet | Gutters | Columns stack | Regions |
|---|---|---|---|---|
| Application | `Frontend/src/app/scss/_layouts.scss` | 1rem | At 48rem and below, where the side menu becomes an overlay | Cards with a title bar, in the manner of the Universal Theme |
| Website | `Frontend/src/website/scss/_blocks.scss` | 1.5rem | Below 48rem (768px), where the menu also folds | Cards with a grey title bar |

Twelve grid tracks have eleven gaps whatever the blocks span, so both themes cap the column gap
at 3% of a grid's width; a fieldset in a narrow column then never overflows. Region titles take
the next heading level of the page (`h3` inside a form with a heading), and the region's own
content continues below it.

The saved export's **Layouts** page beneath Sample Application shows every layout: a read-only
form over the sample orders with each layout, regions and fieldsets as placeholders. Add
`?id=4` to its address to fill the display values with an order. To see the layouts in the
website shell, place a Block Application Page with the same blocks beneath a Website root.

## Content blocks

Four blocks carry content rather than data. They are in the catalogue's **Content** group, so
every area that takes fields takes them too, and like the layouts they stand at the page level
as well, so a page of reports can carry its own help text, links and script. The fifth block of
the group, [Attachments](#attachments), carries a record's files.

| Block | Editor | Renders |
|---|---|---|
| **Rich text** (`appRichText`) | Umbraco's Tiptap editor, **Application: Rich text** | Where it sits, inside `ah-rich-text`, in the theme's typography. Inside a titled form or region, start headings one level below its title: `h3` in a form, `h4` in a region |
| **HTML** (`appHtml`) | Code editor, **Application: HTML** | Exactly as written, where it sits. It is inside the form, so it cannot contain a form of its own, and a button in it needs `type="button"` or it submits the form |
| **CSS** (`appCss`) | Code editor, **Application: CSS** | Once in the page's `<head>`, after the theme's stylesheets and the form runtime's, however many blocks carry the same code. It applies to the whole page |
| **JavaScript** (`appScript`) | Code editor, **Application: JavaScript** | Once at the end of the page's `<body>`, after the shell's own scripts. It is a classic script, so it runs before the form runtime has loaded the record |

CSS and JavaScript blocks take no cell of the grid wherever they are placed. Their code is
compared without the whitespace around it, so the same stylesheet in two regions is written
once, where its first block appears in the page's order. The code is written as the editor
wrote it; the only change is that a `</style>` or `</script>` inside it is escaped, so a string
that contains one cannot end the element early.

**There is no publish-time check of the code.** Back-office access is the trust boundary: anyone
who can edit a block page can put HTML, CSS and JavaScript on it, and that script runs with
the session of whoever opens the page. Give the Content section only to people you would let
change the application.

**Content Security Policy.** The library sends no policy. A site that sends one with a nonce
stores the request's nonce in `HttpContext.Items["CspNonce"]` (`AppPageAssets.CspNonceItem`)
from its own middleware; the blocks' styles and scripts, and the form runtime's stylesheet and
module, then carry `nonce="…"`. Without one they carry none.

A page renders its body before its layout, so the blocks collect their code while the page
renders, and a tag helper component (`AppPageAssetsTagHelperComponent`) appends it to the
layout's `<head>` and `<body>`. Any layout gets it without a line in its template: both shells,
a host's own copy of their views, and a later theme. The form runtime's stylesheet goes into
the head the same way, ahead of the CSS blocks, so a page's own rules win a tie with it.

### Field properties

Every field block (text box, text area, number, date, time, date and time, checkbox, select
and display value) has, besides its binding, label, help, **Required on this form** and **Read
only on this form**:

| Property | Effect |
|---|---|
| **Placeholder** | Shown in the empty field. A select shows it as its empty choice instead of "Select a value". It is a hint, never a substitute for the label |
| **Hidden** | A hidden input without a label or a grid cell, bound and submitted like any other field: for keys and foreign keys. A hidden field that is also read only carries the record's value but is never written. The browser still sends what the person could change, so a value that must be trusted belongs to the resource, not to a hidden field |
| **Default value** | Fills the field when a new record leaves it empty: text, or one bind variable from the table below. The resource's own starting values win, and a saved record is always shown as stored |
| **Width** | The columns of the twelve-column grid the field spans, from 1 to 12. It replaces the span the block has in the grid, which the backoffice keeps drawing, and is capped at the grid the field sits in: straight inside a Two columns area, whose grid is six wide, 6 and 12 both fill the area. Empty keeps the block's own span |

A default is either text, used as written, or one of these bind variables, whole and in any
case:

| Variable | Value |
|---|---|
| `:USER_SID` | The Windows SID the account is linked to, its external login key. An account with only a password has none |
| `:USER_NAME` | The member's or backoffice user's username |
| `:USER_KEY` | The application's key for the account: the member id, or `backoffice:` and the user id |
| `:USER_EMAIL` | The account's email address |
| `:APP.name` | A value supplied by the application's registered `IBindVariableProvider` for this page and block |
| `:NOW` | The web server's date and time, written as the field's kind is: `yyyy-MM-dd` for a date, `HH:mm` for a time, an ISO date and time with its offset for a date and time (shown in the browser's zone), `yyyy-MM-dd HH:mm` for text. A number or a checkbox has no "now" |
| `:PARAM.name` | The `name` parameter of the page's address, such as `note` in `?note=Rush` |

The server resolves defaults when it renders the page, for the person page access admitted
and the address requested, and writes each on its control as `data-default`; the runtime
applies them to the fields a new record leaves empty. A variable without a value, such as the
SID of a password account or a parameter the address does not carry, leaves the field as the
resource started it. Anything else is used as written, a misspelt variable included, so a
mistake shows in the field rather than vanishing. The names are the ones the data sources use
for their bind variables.

A field with a width puts `ah-grid__item--width-N` on its grid item beside the span; a hidden
field is `<input type="hidden" data-field="…">` straight in the grid. Each shell's stylesheet
(`Frontend/src/app/scss/_content.scss`, `Frontend/src/scss/components/_content.scss`) styles
`ah-rich-text` and gives placeholders the theme's muted text, which keeps a contrast of 4.5:1.

The saved export's **Content blocks** page beneath Sample Application shows every block and
field property: rich text, an HTML callout styled by a CSS block in each of two regions (the
head gets the stylesheet once) with a JavaScript clock, and a new-order form whose Customer
defaults to `:USER_NAME`, whose Notes default to `:PARAM.note` (open it with `?note=Rush`),
whose Status is hidden but saved, and whose fields fall into two rows by width. It saves real
orders to the sample database.

## Attachments

The **Attachments** block (`appAttachments`, in the **Content** group) keeps files with a record:
an invoice with an order, a payment instruction with a block of rooms. The files are blobs in the
attachments database (see [Record attachments](../README.md#record-attachments)), never Umbraco
media, so they are read only by whoever may read the record.

Place the block inside a form, in its fields area or in a region or fieldset there, and it shows
the files of the record the form is editing. A new record has none to show and says so until it
is saved; the block then loads its list with the saved key and offers the upload controls when
the person may write. Each upload takes a file, one of the block's **Purposes** when it lists any,
a label and notes. A file's name downloads it; **Replace** keeps the old file as an earlier
revision, listed under the item and still downloadable; **Remove** takes the item out of the list
and leaves its rows for an administrator's purge.

Placed on its own, on a Web Page, the block names the record instead. **Owner resource** selects
the resource or data source, and **Owner key** says where the key comes from: `:PARAM.id` reads
the page's address, `:FIELD.blockId` reads a field of the record the enclosing form loaded (so a
stay page can show the files of the stay's block, read only), and any other text is the key
itself. The block without an owner resource must sit in a form, and `:FIELD` needs one too;
publishing says so, as it refuses a **Largest file** above the site's `Attachments:MaxFileSize`
and an **Accepted types** entry outside the site's `Attachments:AllowedExtensions` when the site
lists any.

What the person may do is decided on the server for every call: the page's access, then whether
the owning resource loads the record for them, then whether it lets them write (a data source's
write groups; a C# resource may implement `IAttachmentOwner`), then the block's **Read only**. The
list carries the antiforgery token the writes need, as a form's load does.

The saved export's **Orders form** page carries an Attachments block with the purposes Invoice,
Delivery note and Other; the **Website content blocks** page shows the same block in the website
shell. Open an order with `?id=4`, upload a file, replace it and remove it to see every state.

## Tabular forms

The **Tabular form** block (`appTabular`, in the **Content** group) edits the rows that belong to a
record several at a time: the lines of an order, the rooms of a block booking. The rows live in
their own table, read and written through a published data source (see
[Tabular forms](../README.md#tabular-forms)), and are saved together: every change of one save is
written, or none is.

1. Publish a data source over the child table, such as `ORDER_LINES`, with its key, **Checksum**
   concurrency so a stale save is refused, its audit columns and the insert defaults a new row needs. Its read and write groups,
   allow flags, row restriction and policies govern the rows the block shows and writes.
2. Place the block inside the form, in its fields area or in a region or fieldset there, usually
   after the fields, so it follows the record the form is editing. On its own, on a Web Page or at
   an application page's top level, it names the record with **Owner resource** and **Owner key**,
   as an Attachments block does.
3. Select the **Data source** and name its **Link field**, the field that holds the record's key,
   such as `orderId`. The server sets it on every new row; it is never a column.
4. List the **Columns** to show, such as `item, quantity, unitPrice, notes`, or leave it empty for
   every field but the link field. Every required field the columns leave out needs a default,
   unless the block is **Read only** or **Without adding**.

A new record has no rows to show and says so until the form saves it; the block then loads the
rows with the saved key. **Add row** adds an editable row with the data source's defaults,
**Delete** marks a row and **Undo** clears the mark, and **Save rows** sends every new, changed and
marked row at once. When the data source refuses one of them, the block names the row and the
column, marks them and keeps everything entered, and nothing of that save is written; a row that
someone else changed in the meantime offers to reload the rows. **Row limit** caps the rows listed
for one record (200 unless set, at most 1000), and the block says so rather than show part of a
larger set.

Publishing refuses a C# resource or an unpublished data source, a link field the data source does
not write (a key the database assigns, a read-only field), a column that is not one of its fields
or is the link field, a row limit outside 1 to 1000, a required field a new row could not fill,
and the owner settings an Attachments block would be refused for.

The saved export's **Orders form** page carries an **Order lines** block over the **Orders lines**
data source (`orders.lines`), with the columns Item, Quantity, Unit price and Notes; the **Website
content blocks** page shows the same block in the website shell. To verify it, open an order with
`?id=4`, change a quantity, add a line, delete another and save; then enter a quantity of 0 and
save to see the refusal name the row and keep the values, and open `?id=5` to see that order's
own lines.

## Schedules

The **Schedule** block (`appSchedule`, in the **Content** group) shows resources against time: a
tape chart of rooms with the stays and repairs in them, a calendar of activities, vehicles and
their allocations. It can stand at the top of an Application Page, as a form or a report does,
or sit in a content area of a Web Page; both shells style it.

Give it a **Rows source** and a **Bars source**: each is the alias of a published report
definition that selects a data source, or of a data source. The rows need the columns `key`,
`label` and `group`; the bars need `key`, `row`, `start`, `end`, `label`, `kind` and `link`, in
any letter case, by column name or by a field alias of the data source's mapping. A bar's `row`
is the key of the row it belongs to; `start` and `end` are dates or date-times, and `end` is the
last day or hour the bar covers; an empty `end` means the bar has not ended; `kind` colours the
bar and names it in the legend; `link` is where the bar leads, or empty to use the **Link
template**, such as `/stays/?id={key}`, where `{key}` and any other bar column in braces are
replaced. Publishing refuses a source that is not published, cannot read its schema or lacks a
column, and says which.

The window is the page's `from` and `to` parameters, or **Days before today** and **Days after
today** when the address has none; the Earlier, Today and Later links move it. Both sources
receive the dates shown as `:PARAM.from` and `:PARAM.to`, so a bars query can select only what
touches the window. **Columns** shows a column per day or per hour. Overlapping bars of one row
take their own sub-rows; a bar without an end runs to the edge of the window with an open
marker; a bar that crosses an edge is cut there and marked. Today's column and the weekends are
marked, and every bar is a link the keyboard reaches, with its details read to a screen reader
and shown as a tooltip.

What a person sees is decided on the server for every request: the page's access first, then
each source's read groups, row restriction and policies through the data source engine. A
source the person may not read, or that cannot be read, shows a short message in the block's
place.

The saved export's **Orders schedule** page under the Sample Application, and **Website orders
schedule** under the Website theme demo, show the customers of the first 48 sample orders by region, each order a bar around
today, pending orders open-ended, with a link to the order form. Their bars source reads the view
`ORDERS_SCHEDULE_BARS`, which the sample's schema scripts define for each database because date
arithmetic differs between them. Open the page with `?from=2026-06-01&to=2026-06-30` to see an empty
window, and follow a bar to its order.

## Adding a data source

A data source gives reports, lists, tabular forms and schedules a table or a SELECT. In **Content**, create a **SQL data
source** beneath **Data sources** (create that root first on a site that has none), fill in its
alias, connection, row source and key columns, choose its required **Access** value and allowed operations, and publish. Publishing
reads the schema through the connection and refuses the node with every reason at once when
something does not fit: an unregistered connection, a missing table or column, a key that is not
the table's primary or unique key, an unknown concurrency mode, an unknown bind
variable, or a custom statement that does not compile. Form Interface selects a registered C#
form class; a data source is selected by the report, list, tabular form or schedule that reads it.

**Public** allows anonymous reads where page access permits them. **Not public** requires a
Member or backoffice User. **Specific groups** requires at least one read group. Leaving Access
empty is an error even when groups are selected. The saved Orders sources use **Not public**;
anonymous visitors cannot write to any SQL data source. Queries and restrictions with `:USER_*`
binds require sign-in even for a Public source.

The [README's Data sources section](../README.md#data-sources) describes every property, the
bind variables, the field rules, the dialects and the safety model. In short:

- Every column becomes a field named after it (`ORDER_DATE` becomes `orderDate`); the mapping
  renames fields, gives SQLite text columns their date kind, adds choices for a select, and sets
  labels, lengths and ranges.
- The row restriction, such as `OWNER_SID = :USER_SID`, applies to every fetch and write, and a
  write that would leave it is refused. Read and write groups narrow the page's own access.
- Insert defaults give a new record its starting values (`ORDER_DATE = :NOW`) and fill what the
  form does not send; audit columns record who changed a row and when.
- Rules a predicate cannot express belong in an `IDataSourcePolicy` registered in C#, which every
  operation asks with the current and proposed values, and with the engine's open connection and
  write transaction so it can read other rows in the same snapshot.

## Testing and publishing a data source

Reports and lists of values are published content under **Data sources** too. Create a
**Report definition** to select a SQL data source or enter a connection and SQL. A report node
with the same alias as an appsettings report overrides that seed, and appears in the existing
report picker. Its **Options** JSON accepts the report options already used in configuration,
including column labels, help and links.

Publishing a report definition checks it as a report block is checked: **Visible controls** takes
the control names listed above, and the default page size cannot be larger than the maximum. The
options cannot name a connection string, a provider or `contextParams`; a data source's row
restriction is what binds the signed-in person. Inline SQL can use `{{RowRestriction}}` only when
a row-access callback in code decides that alias: the Welcome report's, or a site's callback for
a configured report the node overrides. Otherwise select a data source. Each published version of
a definition, and of the data source it selects, is read and checked once rather than on every
request, and a definition that does not read is logged once per version.

Create a **List of values**, choose its source and the display/stored-value columns, and select
it on a select field's **List of values** property. The field can use a text or numeric binding.
Without a list selection it retains the resource's static choices. The list's row limit is
configurable on the node. Each save checks new or changed choices against the current list.
An unchanged stored value can remain after its option is retired. The supplied Orders form uses
the region and status lists; deliberately configured resource rules still apply.

The SQL data source editor's **Test** action reads the current draft, including unsaved edits.
Enter sample bind values as JSON, for example
`{"PARAM.id":"4","USER_SID":"S-1-5-21-123"}`, then choose **Test data source**.
The result shows field aliases, database columns, kinds, required/read-only rules and one
matching row. Unspecified user variables use the current backoffice account; a sample
`USER_GROUPS` is an array of names. The test does not save, publish or execute the custom
write statements. It uses the node's existing edit permission, or the parent's create
permission for a new node.

Publish the data source when its columns and sample row are right. The Form interface picker
labels data sources separately from C# resources and reports schema problems. The field picker
shows the column behind each binding. Publishing a page checks those bindings again.

To build the sample Orders form from scratch, select `orders` in its Form interface, add text
for `customer`, select fields for `region` and `status`, number for `amount`, date for
`orderDate`, and a text area for `notes`, then add Form actions. Select the published region
and status lists on the select blocks. Keys, version and audit values already have server
defaults or read-only descriptors and need no editable field.

## Adding a resource

A C# resource is still the place for business operations that are more than one row's insert,
update and delete. Register a scoped implementation in `src/UmbracoAppHost.Web/Program.cs` before building Umbraco.
It ships in the common release; database pages determine where it is used:

```csharp
builder.Services.AddScoped<IFormResource, OrdersFormResource>();
```

It uses the `ReportsSampleDb` connection, which `AddSampleOrders` registers from the connections
callback of `AddUmbracoAppHost`; a site with the samples on has both.

`IFormResource` declares a stable alias, contract version, field descriptors, initialization,
record load, create, update, and delete. `FormResource<TCreate,TUpdate>` provides strict JSON
binding and data-annotation validation for typed commands. The complete working example is
`src/UmbracoAppHost/Sample/OrdersFormResource.cs`.

Every resource operation must independently authorize the caller and record. Returned
capabilities control the displayed buttons; they are not authorization. Resources own their
transactions, row restrictions, concurrency checks, and business rules. The sample explicitly
allows authenticated website members and current backoffice Administrators to modify sample
orders. Replace that policy for real application data. The resource checks administrator
membership independently for every operation; other backoffice identities cannot perform
sample CRUD.

Loads return a key, version token, values, and allowed operations. Updates and deletes must
compare the submitted version atomically when writing. The sample uses `WHERE VERSION =
@version` and increments the version on update. A stale submission returns HTTP 409.
`SubmissionKey` is available for resource-specific idempotency; the sample records successful
creates in the same transaction and returns the original result on a retry. The request's query
parameters reach a resource as `FormExecutionContext.Parameters`, which a data source binds as
`:PARAM.name`; they are browser input, never authorization.

Only fields bound by the published form can be submitted. Unknown, duplicate, display-only,
and read-only values are rejected. Omitted update fields retain their current values;
explicit null is a deliberate clear and is subject to validation. The response includes only
fields rendered by this form. Antiforgery tokens are required for every mutation. Republishing
the page or changing the resource contract invalidates an already loaded form.

## Report instances and access

Each published report block has a family name derived from its page and block keys. It gets
its own saved-view family and published starting view while inheriting the configured source's
SQL, registered connection, limits, and source policy. Copying a page produces a different
family. Moving a form page changes the links through the content picker automatically.

Direct schema, query, saved-view, lookup, and export requests resolve the published instance
and recheck the page's Public Access, which only a member it names passes. Draft content is excluded, and unpublishing removes
the instance from execution. A published block owns its default view; selecting a saved view
as that family's global default is denied. Personal and shared saved views retain their
ordinary C# authorization rules.

**Source aliases remain separately accessible under their own C# policy.** Page permissions
on a block do not restrict an existing standalone `/api/reports/orders` source. A source that
must only run through approved instances needs a C# authorization callback that denies direct
source execution. Row-restriction and action callbacks can use the new `SourceReportName`
alongside the instance's `ReportName`; update any callback that previously matched only
`orders`. Starting-view filters are user-editable report state, never row authorization.

The adapter uses the generic `ReportDefinitionCompiler` to compile detached dynamic
definitions. Interactive Reports has no reference to Umbraco. Its additional title, controls,
initial-state, column-presentation, and link configuration APIs work in any HTML host; see
the sibling project's `docs/EMBEDDING.md` and Workbench `embedding.html`.

Report state is retained in the current browser tab when following the report's create/edit
links, so returning to it restores the working view and requests fresh data. It is separated
by member and report instance. Form values are not placed in browser storage.

## Printing

Create a **Paper report** beneath the application or website root, choose its SQL data source
and Razor template, and set its culture and page options. The saved English and French Orders
paper reports use a Letter layout. The print view receives `PaperReportModel`, so its Razor
uses rows and columns without opening a database. Publishing checks that the template's view
exists and that a literal **Culture** is a real culture; a bind variable such as
`:PARAM.culture` is resolved on each download.

**Maximum rows** bounds the result, because every row is held in memory and rendered: empty
means 5000, and publishing refuses more than 50000. A query that returns more rows than the
limit fails the download with a 422 that says so, rather than printing part of the result.

Select the node in a report or Form block's **Paper report** property; publishing refuses a
node that is not a paper report. Forms print the loaded row; report row actions use the
report's record-key column, which publishing checks against the columns of a report with a
data source, and its key parameter. The report toolbar can print the full paper report. Query
parameters reach the paper report as they reach forms: at most 50 names, one value each, without
`appCulture` or `format`.

A paper report always renders for a person, so every download needs someone signed in, even when
the node is public: a member session with Public Access to the paper node, or on a node it leaves
open a signed-in backoffice user. Anyone else receives a 403 problem response that asks them to sign in. The data
source's read rules are checked for every download too.

Configure `Pressroom:Url` to produce PDFs. Localhost, `?format=html`, an unset or unusable
service address, or a service failure downloads a self-contained HTML file instead, ready to
print from a browser. Packaging is best effort: an image, stylesheet or font that cannot be read
keeps its original reference. The site's own assets are read from disk; other http(s) assets
are fetched without credentials or redirects, for at most 10 seconds and 8 MB each, and never
from loopback or link-local addresses. See [Paper reports](../README.md#paper-reports) for
parameters, options and template configuration.

## Verification and current scope

Run `dotnet build UmbracoAppHost.slnx` and `dotnet test --project tests/UmbracoAppHost.Tests/UmbracoAppHost.Tests.csproj`.
For the form's browser behavior, run `npm ci` and `npm run test:blocks` in `Frontend`; its
tests also cover the Attachments and Tabular form blocks' runtime, which follows the form's
`app-form:loaded` and `app-form:saved` events. `TabularBlockTests` and `TabularHttpTests` cover the
Tabular form's publishing rules and its batches against the sample order lines. The host-booting tests run on `tests/UmbracoAppHost.Testing`, the
library a downstream project's tests use too (see [Test a host](getting-started.md#test-a-host)).
The integration test installs Umbraco into a separate temporary database, publishes real
block pages, renders a form, exercises its endpoints, and checks member permissions,
antiforgery, concurrency, and unpublishing. It does not require the reference instance's
database. The data source tests run the engine against SQLite files (every operation, the
bind variables, the restriction, policies and publish validation), publish a data source and a
page through Umbraco and drive them through `/api/forms` as a member, check the SQL Server
statement text, and run the engine against SQL Server Express LocalDB where it is installed
(skipped with the reason elsewhere). Press Play in Visual Studio for the configured reference installation, or
create a site with `new-site.ps1`. See [deployment.md](deployment.md).

Browser verification also covers the backoffice resource/field pickers and the native report
preview/capture workflow. The sibling Interactive Reports test suites cover its generic
component configuration, .NET endpoints, and browser demo server independently of Umbraco.

Report sources can come from appsettings or published nodes; select choices can come from
resources or published lists. Data sources have builders for SQLite, SQL Server, PostgreSQL
and Oracle, and an editor Test action. The Block Application Page still uses invariant content,
one form per page and page navigation; Web Pages vary by culture and can hold several
independent forms. Culture variants for Block Application Pages, dialog forms, cascading choices,
and row-specific report action visibility remain later work (see the
[design's work breakdown](design-blocks-and-data-sources.md#5-work-breakdown)).

Optional PostgreSQL/Oracle provider tests live in `tests/UmbracoAppHost.ProviderTests`.
That project references `Npgsql` and `Oracle.ManagedDataAccess.Core` explicitly, as a host
using those databases must now do; AppBlocks leaves optional drivers to its consumer.
Set `UMBRACOAPPHOST_TEST_POSTGRES`, `UMBRACOAPPHOST_TEST_ORACLE` or
`UMBRACOAPPHOST_TEST_ORACLE11G` to a test connection string and run that project. Each test
creates and removes its own uniquely named table (and an Oracle 11g sequence); it does not
create or remove the database. Missing test connections are reported as skipped.
