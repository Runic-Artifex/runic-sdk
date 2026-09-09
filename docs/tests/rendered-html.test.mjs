import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import test from 'node:test';

import publishedRelease from '../src/lib/published-release.json' with { type: 'json' };
import {
  createReleaseDocs,
  packageInstallCommand,
} from '../src/lib/release-docs-core.ts';

const buildDirectory = fileURLToPath(new URL('../build/', import.meta.url));
const releaseDocs = createReleaseDocs(publishedRelease);

const primaryRoutes = [
  '/',
  '/getting-started',
  '/products',
  '/architecture',
  '/packages',
  '/releases',
  '/readiness',
  '/products/runic-toolkit',
  '/products/runic-desktop',
  '/application-bridge',
  '/products/runic-assets',
  '/products/runic-translations',
  '/products/runic-translations-editor',
  '/products/runic-command-line',
  '/products/cs-webui',
  '/products/runic-flow',
];

function render(path = '/') {
  const relativePath =
    path === '/' ? 'index.html' : `${path.slice(1)}/index.html`;
  return readFile(join(buildDirectory, relativePath), 'utf8');
}

function readMeta(html, attribute, name) {
  const matchingTags = [...html.matchAll(/<meta\b[^>]*>/g)]
    .map((match) => match[0])
    .filter((tag) => tag.includes(`${attribute}="${name}"`));
  assert.equal(matchingTags.length, 1, `expected one ${name} meta tag`);
  const content = matchingTags[0].match(/\bcontent="([^"]*)"/)?.[1];
  assert.ok(content, `expected content for ${name}`);
  return content;
}

