import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

import {
  appearanceKeys,
  applyAppearance,
  defaultAppearance,
  isThemeMode,
  isThemePalette,
  readAppearance,
  resolveDarkMode,
  saveAppearance,
  subscribeSystemAppearance,
  themeModes,
  themePalettes,
} from '../src/lib/appearance.ts';

const expectedThemeModes = ['system', 'light', 'dark'];
const expectedThemePalettes = ['runic', 'moss', 'fjord', 'ember'];
const expectedAppearanceKeys = {
  mode: 'runic-docs.theme-mode',
  palette: 'runic-docs.theme-palette',
};
const expectedEditorThemeMatrixSha256 =
  '39409f85f4e943f00f6880c9b5c74d5a7cb7ffa20b9f1d2ed1943fbb8361d544';

function createStorage() {
  const values = new Map();
  const reads = [];
  const writes = [];
  return {
    values,
    reads,
    writes,
    getItem(key) {
      reads.push(key);
      return values.get(key) ?? null;
    },
    setItem(key, value) {
      writes.push([key, String(value)]);
      values.set(key, String(value));
    },
  };
}

function createRoot() {
  const classes = new Set();
  return {
    classes,
    classList: {
      toggle(name, force) {
        if (force) classes.add(name);
        else classes.delete(name);
        return Boolean(force);
      },
    },
    dataset: {},
    style: { colorScheme: '' },
  };
}

function createMedia(matches = false) {
  const listeners = new Set();
  return {
    matches,
    listeners,
    addEventListener(type, listener) {
      assert.equal(type, 'change');
      listeners.add(listener);
    },
    removeEventListener(type, listener) {
      assert.equal(type, 'change');
      listeners.delete(listener);
    },
    emit() {
      for (const listener of [...listeners]) listener();
    },
  };
}

test('uses typed dark Runic defaults and strict guards', () => {
  assert.deepEqual(themeModes, expectedThemeModes);
  assert.deepEqual(themePalettes, expectedThemePalettes);
  assert.deepEqual(appearanceKeys, expectedAppearanceKeys);
  assert.deepEqual(defaultAppearance, { mode: 'dark', palette: 'runic' });
  assert.deepEqual(readAppearance(undefined), defaultAppearance);

  for (const mode of themeModes) assert.equal(isThemeMode(mode), true);
  for (const palette of themePalettes)
    assert.equal(isThemePalette(palette), true);
  for (const invalid of [null, undefined, '', 'sepia', 1, {}]) {
    assert.equal(isThemeMode(invalid), false);
    assert.equal(isThemePalette(invalid), false);
  }
});

test('persists and applies every mode and palette combination with docs-only keys', () => {
  const storage = createStorage();
  const root = createRoot();
  const media = createMedia(false);

  for (const palette of themePalettes) {
    for (const mode of themeModes) {
      saveAppearance(mode, palette, storage, root, media);
      assert.deepEqual(readAppearance(storage), { mode, palette });
      assert.equal(root.dataset.theme, palette);
      assert.equal(root.style.colorScheme, mode === 'dark' ? 'dark' : 'light');
      assert.equal(root.classes.has('dark'), mode === 'dark');
    }
  }

  assert.equal(themeModes.length * themePalettes.length, 12);
  assert.deepEqual(
    new Set(storage.reads),
    new Set([appearanceKeys.mode, appearanceKeys.palette]),
  );
  assert.deepEqual(
    new Set(storage.writes.map(([key]) => key)),
    new Set([appearanceKeys.mode, appearanceKeys.palette]),
  );
  assert.equal(
    storage.writes.some(([key]) =>
      /runic-translations|translations-editor/.test(key),
    ),
    false,
  );
});

test('resolves system mode and falls back safely for corrupt or blocked storage', () => {
  assert.equal(resolveDarkMode('dark', false), true);
  assert.equal(resolveDarkMode('light', true), false);
  assert.equal(resolveDarkMode('system', false), false);
  assert.equal(resolveDarkMode('system', true), true);

  const storage = createStorage();
  storage.values.set(appearanceKeys.mode, 'sepia');
  storage.values.set(appearanceKeys.palette, 'unknown');
  assert.deepEqual(readAppearance(storage), defaultAppearance);
  assert.deepEqual(
    readAppearance({
      getItem() {
        throw new Error('blocked');
      },
      setItem() {},
    }),
    defaultAppearance,
  );

  const root = createRoot();
  const media = createMedia(true);
  applyAppearance('system', 'fjord', root, media);
  assert.equal(root.classes.has('dark'), true);
  assert.equal(root.dataset.theme, 'fjord');
  assert.equal(root.style.colorScheme, 'dark');
});

