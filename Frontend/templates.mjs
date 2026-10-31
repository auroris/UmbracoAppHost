/**
 * Template compiler: one source, two targets, per template family.
 *
 * Templates under src/templates/<family>/*.template.html are the master copy. Each one
 * compiles to:
 *
 *   - **Razor** (.cshtml) for Umbraco 18, written into ../src/UmbracoAppHost/Views/,
 *     or into an explicit --target folder.
 *   - **HTML** for local development, written into demo/pages/<family>/, rendered with
 *     the mock data in src/templates/<family>/fixtures.json
 *
 * Two families exist: `website`, the public site theme, and
 * `application`, the APEX-style shell the applications use.
 *
 * The directive vocabulary is deliberately tiny and closed. Every directive has a
 * total mapping to Razor and a total mapping to fixture data, so "renders locally but
 * won't compile to Razor" is impossible by construction. There are no loops and no
 * conditionals: anything needing them belongs in C#, behind a partial or a view
 * component. See README.md for where that line falls and why.
 *
 * Directives
 *   {{ value "title" }}                  @Model.Value("title")
 *   {{ value "badge" fallback=ancestors }}   ... fallback: Fallback.ToAncestors
 *   {{ dict "Search" }}                  @Umbraco.GetDictionaryValue("Search")
 *   {{ partial "Website/Menu" }}         <partial name="Website/Menu" />
 *   {{ component "Breadcrumb" }}         <vc:breadcrumb />
 *   {{ blockgrid "content" }}            @await Html.GetBlockGridHtmlAsync(...)
 *   {{ body }}                           @RenderBody()
 *   {{ styles "site" }}                  one <link> per bundle under dist/css, version-stamped
 *   {{ scripts "site" }}                 one <script> per bundle under dist/scripts
 *   {{ razor "expression" }}             @(expression)          -- escape hatch
 *   {{# razor }} ... {{# endrazor }}     verbatim Razor block   -- escape hatch
 *   {{! comment }}                       dropped from both outputs
 *
 * Usage
 *   node templates.mjs                       compile every family to demo/pages/ only (safe, default)
 *   node templates.mjs --emit                also write .cshtml into the common host's Views/
 *   node templates.mjs --check               compile and report, write nothing
 *   node templates.mjs --family website      one family only
 *   node templates.mjs --emit --target ./tmp/Views
 *                                            write Razor into another existing Views folder
 */

import { readFile, writeFile, readdir, mkdir, stat } from 'node:fs/promises';
import { resolve, basename } from 'node:path';

const root = import.meta.dirname;
const templatesDir = resolve(root, 'src', 'templates');
const demoDir = resolve(root, 'demo', 'pages');
const hostViews = resolve(root, '..', 'src', 'UmbracoAppHost', 'Views');

/**
 * Reads `--flag value` and bare `--flag` arguments.
 *
 * @param {string[]} argv
 * @returns {{emit: boolean, check: boolean, family: string|null, target: string|null}}
 */
function parseArgs(argv) {
  const result = { emit: false, check: false, family: null, target: null };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--emit') result.emit = true;
    else if (arg === '--check') result.check = true;
    else if (arg === '--family') result.family = argv[++i] ?? null;
    else if (arg === '--target') result.target = argv[++i] ?? null;
    else if (arg.startsWith('--family=')) result.family = arg.slice('--family='.length);
    else if (arg.startsWith('--target=')) result.target = arg.slice('--target='.length);
    else throw new Error(`Unknown argument: ${arg}`);
  }
  return result;
}

const cli = parseArgs(process.argv.slice(2));
const emit = cli.emit;
const checkOnly = cli.check;

/** Placeholder for {{ body }} in dev output, substituted once everything compiles. */
const BODY_SENTINEL = '<!--UMB_RENDER_BODY-->';

/** Bundles the styles and scripts directives reference when given no argument. */
const DEFAULT_BUNDLES = 'site';

/**
 * Marker so nobody mistakes generated Razor for a source file.
 *
 * @param {string} family
 * @param {string} source
 */
const GENERATED_HEADER = (family, source) =>
  `@* ------------------------------------------------------------------------\n` +
  `   GENERATED FILE -- DO NOT EDIT.\n` +
  `\n` +
  `   Master copy: Frontend/src/templates/${family}/${source} in the UmbracoAppHost repository\n` +
  `   Regenerate : cd Frontend && npm run templates:emit -- --family ${family}\n` +
  `\n` +
  `   Edits made here will be overwritten on the next build.\n` +
  `   ------------------------------------------------------------------------ *@\n`;

