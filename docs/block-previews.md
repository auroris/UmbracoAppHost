# Block editor preview coverage

The previews use Umbraco 18.3.0's `blockEditorCustomView` extension, `umb-ref-grid-block`,
`umb-ufm-render`, the supplied edit paths, and `umb-block-grid-areas-container`. Umbraco still
owns the action bar, sorting, resizing, area creation and validation. There is no schema
migration, preview package dependency, application endpoint or authentication change.

Checked against the installed 4Wing development database on 2026-11-04: all **41 distinct
element types offered by its five block-editor data types** have a content preview or native layout view.
The source libraries own one additional portable block (`appFormsContainer`), also covered.
Coverage additionally includes 19 retained legacy 4Wing aliases and four host demo aliases:
**65 aliases in total: 59 custom registrations and six native layouts**. The six pure layouts
already have the necessary native area representation, so no custom view is registered for
them. Settings-only elements are displayed through their parent.

| Owner | Element aliases | Preview |
| --- | --- | --- |
| AppBlocks | `appFullWidth`, `appThreeColumns` | Unmodified native block view and editable areas. |
| AppBlocks | `appColumns` | Native editable areas inside a custom view that reflects split settings `4/8`, `6/6`, `8/4`. |
| AppBlocks | `appRegion`, `appFieldset` | Authored heading/help, collapsible state, native editable area. |
| AppBlocks | `appRichText`, `appHtml` | Current unsaved markup in an isolated document. |
| AppBlocks | `appCss`, `appScript` | Authored source; page scripts do not run. |
| AppBlocks | `appForm`, `appFormInterface`, `appFormsContainer` | Heading, form class/mode, native form areas; nested container contents previewed in layout order. |
| AppBlocks | `appText`, `appTextArea`, `appNumber`, `appDate`, `appTime`, `appDateTime`, `appCheckbox`, `appSelect`, `appShuttle`, `appColor`, `appDisplay` | Disabled controls with authored labels, defaults, placeholders, required/help, hidden and read-only indicators. |
| AppBlocks | `appActions` | Authored action labels on disabled buttons. |
| AppBlocks | `appAttachments` | File selection/drop area and attachment table outline, heading/help and allowed types. |
| AppBlocks | `appTabular` | Configured column names, empty message and action labels. |
| AppBlocks | `appSchedule` | Timeline example, heading/help, window and source names. |
| AppBlocks | `appAntiSpam` | Verification placeholder with service and size; no challenge or network verification. |
| AppBlocks.InteractiveReports | `appReport` | Report heading, table, search/actions, data source and page size. |
| 4WingResources | `fourWingImage`, `fourWingCarouselItem` | Selected image, alternative text and caption. |
| 4WingResources | `fourWingCarousel` | All authored slides, captions and presentation settings. |
| 4WingResources | `fourWingTabs`, `fourWingAccordion`, `fourWingTitledText` | All authored titles and rich text, including nested list ordering and culture. Panels remain visible for authoring. |
| 4WingResources | `fourWingChildSites`, `fourWingPageNavigation`, `fourWingLogoGallery` | Clearly labelled page-context examples; destinations/badges are resolved by the public page. |
| 4WingResources | `fourWingWbgt` | Reading layout with empty values; the live reading is fetched by the public page. |
| 4WingDiagrams | `fourWingDiagram` | Selected diagram image and caption; viewer/editor scripts do not run. |
| 4WingTelephoneDirectory | `fourWingTelephoneDirectory` | Search/table outline and actual view, scope and filter settings. |
| 4WingFileRepository | `fourWingFileRepository` | Search/table outline; no upload/download or data-changing action. |

Legacy 4Wing aliases retained alongside the old Razor views: `blockCSS`,
`blockDeleteManagementCookies`, `blockDiagram`, `blockDropDownList`, `blockHtml`, `blockImage`,
`blockImageCarousel`, `blockJavascript`, `blockListBadgedWebsites`, `blockPageNextPrevious`,
`blockRichText`, `blockTabbedList`, `blockTwoColumn1`, `blockTwoColumn2`, `blockTwoColumn3`,
`blockUnitLogoGallery`, `blockVideoPlayer`, `blockRichTextListItem`, `blockImageCarouselItem`.
These receive the corresponding content, layout or disabled authoring representation.
The three `blockTwoColumn*` aliases keep their native view and configured areas.
They are not currently offered by the installed site's data types.