function stripMarkup(value) {
  return value
    .replace(/<!--[\s\S]*?-->/g, '')
    .replace(/<[^>]+>/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/&#39;|&apos;/g, "'")
    .replace(/&quot;/g, '"')
    .replace(/\s+/g, ' ')
    .trim();
}

test('renders the documentation home with complete metadata and branding', async () => {
  const html = await render();
  assert.match(
    html,
    /^<!doctype html>\s*<html lang="en" class="dark" data-theme="runic">/,
  );
  assert.match(html, /name="color-scheme" content="dark light"/);
  assert.match(html, /<h1>[^<]+<\/h1>/);
  assert.match(html, /<title>[^<]+Runic Artifex<\/title>/);
  assert.match(html, /<small>Documentation<\/small>/);
  assert.match(
    html,
    /property="og:image" content="https:\/\/docs\.runic-artifex\.eu\/og\.png"/,
  );
  assert.match(
    html,
    /rel="canonical" href="https:\/\/docs\.runic-artifex\.eu\/"/,
  );
  assert.match(html, /href="\.\/getting-started"/);
  assert.match(html, /name="twitter:card" content="summary_large_image"/);
  assert.match(html, /rel="icon" href="\/icon\.png"/);
  assert.match(html, /runic-docs\.theme-mode/);
  assert.match(html, /runic-docs\.theme-palette/);
  assert.doesNotMatch(html, /runic-translations\.theme-/);
  assert.match(html, /aria-label="Appearance, Runic Gold · Dark"/);
  assert.match(html, /data-appearance-trigger="compact"/);
  assert.match(
    html,
    /<a class="skip-link" href="#content">Skip to content<\/a>/,
  );
  assert.match(html, /<main id="content" tabindex="-1">/);
  assert.equal(html.match(/<main\b/g)?.length, 1);
  assert.match(
    html,
    /background-image:\s*url\(\/products\/runic-toolkit\.png\)/,
  );
  assert.match(html, /Runic Application/);
  assert.match(html, /CS-WebUI/);
  assert.doesNotMatch(
    html,
    /codex-preview|SkeletonPreview|Your site is taking shape/,
  );
});

test('links to the dedicated project website while retaining the documentation identity', async () => {
  const html = await render();

  assert.match(html, /Runic Artifex Documentation/);
  assert.match(
    html,
    /href="https:\/\/runic-artifex\.eu\/"[^>]*>\s*Runic Artifex website/,
  );
});

test('keeps navigation usable before hydration and exposes the Sheet trigger contract', async () => {
  const homeHtml = await render();
  const productsHtml = await render('/products');
  const fallback = homeHtml.match(/<noscript>([\s\S]*?)<\/noscript>/)?.[1];

  assert.ok(fallback, 'expected a no-JavaScript navigation fallback');
  assert.match(fallback, /<details class="noscript-nav">/);
  assert.match(fallback, /Mobile navigation without JavaScript/);
  for (const [href, label] of [
    ['./getting-started', 'Start'],
    ['./products', 'Products'],
    ['./application-bridge', 'Application Bridge'],
    ['./architecture', 'Architecture'],
    ['./packages', 'Packages'],
    ['./releases', 'Releases'],
  ]) {
    assert.match(fallback, new RegExp(`href="${href}">${label}<\\/a>`));
  }

  assert.match(
    productsHtml,
    /<noscript>[\s\S]*?href="\.\.\/products" aria-current="page">Products<\/a>[\s\S]*?<\/noscript>/,
  );
  assert.match(homeHtml, /aria-haspopup="dialog"/);
  assert.match(homeHtml, /aria-expanded="false"/);
  assert.match(homeHtml, /data-dialog-trigger=""/);
  assert.match(homeHtml, /data-state="closed"/);
  assert.match(homeHtml, /aria-label="Open documentation navigation"/);
  assert.doesNotMatch(homeHtml, /data-slot="sheet-content"/);
});

test('renders every primary documentation route with one page heading', async () => {
  for (const path of primaryRoutes) {
    const html = await render(path);
    assert.equal(html.match(/<h1\b/g)?.length, 1, path);
    assert.match(html, /<title>[^<]+<\/title>/, path);
  }
});

test('getting started offers a published template and runnable app commands', async () => {
  const html = stripMarkup(await render('/getting-started'));
  assert.ok(
    html.includes(
      `dotnet new install Runic.Application.Templates::${publishedRelease.version}`,
    ),
  );
  assert.match(html, /dotnet new runic-app-svelte/);
  assert.match(html, /dotnet tool restore/);
  assert.match(html, /dotnet runic doctor/);
  assert.match(html, /dotnet publish -c Release/);
});

test('builds an accessible branded page for nginx 404 responses', async () => {
  const html = await render('/404');
  assert.match(html, /That rune is not in the catalog/);
  assert.match(html, /<title>Page not found · Runic Artifex<\/title>/);
  assert.match(html, /Skip to content/);
});

test('gives product scope and boundaries a semantic section heading', async () => {
  for (const path of primaryRoutes.filter((route) =>
    route.startsWith('/products/'),
  )) {
    assert.match(
      await render(path),
      /<section id="boundaries">[\s\S]*?<h2>Scope and boundaries<\/h2>/,
      path,
    );
  }
});

test('provides consistent social metadata for each public page', async () => {
  for (const path of primaryRoutes.filter((route) => route !== '/readiness')) {
    const html = await render(path);
    assert.equal(
      readMeta(html, 'property', 'og:title'),
      readMeta(html, 'name', 'twitter:title'),
      path,
    );
    assert.equal(
      readMeta(html, 'property', 'og:description'),
      readMeta(html, 'name', 'twitter:description'),
      path,
    );
  }
});

test('uses one page h1 followed by h2 product-card headings', async () => {
  const html = await render('/products');
  const headings = [
    ...html.matchAll(/<h([1-6])\b[^>]*>([\s\S]*?)<\/h\1>/g),
  ].map((match) => [Number(match[1]), stripMarkup(match[2])]);

  assert.equal(headings[0][0], 1);
  assert.ok(headings.slice(1).every(([level]) => level === 2));
  for (const name of [
    'Runic Application',
    'Runic Desktop',
    'Runic Assets',
    'Runic Translations',
    'Runic Command Line',
  ])
    assert.ok(
      headings.some(([, text]) => text === name),
      name,
    );
});

test('uses the canonical Runic Translations identifiers', async () => {
  const html = await render('/products/runic-translations');
  assert.match(html, /<h1>Runic Translations<\/h1>/);
  assert.match(html, /runic\.translations\/1/);
  assert.match(html, /Runic\.Translations\.\*/);
  assert.match(html, /Runic\.Translations\.Tooling/);
  assert.match(html, /@runic-artifex\/vite-plugin-runic-translations/);
  assert.match(html, /translations\/runic\.json/);
  assert.match(html, /m\.message_id\(\)/);
});

test('links release notes and renders published install commands', async () => {
  for (const path of primaryRoutes) {
    assert.match(
      await render(path),
      /<a href="[^"#]*releases">Release notes<\/a>/,
      path,
    );
  }
  const packageHtml = await render('/packages');
  for (const row of releaseDocs.catalogRows) {
    const command = packageInstallCommand(row);
    if (command) {
      assert.match(
        packageHtml,
        new RegExp(command.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')),
      );
    }
  }
});

test('resolves every internal route link and fragment in the prerendered site', async () => {
  const knownRoutes = new Set(primaryRoutes);
  const pages = new Map(
    await Promise.all(
      primaryRoutes.map(async (path) => [path, await render(path)]),
    ),
  );

  for (const [sourcePath, html] of pages) {
    const basePath = sourcePath === '/' ? '/' : `${sourcePath}/`;
    for (const match of html.matchAll(/<a\b[^>]*\bhref="([^"]+)"[^>]*>/g)) {
      const target = new URL(
        match[1],
        `https://docs.runic-artifex.eu${basePath}`,
      );
      if (target.origin !== 'https://docs.runic-artifex.eu') continue;
      const targetPath = target.pathname.replace(/\/$/, '') || '/';
      assert.ok(
        knownRoutes.has(targetPath),
        `${sourcePath} links to missing route ${target.pathname}`,
      );
      if (target.hash) {
        const fragment = decodeURIComponent(target.hash.slice(1));
        assert.match(
          pages.get(targetPath),
          new RegExp(
            `\\bid="${fragment.replace(/[.*+?^${}()|[\\]\\\\]/g, '\\$&')}"`,
          ),
          `${sourcePath} links to missing fragment ${targetPath}${target.hash}`,
        );
      }
    }
  }
});

test('published release is consistent across onboarding, catalog and release notes', async () => {
  for (const route of ['/packages', '/releases', '/getting-started']) {
    const html = await render(route);
    assert.ok(html.includes(publishedRelease.version), route);
    assert.doesNotMatch(
      html,
      /unpublished candidate|release authority|two-hour soak/i,
    );
  }
  for (const route of ['/releases', '/getting-started'])
    assert.ok((await render(route)).includes(publishedRelease.url), route);
});
