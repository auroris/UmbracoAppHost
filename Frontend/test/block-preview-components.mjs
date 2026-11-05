// Exercises the custom views against the installed Umbraco modules in a DOM, without logging in or executing page runtimes.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { JSDOM, VirtualConsole } from 'jsdom';

const repos = fileURLToPath(new URL('../../../', import.meta.url));
const origin = process.env.PREVIEW_TEST_ORIGIN;
test('custom views render with the running host’s installed Umbraco modules', { skip: !origin }, async () => {
    const page = await (await fetch(`${origin}/umbraco/`)).text();
    const imports = JSON.parse(page.match(/<script type="importmap"[^>]*>([\s\S]*?)<\/script>/)[1]).imports;
    const scripts = [
        'AppBlocks/src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/block-preview.js',
        'AppBlocks/src/AppBlocks.InteractiveReports/wwwroot/App_Plugins/AppBlocks.InteractiveReports/block-preview.js',
        'AppBlocks/src/AppBlocks.InteractiveReports/wwwroot/App_Plugins/AppBlocks.InteractiveReports/block-preview-entrypoint.js',
        '4Wing/4WingResources/wwwroot/App_Plugins/4WingResources/block-preview.js',
        '4Wing/4WingDiagrams/wwwroot/App_Plugins/FourWing.Diagrams/block-preview.js',
        '4Wing/4WingTelephoneDirectory/wwwroot/App_Plugins/FourWing.TelephoneDirectory/block-preview.js',
        '4Wing/4WingFileRepository/wwwroot/App_Plugins/FourWing.FileRepository/block-preview.js',
        'UmbracoAppHost/src/UmbracoAppHost/wwwroot/App_Plugins/BlockPreviews.Host/block-preview.js',
    ];
    for (const path of scripts) {
        const response = await fetch(`${origin}/${path.split('/wwwroot/')[1]}`);
        assert.equal(response.status, 200, path);
        assert.equal(await response.text(), await readFile(resolve(repos, path), 'utf8'), path);
    }
    const bundled = await build({
        stdin: { contents: scripts.map(path => path.endsWith('block-preview-entrypoint.js')
            ? `import { onInit } from ${JSON.stringify(resolve(repos, path))}; onInit();`
            : `import ${JSON.stringify(resolve(repos, path))};`).join('\n'), resolveDir: repos },
        bundle: true, write: false, format: 'iife', logLevel: 'silent',
        define: { 'import.meta.url': JSON.stringify(`${origin}/App_Plugins/ApplicationBlocks/block-preview.js`) },
        plugins: [{ name: 'installed-umbraco', setup(builder) {
            builder.onResolve({ filter: /^\.\.\/ApplicationBlocks\// }, args => ({ path: resolve(repos, 'AppBlocks/src/AppBlocks/wwwroot/App_Plugins', args.path.substring(3)) }));
            builder.onResolve({ filter: /^@umbraco-cms\// }, args => {
                assert(imports[args.path], `Installed Umbraco does not export ${args.path}`);
                return { path: new URL(imports[args.path], origin).href, namespace: 'umbraco' };
            });
            builder.onResolve({ filter: /^\./, namespace: 'umbraco' }, args => ({ path: new URL(args.path, args.importer).href, namespace: 'umbraco' }));
            builder.onLoad({ filter: /.*/, namespace: 'umbraco' }, async args => {
                const response = await fetch(args.path);
                assert.equal(response.status, 200, args.path);
                return { contents: await response.text(), loader: 'js' };
            });
        } }],
    });
    const errors = [];
    const console = new VirtualConsole();
    console.on('jsdomError', error => errors.push(error));
    const dom = new JSDOM('<!doctype html><html><body></body></html>', { url: `${origin}/umbraco/`, runScripts: 'outside-only', pretendToBeVisual: true, virtualConsole: console });
    dom.window.ResizeObserver = class { observe() {} disconnect() {} unobserve() {} };
    dom.window.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {} });
    dom.window.fetch = fetch;
    dom.window.eval(bundled.outputFiles[0].text);
    const samples = [
        ['app-block-preview', 'appHtml', { html: '<style>h2{color:navy}</style><h2>Actual HTML</h2><script>bad()</script><img src=x onerror="bad()"><form><button>Submit</button></form>' }],
        ['app-block-preview', 'appColumns', {}, { split: '4/8' }],
        ['app-block-preview', 'appText', { label: 'First name', placeholder: 'Enter name' }],
        ['app-report-preview', 'appReport', { heading: 'Orders', dataSource: 'orders' }],
        ['app-block-preview', 'appFormsContainer', { forms: { layout: { 'Umbraco.BlockGrid': [{ contentKey: 'report', columnSpan: 12 }] }, contentData: [{ key: 'report', contentTypeKey: '067680b2-58b7-3fd9-a43d-d7e41f31ccde', values: [{ alias: 'heading', value: 'Nested orders' }, { alias: 'dataSource', value: 'orders' }] }] } }],
        ['four-wing-block-preview', 'fourWingTabs', { items: { layout: { 'Umbraco.BlockList': [{ contentKey: 'a' }] }, contentData: [{ key: 'a', values: [{ alias: 'title', value: 'Section one' }, { alias: 'body', value: { markup: '<p>Actual tab content</p>' } }] }] } }],
        ['four-wing-diagram-preview', 'fourWingDiagram', {}],
        ['four-wing-telephone-preview', 'fourWingTelephoneDirectory', { view: 'positions', scope: 'unit', filter: 'Operations' }],
        ['four-wing-repository-preview', 'fourWingFileRepository', {}],
        ['app-host-demo-preview', 'umbBlockGridDemoHeadlineBlock', { headline: 'Heading' }],
    ];
    for (const [tag, alias, content, settings = {}] of samples) {
        const element = dom.window.document.createElement(tag);
        element.manifest = { forContentTypeAlias: alias };
        element.content = content; element.settings = settings; element.label = alias;
        element.config = { showContentEdit: true, editContentPath: '/edit-block' };
        dom.window.document.body.append(element);
        await element.updateComplete;
        assert(element.shadowRoot.querySelector('umb-block-grid-areas-container'), alias);
        const frame = element.shadowRoot.querySelector('iframe');
        if (alias === 'appColumns') {
            assert.equal(frame, null);
            assert.match(element.shadowRoot.querySelector('.split').getAttribute('style'), /1fr 2fr/);
        } else {
            assert(frame, alias);
            assert.equal(frame.getAttribute('sandbox'), 'allow-same-origin');
            assert.equal(element.shadowRoot.querySelector('.edit-preview').getAttribute('href'), '/edit-block');
            const preview = new JSDOM(frame.srcdoc).window.document;
            assert.equal(preview.querySelector('script,form,[onerror]'), null, alias);
            if (alias === 'appHtml') {
                assert.equal(preview.querySelector('h2').textContent, 'Actual HTML');
                assert.match(preview.body.querySelector('style').textContent, /color:navy/);
                const areas = element.shadowRoot.querySelector('umb-block-grid-areas-container');
                element.content = { html: '<h2>Unsaved update</h2>' };
                await element.updateComplete;
                assert.match(element.shadowRoot.querySelector('iframe').srcdoc, /Unsaved update/);
                assert.equal(element.shadowRoot.querySelector('umb-block-grid-areas-container'), areas);
                element.config = { showContentEdit: false };
                await element.updateComplete;
                assert.equal(element.shadowRoot.querySelector('.edit-preview'), null);
            }
            if (alias === 'fourWingTabs') assert.match(preview.body.textContent, /Actual tab content/);
            if (alias === 'appFormsContainer') {
                assert.match(preview.body.textContent, /Nested orders/);
                assert(!preview.body.textContent.includes('Preview unavailable'));
            }
        }
        element.remove();
    }
    dom.window.close();
    assert.deepEqual(errors, []);
});