/**
 * Splits a template into literal text, raw-Razor blocks, and directives.
 *
 * @param {string} source
 * @returns {Array<{kind: 'text'|'rawblock'|'directive', value: string}>}
 */
function tokenize(source) {
  /** @type {Array<{kind: 'text'|'rawblock'|'directive', value: string}>} */
  const tokens = [];
  // Raw blocks first so their contents are never scanned for directives.
  //
  // The directive branch allows a lone `}` inside the braces -- only `}}` closes a
  // directive. Razor escape hatches genuinely contain them, e.g. the interpolated
  // string `$"https://{Context.Request.Host}/umbraco/"`, which a plain [^}]* would
  // truncate and silently emit as literal text.
  const pattern = /\{\{#\s*razor\s*\}\}([\s\S]*?)\{\{#\s*endrazor\s*\}\}|\{\{((?:[^}]|\}(?!\}))*)\}\}/g;
  let last = 0;
  let match;

  while ((match = pattern.exec(source)) !== null) {
    if (match.index > last) {
      tokens.push({ kind: 'text', value: source.slice(last, match.index) });
    }
    if (match[1] !== undefined) {
      tokens.push({ kind: 'rawblock', value: match[1] });
    } else {
      tokens.push({ kind: 'directive', value: match[2].trim() });
    }
    last = match.index + match[0].length;
  }

  if (last < source.length) {
    tokens.push({ kind: 'text', value: source.slice(last) });
  }

  return tokens;
}

/**
 * Parses `name "arg" key=value` into its parts.
 *
 * @param {string} directive
 * @returns {{name: string, arg: string|null, options: Record<string,string>}}
 */
function parseDirective(directive) {
  const nameMatch = /^([A-Za-z!][\w-]*|!)/.exec(directive);
  if (nameMatch === null) {
    throw new Error(`Cannot parse directive: {{ ${directive} }}`);
  }
  const name = nameMatch[1];
  let rest = directive.slice(name.length).trim();

  // Args accept either quote style. Single quotes exist so Razor escape hatches can
  // contain double quotes: {{ razor 'date.ToString("yyyy-MM-dd")' }}
  let arg = null;
  const argMatch = /^"([^"]*)"|^'([^']*)'/.exec(rest);
  if (argMatch !== null) {
    arg = argMatch[1] ?? argMatch[2];
    rest = rest.slice(argMatch[0].length).trim();
  }

  /** @type {Record<string,string>} */
  const options = {};
  for (const opt of rest.matchAll(/([\w-]+)=("[^"]*"|\S+)/g)) {
    options[opt[1]] = opt[2].replace(/^"|"$/g, '');
  }

  return { name, arg, options };
}

/** @param {string} value */
function escapeHtml(value) {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/**
 * The bundle names a styles or scripts directive lists, defaulting to the website pair.
 *
 * @param {{arg: string|null}} d
 * @returns {string[]}
 */
function bundles(d) {
  return (d.arg ?? DEFAULT_BUNDLES).trim().split(/\s+/);
}

/**
 * Emits Razor for one directive.
 *
 * @param {{name: string, arg: string|null, options: Record<string,string>}} d
 * @returns {string}
 */
function toRazor(d) {
  const fallback =
    d.options.fallback === 'ancestors' ? ', fallback: Fallback.ToAncestors' : '';

  switch (d.name) {
    case '!':
      return '';
    case 'value':
      return `@Model.Value("${d.arg}"${fallback})`;
    case 'dict':
      // Keep labels readable if a dictionary entry has not been bootstrapped yet.
      return d.options.default === undefined
        ? `@Umbraco.GetDictionaryValue("${d.arg}")`
        : `@Umbraco.GetDictionaryValueOrDefault("${d.arg}", "${d.options.default}")`;
    case 'partial':
      return `<partial name="${d.arg}" />`;
    case 'component':
      // Tag-helper form; kebab-cased as ASP.NET expects.
      return `<vc:${String(d.arg).replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase()} />`;
    case 'blockgrid':
      return `@await Html.GetBlockGridHtmlAsync(Model.Value("${d.arg}") as BlockGridModel)`;
    case 'body':
      return '@RenderBody()';
    case 'styles':
      // `~/` follows the site's virtual path and asp-append-version stamps the file
      // hash for cache busting. _ViewImports.cshtml enables both ASP.NET Core tag helpers.
      return bundles(d)
        .map((bundle) => `<link rel="stylesheet" href="~/dist/css/${bundle}.css" asp-append-version="true" />`)
        .join('\n    ');
    case 'scripts':
      return bundles(d)
        .map((bundle) => `<script src="~/dist/scripts/${bundle}.js" asp-append-version="true"></script>`)
        .join('\n    ');
    case 'razor':
      // A bare identifier, member access or no-argument call needs no parentheses, and
      // reads the way the hand-written Razor did. Anything else -- arguments, string
      // literals, interpolation -- gets wrapped so Razor's parser cannot mis-scan it.
      return /^[A-Za-z_][\w.]*(\(\))?$/.test(String(d.arg)) ? `@${d.arg}` : `@(${d.arg})`;
    default:
      throw new Error(`Unknown directive "${d.name}" -- the vocabulary is closed on purpose.`);
  }
}

/**
 * Emits development HTML for one directive using fixture data.
 *
 * @param {{name: string, arg: string|null, options: Record<string,string>}} d
 * @param {any} fixtures
 * @param {string[]} warnings
 * @returns {string}
 */
function toHtml(d, fixtures, warnings) {
  /** @param {string} group */
  const lookup = (group) => {
    const table = fixtures[group] ?? {};
    if (!(String(d.arg) in table)) {
      // Mirror the Razor dictionary fallback: a declared default is the answer, not a
      // missing-fixture warning.
      if (d.options.default !== undefined) {
        return d.options.default;
      }
      warnings.push(`fixtures.json has no ${group}["${d.arg}"]`);
      return `<mark>[${group}: ${escapeHtml(String(d.arg))}]</mark>`;
    }
    return table[String(d.arg)];
  };

  switch (d.name) {
    case '!':
      return '';
    case 'value':
      return escapeHtml(lookup('values'));
    case 'dict':
      return escapeHtml(lookup('dictionary'));
    case 'partial':
      return lookup('partials');
    case 'component':
      return lookup('components');
    case 'blockgrid':
      return lookup('blockgrid');
    case 'body':
      // Filled in after all templates compile, so a child template can be rendered
      // inside its layout locally the way Umbraco composes them.
      return BODY_SENTINEL;
    case 'styles':
      // The dev page loads the built bundles directly. Paths are relative to
      // demo/pages/<family>/.
      return bundles(d)
        .map((bundle) => `<link rel="stylesheet" href="../../../dist/css/${bundle}.css" />`)
        .join('\n    ');
    case 'scripts':
      return bundles(d)
        .map((bundle) => `<script src="../../../dist/scripts/${bundle}.js"></script>`)
        .join('\n    ');
    case 'razor':
      // Razor escape hatches cannot be evaluated in Node. Fixtures may supply a
      // stand-in keyed by the expression text; otherwise it is shown as a stub.
      return (fixtures.razor ?? {})[String(d.arg)] ?? `<mark>[razor: ${escapeHtml(String(d.arg))}]</mark>`;
    default:
      throw new Error(`Unknown directive "${d.name}"`);
  }
}

/**
 * Compiles one template to both targets.
 *
 * @param {string} source
 * @param {any} fixtures
 * @returns {{razor: string, html: string, warnings: string[], directives: number}}
 */
function compile(source, fixtures) {
  // Drop comment-only lines wholesale. Emitting '' for the directive would otherwise
  // leave the surrounding indentation and newline behind as blank lines in both
  // outputs.
  source = source.replace(/^[ \t]*\{\{!(?:[^}]|\}(?!\}))*\}\}[ \t]*\r?\n/gm, '');

  const tokens = tokenize(source);
  /** @type {string[]} */
  const warnings = [];
  let razor = '';
  let html = '';
  let directives = 0;

  for (const token of tokens) {
    if (token.kind === 'text') {
      razor += token.value;
      html += token.value;
      continue;
    }

    if (token.kind === 'rawblock') {
      razor += token.value;
      // The dev page cannot execute C#. Anything the page genuinely needs must be
      // expressed as a directive instead, so raw blocks simply vanish here.
      continue;
    }

    const d = parseDirective(token.value);
    if (d.name !== '!') {
      directives += 1;
    }
    razor += toRazor(d);
    html += toHtml(d, fixtures, warnings);
  }

  return { razor, html, warnings, directives };
}

