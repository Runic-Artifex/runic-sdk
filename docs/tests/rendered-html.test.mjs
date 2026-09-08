import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import test from 'node:test';

import { releaseData } from '../src/lib/generated/release-data.ts';
import {
  createReleaseDocs,
  packageInstallCommand,
} from '../src/lib/release-docs-core.ts';

const buildDirectory = fileURLToPath(new URL('../build/', import.meta.url));
const releaseDocs = createReleaseDocs(releaseData);

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
  assert.match(html, /<h1>Build with only the tools you need\.<\/h1>/);
  assert.match(
    html,
    /<title>Open-source \.NET tools that work independently · Runic Artifex<\/title>/,
  );
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
  assert.match(html, /The map of independent tools and explicit seams\./);
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

test('renders every primary documentation route', async () => {
  const routes = [
    ['/getting-started', 'Start from what you’re building'],
    ['/products', 'Products with clear boundaries'],
    ['/architecture', 'Use products independently'],
    ['/packages', 'Find packages by product and registry'],
    ['/releases', 'See assigned release versions'],
    ['/readiness', 'Verify the candidate before publishing'],
    ['/products/runic-toolkit', 'Runic Application'],
    ['/products/runic-desktop', 'Runic Desktop'],
    ['/application-bridge', 'Connect a frontend to .NET'],
    ['/products/runic-assets', 'Runic Assets'],
    ['/products/runic-translations', 'Runic Translations'],
    ['/products/runic-translations-editor', 'Runic Translations Editor'],
    ['/products/runic-command-line', 'Runic Command Line'],
    ['/products/cs-webui', 'CS-WebUI'],
  ];

  for (const [path, expected] of routes) {
    assert.match(await render(path), new RegExp(expected), path);
  }
});

test('renders the authority-derived Desktop choose-your-path matrix', async () => {
  const html = await render('/getting-started');
  assert.match(
    html,
    /Authority-derived paths for starting a Runic Desktop application/,
  );
  assert.match(html, /Runic\.Application\.Templates@0\.2\.0-preview\.1/);
  assert.match(html, /@runic-artifex\/desktop@0\.2\.0-preview\.1/);
  assert.match(html, /\.NET SDK 10\.0\.400; Bun 1\.4\.2/);
  assert.match(html, /packageManager/);
  assert.match(html, /static frontend/);
  assert.match(html, /runic-sdk\/examples/);
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

test('keeps route-specific Open Graph and Twitter copy', async () => {
  const routes = [
    [
      '/',
      'Open-source .NET tools that work independently · Runic Artifex',
      'Open-source .NET tools for desktop and browser UI, application hosting, assets, localization, and command-line applications.',
    ],
    [
      '/getting-started',
      'Getting started · Runic Artifex',
      'Choose the focused Runic Artifex product that solves your next application problem.',
    ],
    [
      '/products',
      'Products · Runic Artifex',
      'Choose the independent Runic Artifex product that owns the capability you need.',
    ],
    [
      '/products/runic-toolkit',
      'Runic Application · Runic Artifex',
      'Compose desktop windows, browser frontends, and .NET hosting around one application model with NativeAOT-safe application contracts.',
    ],
    [
      '/architecture',
      'Architecture · Runic Artifex',
      'How Runic products stay useful on their own while official integrations let you connect only the pieces your project needs.',
    ],
    [
      '/application-bridge',
      'Application Bridge · Runic Artifex',
      'Connect browser frontends to NativeAOT-safe .NET hosts with explicit commands, validated events, and generated contracts.',
    ],
    [
      '/packages',
      'Find packages by product and registry · Runic Artifex',
      'Browse Runic Artifex packages by registry, product, current version, and public availability.',
    ],
    [
      '/releases',
      'See assigned release versions · Runic Artifex',
      'See the release train, compatibility lanes, package migration status, and explicitly assigned versions.',
    ],
  ];

  for (const [path, title, description] of routes) {
    const html = await render(path);
    assert.equal(readMeta(html, 'property', 'og:title'), title, path);
    assert.equal(
      readMeta(html, 'property', 'og:description'),
      description,
      path,
    );
    assert.equal(readMeta(html, 'name', 'twitter:title'), title, path);
    assert.equal(
      readMeta(html, 'name', 'twitter:description'),
      description,
      path,
    );
  }
});

test('uses one page h1 followed by h2 product-card headings', async () => {
  const html = await render('/products');
  const headings = [
    ...html.matchAll(/<h([1-6])\b[^>]*>([\s\S]*?)<\/h\1>/g),
  ].map((match) => [Number(match[1]), stripMarkup(match[2])]);

  assert.deepEqual(headings, [
    [1, 'Products with clear boundaries.'],
    [2, 'Runic Application'],
    [2, 'Runic Desktop'],
    [2, 'CS-WebUI'],
    [2, 'Runic Assets'],
    [2, 'Runic Translations'],
    [2, 'Runic Translations Editor'],
    [2, 'Runic Command Line'],
  ]);
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
  assert.match(html, /language server is planned for 2\.0/);
});

test('keeps the footer release-status label and renders only authority-backed install commands', async () => {
  for (const path of primaryRoutes) {
    assert.match(
      await render(path),
      /<a href="[^"#]*releases">Release status<\/a>/,
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

test('current preview is discoverable and explicitly unpublished', async () => {
  for (const route of ['/packages', '/releases', '/getting-started']) {
    const html = await render(route);
    assert.match(html, /0\.2\.0-preview\.1/);
    assert.match(html, /unpublished/);
    assert.match(html, /guides\/releases\/0\.2\.0-preview\.1\.md/);
    assert.match(html, /outside this (?:SDK )?preview/);
  }
  const catalog = await render('/packages');
  for (const entry of releaseData.currentCandidate.packages) {
    assert.ok(
      catalog.includes(`${entry.identity}@${entry.version}`),
      entry.identity,
    );
  }
  const started = await render('/getting-started');
  assert.doesNotMatch(started, /Products release independently/);
  assert.doesNotMatch(started, /1\.0\.0-preview\.1/);
});

test('home and architecture describe the current monorepo preview boundary', async () => {
  for (const route of ['/', '/architecture']) {
    const html = stripMarkup(await render(route)).replace(/\s+/g, ' ');
    assert.match(html, /monorepo/);
    assert.match(
      html,
      /Standalone Translations Editor distributions are outside this preview/,
    );
    assert.doesNotMatch(
      html,
      /Each product has its own repository|archives from its own repository/,
    );
  }
});
