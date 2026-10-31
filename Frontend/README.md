# Umbraco 18 frontend

This is the frontend source for UmbracoAppHost's two themes on Umbraco 18: the `website` theme,
a small public site in the spirit of a starter site, and the `application` shell. One build
supplies every UmbracoAppHost installation. A site's branded theme is a Razor class library of
its own with its own frontend, registered as described in
[Design](../docs/design-blocks-and-data-sources.md#1-themes). It can import `src/scss/components/forms`
and `src/scss/site/blockgridlayout` from here, the block styles every theme shares.

## Build and develop

Use Node 20 or newer. From this folder:

```powershell
npm ci
npm run build
npm run templates:emit
npm run check
npm test
```

`UmbracoAppHost` builds and packages `dist/` and embeds the generated Razor views automatically
when Visual Studio builds the library. There is no per-instance frontend copy step.
The .NET build runs the asset build and template generation whenever their sources change;
run `npm run check` and `npm test` here before publishing a site with `new-site.ps1` or `update-site.ps1`.

| Command | Purpose |
|---|---|
| `npm run build` | Production assets with external source maps in `dist/` |
| `npm run build:dev` | Readable assets with inline source maps |
| `npm run watch` | Rebuild assets when their source changes |
| `npm start` | Build, watch and serve the template previews under `http://127.0.0.1:8000/demo/pages/` |
| `npm run templates` | Render local HTML previews with fixture data |
| `npm run templates:emit` | Also generate the common host's Razor layouts |
| `npm run templates:check` | Validate template directives without writing output |
| `npm run check` | Check JavaScript types and JSDoc contracts |
| `npm test` | Run form runtime, template generation and website behaviour tests |
| `npm run clean` | Remove generated assets from `dist/` |

Build assets and emit templates before running tests. Stop the preview server with Ctrl+C
so it releases its listener and watchers.

## Source and outputs

| Source | Output or responsibility |
|---|---|
| `src/website/` | `dist/scripts/site.js`, `dist/css/site.css` for the website theme |
| `src/app/` | `dist/scripts/app.js`, `dist/css/app.css` for the application shell |
| `src/scss/` | The block styles both themes share: Umbraco's grid and the forms, tabular and schedule tokens |
| `src/templates/website/` | Website layout sources and preview fixtures |
| `src/templates/application/` | Application layout sources and preview fixtures |
| `test/` | Headless regression tests and optional browser fixtures |

The website theme has no runtime dependencies. Neither bundle needs jQuery or Bootstrap.

The template compiler writes five generated views directly into
`src/UmbracoAppHost/Views/`. Edit their `.template.html` sources here. CMS partials,
form/report page views, C# resources and startup code live in the shared library. The two
template families select presentation; they are not separate deployable projects.

Templates use a small directive vocabulary for content values, dictionary text, partials,
components, block grids, assets and the layout body. `fixtures.json` supplies local preview
values. Conditional or server behavior belongs in Razor partials or C# components. Raw
Razor sections use Umbraco 18 APIs directly and are not executed in the static previews.

## Capturing real website output

Capture a rendered page when changes depend on editor-authored markup:

```powershell
npm run capture -- --origin=https://localhost:44400
npm run capture -- /en/home --name=home --origin=https://localhost:44393
```

The capture tool writes ignored fixtures under `demo/templates/`, substitutes the shared
website bundles and resolves other root-relative URLs against the supplied site. Captures
retain the site's rendered content and inline scripts; refresh them when that markup changes.

See [Deployment](../docs/deployment.md) for reference instances, releases and updates.