/**
 * @param {string} dir
 * @returns {Promise<boolean>}
 */
async function isDirectory(dir) {
  try {
    return (await stat(dir)).isDirectory();
  } catch {
    return false;
  }
}

/**
 * Compiles one family and writes its outputs.
 *
 * @param {string} family
 * @returns {Promise<number>} the number of fixture warnings
 */
async function compileFamily(family) {
  const srcDir = resolve(templatesDir, family);
  const pagesDir = resolve(demoDir, family);
  const viewsDir = cli.target === null ? hostViews : resolve(process.cwd(), cli.target);

  const fixtures = JSON.parse(await readFile(resolve(srcDir, 'fixtures.json'), 'utf8'));
  const files = (await readdir(srcDir)).filter((f) => f.endsWith('.template.html')).sort();

  if (files.length === 0) {
    throw new Error(`No *.template.html files in ${srcDir}`);
  }
  if (emit && !checkOnly && !(await isDirectory(viewsDir))) {
    throw new Error(`Cannot emit ${family}: the Views folder does not exist:\n  ${viewsDir}`);
  }

  console.log(`\n${family}`);
  await mkdir(pagesDir, { recursive: true });

  let totalWarnings = 0;

  /** @type {Map<string, {razor: string, html: string, warnings: string[], directives: number, file: string}>} */
  const compiled = new Map();

  // Pass 1 -- compile every template.
  for (const file of files) {
    const name = basename(file, '.template.html');
    const source = await readFile(resolve(srcDir, file), 'utf8');

    try {
      compiled.set(name, { ...compile(source, fixtures), file });
    } catch (error) {
      console.error(`  FAILED  ${file}\n          ${error instanceof Error ? error.message : error}`);
      process.exitCode = 1;
    }
  }

  // Pass 2 -- compose dev pages and write output. `layouts` in fixtures.json maps a
  // child template to the layout it declares via `Layout = "...cshtml"`, which is inside
  // a Razor block and so invisible to the compiler.
  const layouts = fixtures.layouts ?? {};

  for (const [name, result] of compiled) {
    let devHtml = result.html;

    const layoutName = layouts[name];
    if (layoutName !== undefined) {
      const layout = compiled.get(layoutName);
      if (layout === undefined) {
        console.log(`      warning: ${name} declares layout "${layoutName}", which was not compiled`);
        totalWarnings += 1;
      } else {
        devHtml = layout.html.replace(BODY_SENTINEL, result.html);
      }
    }

    // Any sentinel still present belongs to a layout being previewed on its own.
    devHtml = devHtml.replace(BODY_SENTINEL, fixtures.body ?? '');

    // Root-absolute asset URLs in the markup (`/media/...`) are served by Umbraco, not
    // by the dev server, so repoint them at the running site the way capture.mjs does.
    // Skips the ../../../dist/ paths injected above, which are already relative.
    const origin = (fixtures.origin ?? 'https://localhost:44392').replace(/\/$/, '');
    devHtml = devHtml.replace(/\b(href|src)="\/(?!\/)([^"]*)"/g, (_m, attr, path) => `${attr}="${origin}/${path}"`);

    if (!checkOnly) {
      await writeFile(resolve(pagesDir, `${name}.html`), devHtml, 'utf8');
      if (emit) {
        // Umbraco rewrites a template file with a UTF-8 byte-order mark when it registers the
        // template, so the emitted file carries one too and the two agree byte for byte.
        await writeFile(resolve(viewsDir, `${name}.cshtml`), `﻿${GENERATED_HEADER(family, result.file)}${result.razor}`, 'utf8');
      }
    }

    const target = checkOnly
      ? 'checked'
      : emit
        ? `-> ${viewsDir}\\${name}.cshtml + demo/pages/${family}/${name}.html`
        : `-> demo/pages/${family}/${name}.html`;
    const inside = layoutName === undefined ? '' : `  (inside ${layoutName})`;
    console.log(`  ${name.padEnd(16)} ${String(result.directives).padStart(3)} directives  ${target}${inside}`);

    for (const warning of result.warnings) {
      console.log(`      warning: ${warning}`);
      totalWarnings += 1;
    }
  }

  return totalWarnings;
}

// ---------------------------------------------------------------------------

const families = (await readdir(templatesDir, { withFileTypes: true }))
  .filter((entry) => entry.isDirectory())
  .map((entry) => entry.name)
  .filter((name) => cli.family === null || name === cli.family)
  .sort();

if (families.length === 0) {
  console.error(cli.family === null ? `No template families under ${templatesDir}` : `No template family named "${cli.family}" under ${templatesDir}`);
  process.exit(1);
}

let warnings = 0;
for (const family of families) {
  try {
    warnings += await compileFamily(family);
  } catch (error) {
    console.error(`  FAILED  ${family}\n          ${error instanceof Error ? error.message : error}`);
    process.exitCode = 1;
  }
}

if (!emit && !checkOnly) {
  console.log('\nDev pages written. Add --emit (npm run templates:emit) to write the Views/*.cshtml files.');
}
if (warnings > 0) {
  console.log(`\n${warnings} fixture warning(s) -- those spots render as [placeholders] locally.`);
}