test('subscribes to system changes only in system mode and cleans up exactly once', () => {
  const media = createMedia(false);
  let appearance = { mode: 'system', palette: 'ember' };
  const applied = [];
  const cleanup = subscribeSystemAppearance(
    () => appearance,
    media,
    (mode, palette) => applied.push([mode, palette]),
  );

  assert.equal(media.listeners.size, 1);
  media.emit();
  assert.deepEqual(applied, [['system', 'ember']]);

  appearance = { mode: 'dark', palette: 'moss' };
  media.emit();
  assert.deepEqual(applied, [['system', 'ember']]);

  cleanup();
  assert.equal(media.listeners.size, 0);
  media.emit();
  assert.deepEqual(applied, [['system', 'ember']]);
});

test('defines the complete semantic token contract for all eight theme selectors', async () => {
  const css = await readFile(
    new URL('../src/routes/layout.css', import.meta.url),
    'utf8',
  );
  const appCss = await readFile(
    new URL('../src/app.css', import.meta.url),
    'utf8',
  );
  const requiredTokens = [
    'background',
    'foreground',
    'card',
    'card-foreground',
    'popover',
    'popover-foreground',
    'primary',
    'primary-foreground',
    'secondary',
    'secondary-foreground',
    'muted',
    'muted-foreground',
    'accent',
    'accent-foreground',
    'border',
    'input',
    'ring',
    'chart-1',
    'chart-2',
    'chart-3',
    'chart-4',
    'chart-5',
    'sidebar',
    'sidebar-foreground',
    'sidebar-primary',
    'sidebar-primary-foreground',
    'sidebar-accent',
    'sidebar-accent-foreground',
    'sidebar-border',
    'sidebar-ring',
  ];
  const selectors = [
    ':root',
    '.dark',
    ...expectedThemePalettes
      .filter((palette) => palette !== 'runic')
      .flatMap((palette) => [
        `:root:not(.dark)[data-theme='${palette}']`,
        `.dark[data-theme='${palette}']`,
      ]),
  ];

  assert.equal(selectors.length, 8);
  for (const selector of selectors) {
    const block = cssBlock(css, selector);
    for (const token of requiredTokens) {
      assert.match(
        block,
        new RegExp(`--${token}:`),
        `${selector} does not define --${token}`,
      );
    }
  }

  assert.equal(
    createHash('sha256').update(normalizedThemeMatrix(css)).digest('hex'),
    expectedEditorThemeMatrixSha256,
    'theme selector, variable, or value drifted from the pinned Editor matrix',
  );

  assert.match(appCss, /--docs-accent:\s*var\(--primary\)/);
  assert.match(appCss, /--docs-accent-soft:[\s\S]*?var\(--primary\)/);
  assert.match(appCss, /--docs-ambient-secondary:[\s\S]*?var\(--chart-2\)/);
  assert.doesNotMatch(appCss, /--runic-(?:gold|moss)/);
  assert.doesNotMatch(appCss, /oklch\(\d/);
});

function cssBlock(source, selector) {
  const start = source.indexOf(`${selector} {`);
  assert.notEqual(start, -1, `Missing theme selector ${selector}`);
  const bodyStart = source.indexOf('{', start) + 1;
  const end = source.indexOf('}', bodyStart);
  assert.notEqual(end, -1, `Unclosed theme selector ${selector}`);
  return source.slice(bodyStart, end);
}

function normalizedThemeMatrix(source) {
  const normalizedSelectors = [
    ':root',
    '.dark',
    ...expectedThemePalettes
      .filter((palette) => palette !== 'runic')
      .flatMap((palette) => [
        `:root:not(.dark)[data-theme=${palette}]`,
        `.dark[data-theme=${palette}]`,
      ]),
  ];
  const themes = [];
  const themeSource = source.slice(source.indexOf(':root {'));

  for (const [, rawSelector, body] of themeSource.matchAll(
    /([^{}]+)\{([^{}]*)\}/g,
  )) {
    const selector = rawSelector.trim().replaceAll(/["']/g, '');
    if (!normalizedSelectors.includes(selector)) continue;

    const variables = Object.fromEntries(
      [...body.matchAll(/--([\w-]+):\s*([^;]+);/g)].map(([, name, value]) => [
        name,
        value.replace(/\s+/g, ' ').trim(),
      ]),
    );
    themes.push([selector, variables]);
  }

  assert.deepEqual(
    themes.map(([selector]) => selector),
    normalizedSelectors,
    'theme selector order must stay identical to the Editor matrix',
  );
  return JSON.stringify(themes);
}
