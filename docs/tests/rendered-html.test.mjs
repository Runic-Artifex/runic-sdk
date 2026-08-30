import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import test from 'node:test';

import { releaseData } from '../src/lib/generated/release-data.ts';
import {
  createReleaseDocs,
  packageInstallCommand,
  versionLabel,
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
  '/products/runic-flow',
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

function tableRows(html) {
  const body = html.match(/<tbody\b[\s\S]*?<\/tbody>/)?.[0];
  assert.ok(body, 'expected a table body');
  return [...body.matchAll(/<tr\b[\s\S]*?<\/tr>/g)].map((row) =>
    [...row[0].matchAll(/<td\b[\s\S]*?<\/td>/g)].map((cell) =>
      stripMarkup(cell[0]),
    ),
  );
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
  assert.match(html, /Runic Toolkit/);
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
    ['/products', 'Seven products, each with a clear job'],
    ['/architecture', 'Use products independently'],
    ['/packages', 'Find packages by product and registry'],
    ['/releases', 'See assigned release versions'],
    ['/readiness', 'Historical W80 evidence'],
    ['/products/runic-toolkit', 'Runic Toolkit'],
    ['/products/runic-desktop', 'Runic Desktop'],
    ['/application-bridge', 'Connect a frontend to .NET'],
    ['/products/runic-flow', 'Runic Flow'],
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

test('renders the local unsigned readiness boundary without a release or updater promise', async () => {
  const html = await render('/readiness');
  assert.match(html, /Local readiness evidence/);
  assert.match(html, /Historical W80 evidence, not the expanded v1 verdict/);
  assert.match(html, /expanded\s+W110 index remains pending/);
  assert.match(html, /private-file streaming and WebView/);
  assert.match(
    html,
    /source, translation, review, session, cookie, and token content/,
  );
  assert.match(html, /does not update, download, install, or roll back/);
  assert.doesNotMatch(html, /Published distribution/);
});

test('renders the authority-derived Desktop choose-your-path matrix', async () => {
  const html = await render('/getting-started');
  assert.match(
    html,
    /Authority-derived paths for starting a Runic Desktop application/,
  );
  assert.match(html, /Runic\.Application\.Templates@1\.0\.0-preview\.1/);
  assert.match(html, /@runic-artifex\/desktop@1\.0\.0-preview\.1/);
  assert.match(html, /\.NET SDK 10\.0\.302, Node 24\.18\.0, npm 11\.16\.0/);
  assert.match(html, /runic-toolkit-examples/);
});

test('builds an accessible branded page for nginx 404 responses', async () => {
  const html = await render('/404');
  assert.match(html, /That rune is not in the catalog/);
  assert.match(html, /<title>Page not found · Runic Artifex<\/title>/);
  assert.match(html, /Skip to content/);
});

test('presents the editor archive as historical distribution evidence', async () => {
  const html = await render('/products/runic-translations-editor');
  const editor = releaseData.distributions.find(
    (distribution) => distribution.identity === 'Runic.Translations.Editor',
  );
  assert.match(html, /<h2>Distribution history<\/h2>/);
  assert.match(html, /Runic\.Translations\.Editor/);
  assert.match(html, /Historical archive distribution:/);
  if (editor?.version.state === 'published') {
    assert.match(
      html,
      new RegExp(`Published historical distribution: ${editor.version.value}`),
    );
  } else {
    assert.match(html, /Historical distribution version unassigned/);
    assert.match(stripMarkup(html), /does not indicate current availability/);
  }
  assert.match(html, /Does not own the compiler, schemas, runtime ABI/);
  assert.doesNotMatch(html, /\bsigned\b/i);
  assert.doesNotMatch(html, /First release pending/);
  assert.doesNotMatch(html, /Prepare to install/);
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
      'Runic Toolkit · Runic Artifex',
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
    [1, 'Seven products, each with a clear job.'],
    [2, 'Runic Toolkit'],
    [2, 'Runic Desktop'],
    [2, 'CS-WebUI'],
    [2, 'Runic Assets'],
    [2, 'Runic Translations'],
    [2, 'Runic Translations Editor'],
    [2, 'Runic Command Line'],
  ]);
});

test('keeps Flow only as an archived migration record', async () => {
  const flowHtml = await render('/products/runic-flow');
  assert.match(flowHtml, /Archived — no release-bearing packages/);
  assert.match(
    stripMarkup(flowHtml),
    /historical records retain its former identities/i,
  );
  assert.match(flowHtml, /0002-v02-operations-probation-archive.md/);
  assert.doesNotMatch(flowHtml, /<h2>Packages<\/h2>/);
  assert.doesNotMatch(flowHtml, /Install:/);

  const packageHtml = await render('/packages');
  const canonicalRows = tableRows(packageHtml);
  assert.match(packageHtml, /@runic-artifex\/application-bridge/);
  assert.match(packageHtml, /Runic\.Application\.Testing/);
  assert.match(packageHtml, /Runic\.CommandLine\.Testing/);
  assert.ok(canonicalRows.every((row) => !row[0].startsWith('RunicToolkit')));
  assert.match(packageHtml, /@runic-artifex\/vite-plugin-runic-translations/);
  assert.doesNotMatch(packageHtml, /RunicToolkit\./);
  assert.doesNotMatch(packageHtml, /RunicTranslations\.Generator/);
  assert.doesNotMatch(packageHtml, /RunicAssets\.RunicToolkit/);
  assert.doesNotMatch(packageHtml, /RunicFlow/);
  assert.doesNotMatch(packageHtml, /Runic\.Operations/);
  assert.match(packageHtml, /Historical migrations stay outside/);
});

