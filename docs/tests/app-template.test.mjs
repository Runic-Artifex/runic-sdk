import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { readdir } from 'node:fs/promises';
import test from 'node:test';

import {
  appearanceKeys,
  themeModes,
  themePalettes,
} from '../src/lib/appearance.ts';

const expectedThemeModes = ['system', 'light', 'dark'];
const expectedThemePalettes = ['runic', 'moss', 'fjord', 'ember'];
const expectedAppearanceKeys = {
  mode: 'runic-docs.theme-mode',
  palette: 'runic-docs.theme-palette',
};

test('initializes validated docs appearance before SvelteKit head rendering', async () => {
  const html = await readFile(
    new URL('../src/app.html', import.meta.url),
    'utf8',
  );
  const initializer = html.match(/<script>([\s\S]*?)<\/script>/)?.[1];

  assert.ok(initializer, 'missing pre-hydration appearance initializer');
  assert.match(html, /<html lang="en" class="dark" data-theme="runic">/);
  assert.match(html, /<meta name="color-scheme" content="dark light" \/>/);
  assert.ok(
    html.indexOf('<script>') < html.indexOf('%sveltekit.head%'),
    'appearance initializer must precede %sveltekit.head%',
  );
  assert.deepEqual(themeModes, expectedThemeModes);
  assert.deepEqual(themePalettes, expectedThemePalettes);
  assert.deepEqual(appearanceKeys, expectedAppearanceKeys);

  const initializerModes = parseInitializerArray(initializer, 'modes');
  const initializerPalettes = parseInitializerArray(initializer, 'palettes');
  const initializerKeys = [
    ...initializer.matchAll(/localStorage\.getItem\(\s*'([^']+)'\s*,?\s*\)/g),
  ].map((match) => match[1]);

  assert.deepEqual(initializerModes, expectedThemeModes);
  assert.deepEqual(initializerPalettes, expectedThemePalettes);
  assert.deepEqual(initializerKeys, Object.values(expectedAppearanceKeys));
  assert.deepEqual(initializerModes, themeModes);
  assert.deepEqual(initializerPalettes, themePalettes);
  assert.deepEqual(initializerKeys, Object.values(appearanceKeys));
  assert.match(initializer, /modes\.includes\(storedMode\)/);
  assert.match(initializer, /palettes\.includes\(storedPalette\)/);
  assert.match(initializer, /\? storedMode : 'dark'/);
  assert.match(initializer, /: 'runic'/);
  assert.match(initializer, /runic-docs\.theme-mode/);
  assert.match(initializer, /runic-docs\.theme-palette/);
  assert.match(initializer, /prefers-color-scheme: dark/);
  assert.match(initializer, /style\.colorScheme = dark \? 'dark' : 'light'/);
  assert.match(
    initializer,
    /document\.documentElement\.classList\.add\('js'\)/,
  );
  assert.doesNotMatch(html, /runic-translations\.theme-/);
  assert.doesNotMatch(html, /webui\.js|translations-editor/i);
});

test('reveals the hydrated Sheet trigger only after JavaScript is detected', async () => {
  const html = await readFile(
    new URL('../src/app.html', import.meta.url),
    'utf8',
  );
  const css = await readFile(
    new URL('../src/app.css', import.meta.url),
    'utf8',
  );

  assert.match(html, /document\.documentElement\.classList\.add\('js'\)/);
  assert.match(css, /\.mobile-menu-button\s*\{[\s\S]*?display:\s*none/);
  assert.match(
    css,
    /@media\s*\(max-width:\s*\d+px\)[\s\S]*?\.js \.mobile-menu-button\s*\{[^}]*display:\s*inline-flex/,
  );
  assert.doesNotMatch(
    css,
    /(?<!\.js )\.mobile-menu-button\s*\{[^}]*display:\s*inline-flex/,
  );
});

function parseInitializerArray(initializer, name) {
  const match = initializer.match(
    new RegExp(`const ${name} = \\[([^\\]]+)\\]`),
  );
  assert.ok(match, `missing initializer ${name} allowlist`);
  return [...match[1].matchAll(/'([^']+)'/g)].map((entry) => entry[1]);
}

test('owns shared root appearance state and both desktop and mobile controls', async () => {
  const layout = await readFile(
    new URL('../src/routes/+layout.svelte', import.meta.url),
    'utf8',
  );
  const menu = await readFile(
    new URL('../src/lib/components/AppearanceMenu.svelte', import.meta.url),
    'utf8',
  );

  assert.match(layout, /let themeMode = \$state<ThemeMode>\('dark'\)/);
  assert.match(layout, /let themePalette = \$state<ThemePalette>\('runic'\)/);
  assert.match(layout, /onMount\(\(\) => \{/);
  assert.match(layout, /const appearance = readAppearance\(\)/);
  assert.match(layout, /applyAppearance\(themeMode, themePalette\)/);
  assert.match(layout, /return subscribeSystemAppearance/);
  assert.equal(layout.match(/<AppearanceMenu/g)?.length, 2);
  assert.match(layout, /<AppearanceMenu[\s\S]*?compact/);
  assert.match(
    layout,
    /<Sheet\.Content[\s\S]*?<AppearanceMenu[\s\S]*?onpalettechange=/,
  );
  assert.match(layout, /<noscript>[\s\S]*?noscript-nav[\s\S]*?<\/noscript>/);

  assert.match(menu, /let accessibleLabel = \$derived/);
  assert.match(menu, /aria-label=\{accessibleLabel\}/);
  assert.match(
    menu,
    /data-appearance-trigger=\{compact \? 'compact' : 'full'\}/,
  );
  assert.match(menu, /<DropdownMenu\.Label>Appearance<\/DropdownMenu\.Label>/);
  assert.match(menu, /<DropdownMenu\.Label>Color theme<\/DropdownMenu\.Label>/);
  for (const value of ['system', 'light', 'dark']) {
    assert.match(menu, new RegExp(`RadioItem value="${value}"`));
  }
  for (const value of ['runic', 'moss', 'fjord', 'ember']) {
    assert.match(menu, new RegExp(`RadioItem value="${value}"`));
  }
});

test('keeps the Editor dropdown wrapper snapshot byte-equivalent', async () => {
  const directory = new URL(
    '../src/lib/components/ui/dropdown-menu/',
    import.meta.url,
  );
  const files = (await readdir(directory)).sort();
  const hash = createHash('sha256');

  for (const file of files) {
    hash.update(file);
    hash.update('\0');
    hash.update(await readFile(new URL(file, directory)));
    hash.update('\0');
  }

  assert.equal(files.length, 18);
  assert.equal(
    hash.digest('hex'),
    'a6c0279ac20717664f271ce234549e4992dc8bc4aba9c285c3ae2de8615e13a2',
  );
});