Host demo aliases: `umbBlockGridDemoHeadlineBlock`, `umbBlockGridDemoRichTextBlock`,
`umbBlockGridDemoImageBlock`, `umbBlockGridDemoTwoColumnLayoutBlock`.
The demo two-column layout keeps its native view and configured areas.

`appColumnsSettings`, `appRegionSettings` and `fourWingMediaPresentation` are settings models,
not selectable content blocks. AppBlocks.Documents supplies a paper-report document type,
not an additional block. ActivityScheduler and AerodromeStatus supply data sources consumed
by the existing schedule/report/form blocks; neither owns another editor block.

## Implementation and limits

Shared code and core registrations live in AppBlocks' `wwwroot/App_Plugins/ApplicationBlocks`.
Each companion/plugin supplies its own manifests and small renderer. There is no wildcard
custom view that could silently take over an unrelated site's blocks.

Umbraco's installed default block view is a labelled card with native areas; no configuration
switch enables Razor previews. Native inline editing is a property-editing mode, not a
rendered preview. The current BlockPreview package's published compatibility covers v17,
not v18, so it was not installed. References:
[v18 manifest API](https://apidocs.umbraco.com/v18/ui-api/interfaces/packages_block_block-custom-view.ManifestBlockEditorCustomView.html),
[official custom-view documentation](https://docs.umbraco.com/umbraco-cms/extend-your-project/backoffice-extensions/extending-overview/extension-types/block-custom-view),
[BlockPreview supported versions](https://github.com/rickbutterfield/BlockPreview#supported-versions).

The implementation uses the native custom-view values rather than executing the frontend
Razor pipeline. Razor currently resolves form classes, visitor identity, record queries and
page context; introducing a server renderer would require a separate authorized preview
contract. Instead, ordinary markup and media are presented directly, using Umbraco's media
repository and existing AppBlocks/host/4Wing stylesheets. The small 4Wing presentation
markup mirrors the relevant Razor hooks; changes to those hooks should update its preview.
This is an authoring preview, not a pixel-identical full-page rendering.

HTML is sanitized with Umbraco's bundled DOMPurify and placed in a sandboxed, inert iframe
without script or form permissions. Its CSP disallows scripts, connections, nested frames
and form submission. Only same-origin/data images and same-origin styles/fonts load.
Authored inline styles are isolated inside the preview. Content referring to external
images, embeds or scripts needs the normal page preview. Standalone CSS/JavaScript blocks
are shown as source and are not applied to other block previews.

Fields show explicitly authored values; resource-derived labels/defaults and live list
choices are not queried. Report, schedule, attachment, repository, directory and WBGT
previews do not fetch business records. Hidden fields are deliberately visible and labelled
in the editor. Form/container nested property editors open through the normal edit link;
their inline preview is read-only, while actual grid areas retain native editing.

## Verification

From `UmbracoAppHost/Frontend`:

```powershell
$env:PREVIEW_TEST_ORIGIN = 'http://localhost:5094'
npm run test:previews
node --test test/templates.mjs test/website.mjs
```

The coverage test scans owning schemas, all configured block references and extension
manifests. Rendering tests cover controls, escaping, nested ordering and culture. The
component test bundles the modules served by the running host's actual import map and
renders them in JSDOM with real Umbraco/Lit/DOMPurify code. Without `PREVIEW_TEST_ORIGIN`,
that integration test explicitly skips. JSDOM does not verify visual layout or pointer drag.

The 4WingWebsite build passed, and preview manifests/modules and the 4Wing stylesheet
returned HTTP 200. Existing template/website tests passed. The unrelated failing broader
application-block suite was not changed.

**Live visual verification remains outstanding.** Computer Use refused the available
Windows browser because URL-policy enforcement is unsupported. It did not open or modify
the editor. Before accepting visual QA, check the following in the actual backoffice:

- Open the page containing Two columns, Rich text, HTML and Tabbed content. Verify content
  is visible, tabs show their bodies and each preview opens its own content editor.
- Edit content and settings without publishing; verify updates, split widths and culture.
- Add/reorder a child within a native area, move it across areas, resize a permitted block,
  and inspect required-field validation and read-only access.
- Open a form with fields, report, schedule, image/carousel and plugin block. Verify controls
  are inert, media resolves and native editing still works.