test('renders package and release tables with captions, scoped heads, and overflow containment', async () => {
  const packageHtml = await render('/packages');
  const releaseHtml = await render('/releases');

  for (const html of [packageHtml, releaseHtml]) {
    assert.match(
      html,
      /data-slot="table-container" class="relative w-full overflow-x-auto"/,
    );
    assert.match(html, /data-slot="table-caption"/);
    assert.match(html, /<th[^>]*scope="col">/);
  }
  assert.match(
    packageHtml,
    /Runic Artifex canonical package identities and release versions/,
  );
  assert.match(releaseHtml, /Runic Artifex current release-train versions/);
  assert.equal(packageHtml.match(/scope="col"/g)?.length, 5);
  assert.equal(releaseHtml.match(/scope="col"/g)?.length, 8);
});

test('renders release, compatibility, and distribution data from the authority', async () => {
  const html = await render('/releases');

  const lede = html.match(/<p class="lede">([\s\S]*?)<\/p>/)?.[1];

  assert.ok(lede, 'expected the release lede');
  assert.match(stripMarkup(lede), /release authority/);
  const rows = tableRows(html);
  const expectedRows = releaseData.compatibilityTrains.flatMap((train) =>
    train.lanes
      .filter((lane) => lane.name === 'current')
      .flatMap((lane) => lane.versions),
  );
  assert.equal(rows.length, expectedRows.length);
  for (const entry of expectedRows) {
    assert.ok(
      rows.some((row) => row.includes(versionLabel(entry.version))),
      `expected ${entry.product} version in release table`,
    );
  }
  assert.match(html, /Compatibility lanes are generated from the authority/);
  assert.match(html, /Runic\.Translations\.Editor/);
  assert.match(html, /dotnet runic/);
  assert.match(html, /typescript-effect/);
  assert.match(html, /rust/);
  assert.match(html, /no package or support claim is made/);
});

test('uses the canonical Runic Translations identifiers', async () => {
  const html = await render('/products/runic-translations');
  assert.match(html, /<h1>Runic Translations<\/h1>/);
  assert.match(html, /runic\.translations\/1/);
  assert.match(html, /Runic\.Translations\.\*/);
  assert.match(html, /Runic\.Translations\.Tooling/);
  assert.match(html, /@runic-artifex\/vite-plugin-runic-translations/);
});

test('renders availability from release authority records', async () => {
  const homeHtml = await render('/');
  const releaseHtml = await render('/releases');
  const gettingStartedHtml = await render('/getting-started');
  const bridgeHtml = await render('/application-bridge');
  assert.match(homeHtml, /<h2>Track the release authority\.<\/h2>/);
  assert.match(homeHtml, /release authority/);
  assert.match(gettingStartedHtml, /archive status is recorded independently/);
  const bridgeVersion = releaseData.compatibilityTrains
    .flatMap((train) => train.lanes)
    .find((lane) => lane.name === 'current')
    ?.versions.find((entry) => entry.product === 'application')?.version;
  assert.match(bridgeHtml, new RegExp(versionLabel(bridgeVersion)));
  if (bridgeVersion?.state === 'unassigned') {
    assert.match(releaseHtml, /Pending release — version unassigned/);
    assert.match(bridgeHtml, /Release versions are currently unassigned/);
  } else {
    assert.match(releaseHtml, /Published/);
  }
});

test('renders product release labels from their authority-selected active lanes', async () => {
  const productGuides = [
    ['/products/runic-toolkit', 'application'],
    ['/products/runic-desktop', 'desktop'],
    ['/products/runic-assets', 'assets'],
    ['/products/runic-translations', 'translations'],
    ['/products/runic-command-line', 'command-line'],
  ];

  for (const [path, product] of productGuides) {
    const html = await render(path);
    assert.match(
      html,
      new RegExp(versionLabel(releaseDocs.activeVersionForProduct(product))),
      path,
    );
  }
});

test('describes CS-WebUI only as an independent upstream compatibility product', async () => {
  const html = await render('/products/cs-webui');

  assert.match(html, /tracks unmodified upstream WebUI/);
  assert.match(html, /complete WebUI 2\.5 C ABI/);
  assert.match(html, /Maintained outside the Runic v1 train/);
  assert.match(html, /not governed by the Runic v1 compatibility set/);
  assert.match(html, /Is not the implementation underneath Runic Desktop/);
  assert.doesNotMatch(html, /Release-train version/);
  assert.doesNotMatch(html, /<h2>Packages<\/h2>/);
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
