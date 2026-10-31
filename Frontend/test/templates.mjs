import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { JSDOM } from 'jsdom';

const root = resolve(import.meta.dirname, '..');
test('starter dictionary covers the keys referenced by templates and Razor views', () => {
  const exportRoot = resolve(root, '../src/UmbracoAppHost/uSync');
  const xml = file => new JSDOM(readFileSync(file, 'utf8'), { contentType: 'text/xml' }).window.document;
  const seeds = Object.fromEntries(readdirSync(join(exportRoot, 'Dictionary')).filter(file => file.endsWith('.config')).map(file => {
    const document = xml(join(exportRoot, 'Dictionary', file));
    return [document.documentElement.getAttribute('Alias'), Object.fromEntries([...document.querySelectorAll('Translation')]
      .map(value => [value.getAttribute('Language'), value.textContent]))];
  }));
  const cultures = readdirSync(join(exportRoot, 'Languages')).filter(file => file.endsWith('.config'))
    .map(file => xml(join(exportRoot, 'Languages', file)).querySelector('IsoCode').textContent);
  const keys = new Set();
  for (const directory of [resolve(root, 'src/templates'), resolve(root, '../src/UmbracoAppHost/Views')]) {
    for (const file of readdirSync(directory, { recursive: true }).filter(name => /\.(?:template\.html|cshtml)$/.test(name))) {
      const source = readFileSync(join(directory, file), 'utf8');
      if (source.includes('GENERATED FILE -- DO NOT EDIT.')) continue;
      for (const match of source.matchAll(/\{\{\s*dict\s+"([^"]+)"|GetDictionaryValue(?:OrDefault)?\(\s*"([^"]+)"/g)) {
        keys.add(match[1] ?? match[2]);
      }
    }
  }
  assert.ok(keys.size > 0);
  for (const key of keys) {
    assert.ok(seeds[key], `Missing dictionary seed: ${key}`);
    for (const culture of cultures) {
      assert.equal(typeof seeds[key][culture], 'string', `Missing ${culture} value for ${key}`);
      assert.ok(seeds[key][culture].length > 0, `Empty ${culture} placeholder for ${key}`);
    }
  }
});

test('website and application templates emit Umbraco 18 views with their assets', () => {
  const target = mkdtempSync(join(tmpdir(), 'frontend-templates-'));
  try {
    const result = spawnSync(process.execPath, ['templates.mjs', '--emit', '--target', target], { cwd: root, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    const files = readdirSync(target).filter(f => f.endsWith('.cshtml'));
    assert.deepEqual(files.sort(), ['AppHome.cshtml', 'AppMaster.cshtml', 'AppPage.cshtml', 'AppSignIn.cshtml', 'WebsiteLayout.cshtml']);

    const website = readFileSync(join(target, 'WebsiteLayout.cshtml'), 'utf8');
    assert.match(website, /~\/dist\/scripts\/site\.js/);
    assert.match(website, /~\/dist\/css\/site\.css/);
    assert.doesNotMatch(website, /vendor\.(css|js)|registered-(css|js)-bundle|debug="/);
    assert.match(website, /GetDictionaryValueOrDefault\("nav-menu", "Menu"\)/);
    assert.match(website, /PageAssets\.RegisterPage\(Model, NavigationQueryService, PublishedStatusFilteringService, Context\)/);
    assert.doesNotMatch(website, /Html\.Raw\((?:css|js)\)/);

    const application = readFileSync(join(target, 'AppMaster.cshtml'), 'utf8');
    assert.match(application, /~\/dist\/scripts\/app\.js/);
    assert.match(application, /~\/dist\/css\/app\.css/);
    assert.match(application, /GetDictionaryValueOrDefault\("app-skip", "Skip to content"\)/);
    assert.match(application, /Model\.SiteRoot\(NavigationQueryService, PublishedStatusFilteringService\)/);
    assert.match(application, /PageAssets\.RegisterPage\(Model, NavigationQueryService, PublishedStatusFilteringService, Context\)/);
  } finally { rmSync(target, { recursive: true }); }
});

test('removed CMS selection and unknown template families fail', () => {
  for (const [args, error] of [
    [['--cms', '13'], /Unknown argument: --cms/],
    [['--cms=18'], /Unknown argument: --cms=18/],
    [['--family', 'missing'], /No template family named "missing"/],
  ]) {
    const result = spawnSync(process.execPath, ['templates.mjs', ...args], { cwd: root, encoding: 'utf8' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, error);
  }
});
