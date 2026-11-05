import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { test } from 'node:test';
import { JSDOM } from 'jsdom';

const root = resolve(import.meta.dirname, '..');
const script = readFileSync(resolve(root, 'dist/scripts/site.js'), 'utf8');
const stylesheet = readFileSync(resolve(root, 'dist/css/site.css'), 'utf8');

function page() {
  const dom = new JSDOM(`<!doctype html><body>
    <button class="site-nav-toggle" aria-expanded="false" aria-controls="site-nav"></button>
    <nav class="site-nav" id="site-nav"><ul class="site-menu">
      <li><a href="/about/">About</a></li>
      <li class="has-submenu" id="group"><a>Group</a><button type="button" class="site-submenu-toggle" aria-expanded="false"></button><ul><li><a href="/in-group/">In group</a></li></ul></li>
      <li class="has-submenu" id="other"><a href="/other/">Other</a><button type="button" class="site-submenu-toggle" aria-expanded="false"></button><ul><li><a href="/in-other/">In other</a></li></ul></li>
    </ul></nav><p id="outside">Text</p></body>`, { runScripts: 'outside-only' });
  dom.window.eval(script);
  return dom.window;
}

const click = (element) => element.dispatchEvent(new element.ownerDocument.defaultView.MouseEvent('click', { bubbles: true }));

test('the menu folds behind its toggle once the script runs', () => {
  const window = page();
  const toggle = window.document.querySelector('.site-nav-toggle');
  const nav = window.document.querySelector('.site-nav');
  assert.ok(window.document.documentElement.classList.contains('js'));
  click(toggle);
  assert.ok(nav.classList.contains('is-open'));
  assert.equal(toggle.getAttribute('aria-expanded'), 'true');
  click(toggle);
  assert.ok(!nav.classList.contains('is-open'));
  assert.equal(toggle.getAttribute('aria-expanded'), 'false');
});

test('a submenu opens by its button or an addressless title, one at a time, and closes from outside', () => {
  const window = page();
  const group = window.document.getElementById('group');
  const other = window.document.getElementById('other');
  const groupButton = group.querySelector('button');
  click(groupButton);
  assert.ok(group.classList.contains('is-open'));
  assert.equal(groupButton.getAttribute('aria-expanded'), 'true');
  click(other.querySelector('button'));
  assert.ok(other.classList.contains('is-open'));
  assert.ok(!group.classList.contains('is-open'));
  assert.equal(groupButton.getAttribute('aria-expanded'), 'false');
  click(window.document.getElementById('outside'));
  assert.ok(!other.classList.contains('is-open'));
  click(group.querySelector('a'));
  assert.ok(group.classList.contains('is-open'));
  // A title with an address still goes there.
  click(other.querySelector('a'));
  assert.ok(!other.classList.contains('is-open'));
});

test('escape closes the submenu holding the focus and returns it to the button', () => {
  const window = page();
  const group = window.document.getElementById('group');
  const button = group.querySelector('button');
  button.focus();
  click(button);
  group.querySelector('ul a').focus();
  window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape' }));
  assert.ok(!group.classList.contains('is-open'));
  assert.equal(window.document.activeElement, button);
});

test('the stylesheets style the shell and every block family, with no borrowed framework', () => {
  for (const hook of ['.site-hero', '.site-menu', '.ah-layout>.ah-area', '.ah-region__title', '.ah-rich-text', '.app-form', '.app-tabular-table', '.app-schedule-scroll', '.app-report', '--umb-block-grid--area-grid-columns']) {
    assert.ok(stylesheet.includes(hook), `Missing ${hook}`);
  }
  assert.doesNotMatch(stylesheet, /Bootstrap|\.col-md-/);
  const signIn = readFileSync(resolve(root, 'dist/css/signin.css'), 'utf8');
  assert.ok(signIn.includes('.app-signin'));
  assert.doesNotMatch(signIn, /Bootstrap|\.col-md-/);
});
