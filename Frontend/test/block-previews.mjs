import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { JSDOM } from 'jsdom';
import { renderAppBlock, fieldTypes, values, listItems } from '../../../AppBlocks/src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/preview-model.js';
import { appBlockTypes } from '../../../AppBlocks/src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/block-types.js';

const repos = resolve(fileURLToPath(new URL('../../../', import.meta.url)));
// These pure layouts already have the required preview: Umbraco's own card and editable areas.
const nativeLayouts = new Set(['appFullWidth', 'appThreeColumns', 'blockTwoColumn1', 'blockTwoColumn2', 'blockTwoColumn3', 'umbBlockGridDemoTwoColumnLayoutBlock']);
const owners = [
    ['AppBlocks/src/AppBlocks', 'Schema'], ['AppBlocks/src/AppBlocks.InteractiveReports', 'Schema'],
    ['AppBlocks/src/AppBlocks.Documents', 'Schema'], ['UmbracoAppHost/src/UmbracoAppHost', 'uSync'],
    ...['4WingResources', '4WingDiagrams', '4WingTelephoneDirectory', '4WingFileRepository', '4WingActivityScheduler', '4WingAerodromeStatus'].map(name => [`4Wing/${name}`, 'Schema']),
];
async function files(directory, suffix) {
    let entries;
    try { entries = await readdir(directory, { withFileTypes: true }); } catch (error) { if (error.code === 'ENOENT') return []; throw error; }
    return (await Promise.all(entries.map(entry => entry.isDirectory() ? files(resolve(directory, entry.name), suffix) : entry.name.endsWith(suffix) ? [resolve(directory, entry.name)] : []))).flat();
}
test('every owned element offered by the block editors has a preview in its owning library', async () => {
    const allTypes = new Map();
    const configs = [];
    const previewAliases = new Set();
    const previewScopes = new Set();
    for (const [project, schema] of owners) {
        const manifests = await files(resolve(repos, project, 'wwwroot/App_Plugins'), 'umbraco-package.json');
        const aliases = new Set();
        for (const path of manifests) {
            const manifest = JSON.parse(await readFile(path, 'utf8'));
            for (const ext of manifest.extensions ?? []) if (ext.type === 'blockEditorCustomView') {
                aliases.add(ext.forContentTypeAlias);
                const scope = `${ext.forContentTypeAlias} in ${ext.forBlockEditor ?? 'every block editor'}`;
                assert(!previewScopes.has(scope), `Duplicate preview: ${scope}`);
                previewScopes.add(scope);
                previewAliases.add(ext.forContentTypeAlias);
                await readFile(resolve(repos, project, 'wwwroot', ext.element.replace(/^\//, '')));
            }
        }
        for (const path of await files(resolve(repos, project, schema), '.config')) {
            const doc = new JSDOM(await readFile(path, 'utf8'), { contentType: 'text/xml' }).window.document;
            const type = doc.querySelector('ContentType');
            if (type) {
                const alias = type.getAttribute('Alias');
                allTypes.set(type.getAttribute('Key'), alias);
                const nestedType = appBlockTypes.find(item => item.alias === alias);
                if (nestedType) assert.equal(nestedType.key, type.getAttribute('Key'), `Nested preview identity differs: ${alias}`);
                if (doc.querySelector('Info > IsElement')?.textContent === 'true' && !['appColumnsSettings', 'appRegionSettings', 'fourWingMediaPresentation'].includes(alias)) {
                    assert(aliases.has(alias) || nativeLayouts.has(alias), `${project} does not preview ${alias}`);
                }
            }
            const config = doc.querySelector('DataType > Config')?.textContent;
            if (config) { try { configs.push(JSON.parse(config)); } catch {} }
        }
    }
    for (const config of configs) for (const block of config.blocks ?? []) {
        const alias = allTypes.get(block.contentElementTypeKey);
        assert(alias, `Unknown configured type ${block.contentElementTypeKey}`);
        assert(previewAliases.has(alias) || nativeLayouts.has(alias), `Configured block has no preview: ${alias}`);
        if (nativeLayouts.has(alias)) assert(block.areas?.length > 0, `Native layout has no configured areas: ${alias}`);
    }
});

test('every AppBlocks type renders an intentional representation, including nested forms', () => {
    for (const { alias } of appBlockTypes) {
        const result = renderAppBlock(alias, {}, {}, () => '<p>Nested form content</p>');
        assert(!result.includes('Preview unavailable'), alias);
        if (!['appFullWidth', 'appColumns', 'appThreeColumns', 'appRegion', 'appFieldset'].includes(alias)) assert(result.length > 0, alias);
    }
    assert.match(renderAppBlock('appFormsContainer', {}, {}, () => '<p>Nested form content</p>'), /Nested form content/);
});

test('field previews encode authored values and have no working controls', () => {
    for (const alias of Object.keys(fieldTypes)) {
        const markup = renderAppBlock(alias, { label: '<script>bad()</script>', defaultValue: '" autofocus onfocus="bad()', placeholder: '<unsafe>', helpText: '<img src=x>', required: true });
        const doc = new JSDOM(markup).window.document;
        assert.equal(doc.querySelector('script,img,[autofocus],[onfocus]'), null, alias);
        for (const control of doc.querySelectorAll('input,select,textarea,button')) assert(control.disabled, alias);
        assert(doc.querySelector('label').textContent.endsWith(' *'));
    }
});

test('nested lists follow layout order and select the active culture without leaking other variants', () => {
    const data = { layout: { 'Umbraco.BlockList': [{ contentKey: 'b' }, { contentKey: 'a' }] }, contentData: [
        { key: 'a', values: [{ alias: 'title', culture: null, value: 'A' }] },
        { key: 'b', values: [{ alias: 'title', culture: 'en-US', value: 'English' }, { alias: 'title', culture: 'fr-FR', value: 'Français' }] },
    ] };
    assert.deepEqual(listItems(data, 'fr-FR').map(item => item.title), ['Français', 'A']);
    assert.deepEqual(values(data.contentData[1]), {});
});

test('code and interactive previews do not execute page code or submit forms', () => {
    for (const alias of ['appScript', 'appCss', 'appForm', 'appActions', 'appAttachments', 'appTabular', 'appSchedule', 'appAntiSpam']) {
        const doc = new JSDOM(renderAppBlock(alias, { script: '<script>bad()</script>', css: '</style><script>bad()</script>' })).window.document;
        assert.equal(doc.querySelector('script,form,iframe'), null, alias);
        for (const button of doc.querySelectorAll('button')) assert(button.disabled, alias);
    }
});
