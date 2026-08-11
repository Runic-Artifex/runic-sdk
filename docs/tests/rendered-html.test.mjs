import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import test from 'node:test';

import { products } from '../src/lib/docs-data.ts';

const buildDirectory = fileURLToPath(new URL('../build/', import.meta.url));

const primaryRoutes = [
  '/',
  '/getting-started',
  '/products',
  '/architecture',
  '/packages',
  '/releases',
  '/products/runic-toolkit',
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
    ['/releases', 'See what you can install today'],
    ['/products/runic-toolkit', 'Runic Toolkit'],
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

test('builds an accessible branded page for nginx 404 responses', async () => {
  const html = await render('/404');
  assert.match(html, /That rune is not in the catalog/);
  assert.match(html, /<title>Page not found · Runic Artifex<\/title>/);
  assert.match(html, /Skip to content/);
});

test('presents the editor as a downstream application with its own artifacts', async () => {
  const html = await render('/products/runic-translations-editor');
  assert.match(html, /<h2>Downloads<\/h2>/);
  assert.match(html, /Linux x64 self-contained archive/);
  assert.match(html, /First preview pending/);
  assert.match(html, /The first desktop preview is pending/);
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
      'Open-source .NET tools for UI, hosting, workflows, assets, localization, and command-line applications.',
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
      'Browse Runic Artifex packages by registry, product, candidate version, and publication status.',
    ],
    [
      '/releases',
      'See what you can install today. · Runic Artifex',
      'See which Runic Artifex packages are available now and which verified preview candidates are waiting for publication.',
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
    [2, 'CS-WebUI'],
    [2, 'Runic Flow'],
    [2, 'Runic Assets'],
    [2, 'Runic Translations'],
    [2, 'Runic Translations Editor'],
    [2, 'Runic Command Line'],
  ]);
});

test('documents the headless Flow surface and package ownership', async () => {
  const flowHtml = await render('/products/runic-flow');
  assert.match(flowHtml, /deterministic application processes/i);
  assert.match(flowHtml, /RunicFlow\.ApplicationBridge/);
  assert.doesNotMatch(
    flowHtml,
    /RunicFlow\.(?:Generators|CommunityToolkit|RunicToolkit)/,
  );

  const packageHtml = await render('/packages');
  assert.match(packageHtml, /@runic-artifex\/application-bridge/);
  assert.match(packageHtml, /RunicToolkit\.ApplicationBridge\.Generators/);
  assert.match(packageHtml, /RunicTranslations\.Generator/);
  assert.match(packageHtml, /RunicTranslations\.Authoring/);
  assert.match(packageHtml, /@runic-artifex\/vite-plugin-runic-translations/);
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
    /Runic Artifex package projects, candidate versions, and current\s+availability/,
  );
  assert.match(
    releaseHtml,
    /Runic Artifex release candidates and publication order/,
  );
  assert.equal(packageHtml.match(/scope="col"/g)?.length, 4);
  assert.equal(releaseHtml.match(/scope="col"/g)?.length, 3);
});

test('keeps release status public-facing and summarizes release integrity', async () => {
  const html = await render('/releases');

  assert.match(html, /CS-WebUI is available on NuGet/);
  assert.match(html, /Verified candidate · not yet published/);
  assert.match(html, /First preview pending/);
  assert.match(html, /Verify once\. Publish the same artifacts\./);
  assert.match(html, /Expect exact versions and independent releases/);
  assert.match(
    html,
    /Candidates are built and tested from exact public source commits/,
  );
  assert.doesNotMatch(html, /Freeze exact source commits/);
  assert.doesNotMatch(html, /short-lived bootstrap token/);
});

test('shows the exact release publication order without treating the Editor as a candidate', async () => {
  const html = await render('/releases');
  const lede = html.match(/<p class="lede">([\s\S]*?)<\/p>/)?.[1];

  assert.ok(lede, 'expected the release lede');
  assert.match(
    stripMarkup(lede),
    /^CS-WebUI is available on NuGet\. Toolkit, Command Line, Translations, Assets, Flow, Svelte, and Vite have verified candidates awaiting their first registry publication\. Translations Editor’s first preview is still pending\.$/,
  );
  assert.doesNotMatch(
    stripMarkup(lede),
    /Translations Editor[^.]*verified candidate/i,
  );
  assert.match(
    html,
    /<th[^>]*scope="col">[\s\S]*?Publication order[\s\S]*?<\/th>/,
  );
  assert.deepEqual(tableRows(html), [
    ['CS-WebUI', '2.5.0-beta.4.4 Available on NuGet', 'Available now'],
    [
      'Runic Command Line',
      '0.1.0-preview.4.1 Verified candidate · not yet published',
      'Can publish independently',
    ],
    [
      'Runic Translations',
      '0.1.0-preview.8.1 Verified candidate · not yet published',
      'Can publish independently to NuGet and npm',
    ],
    [
      'Runic Translations Editor',
      'First preview pending',
      'After Runic Translations',
    ],
    [
      'Runic Svelte integrations',
      '0.1.0-preview.14.1 Verified candidate · not yet published',
      'Before Runic Toolkit',
    ],
    [
      'Runic Vite integration',
      '0.1.0-preview.8.1 Verified candidate · not yet published',
      'Before Runic Toolkit',
    ],
    [
      'Runic Toolkit',
      '0.1.0-preview.30.1 Verified candidate · not yet published',
      'After Svelte and Vite integrations',
    ],
    [
      'Runic Assets',
      '0.1.0-preview.23.1 Verified candidate · not yet published',
      'After Runic Toolkit',
    ],
    [
      'Runic Flow',
      '0.1.0-preview.18.1 Verified candidate · not yet published',
      'After Runic Toolkit',
    ],
  ]);
});

test('uses the canonical Runic Translations identifiers', async () => {
  const html = await render('/products/runic-translations');
  assert.match(html, /<h1>Runic Translations<\/h1>/);
  assert.match(html, /runic\.translations\/1/);
  assert.match(html, /RunicTranslations\.Compiler/);
  assert.match(html, /@runic-artifex\/vite-plugin-runic-translations/);
});

test('reports release readiness conservatively', async () => {
  const homeHtml = await render('/');
  const releaseHtml = await render('/releases');
  const gettingStartedHtml = await render('/getting-started');
  const bridgeHtml = await render('/application-bridge');
  assert.match(homeHtml, /<h2>The source is ready to explore\.<\/h2>/);
  assert.match(
    homeHtml,
    /The repositories are public\. CS-WebUI 2\.5\.0-beta\.4\.4 is available on\s+NuGet\. Exact preview candidates for the remaining package families have\s+passed verification but are not yet published to NuGet or npm\./,
  );
  assert.match(releaseHtml, /0\.1\.0-preview\.30\.1/);
  assert.match(releaseHtml, /0\.1\.0-preview\.8\.1/);
  assert.match(releaseHtml, /Verified candidate · not yet published/);
  assert.doesNotMatch(homeHtml, /packages? (?:are|is) public/i);
  assert.doesNotMatch(
    releaseHtml,
    /<span class="status-pill">Published<\/span>/,
  );
  assert.match(
    gettingStartedHtml,
    /The remaining package families have exact verified candidates, and their\s+source is public\. Install commands will appear after matching registry\s+artifacts are published and accepted\./,
  );
  assert.match(
    gettingStartedHtml,
    /Runic Translations Editor is a\s+separate application; its first preview is still pending\./,
  );
  assert.match(
    gettingStartedHtml,
    /dotnet add package CsWebUi --version 2\.5\.0-beta\.4\.4/,
  );
  for (const version of ['0.1.0-preview.30.1', '0.1.0-preview.14.1']) {
    assert.match(bridgeHtml, new RegExp(version.replaceAll('.', '\\.')));
  }
  const bridgeText = stripMarkup(bridgeHtml);
  assert.equal(
    (bridgeText.match(/Verified candidate · not yet published/g) ?? []).length,
    3,
  );
  assert.match(
    bridgeHtml,
    /Install commands will appear after the matching NuGet and npm\s+artifacts are published and accepted\./,
  );
  for (const html of [gettingStartedHtml, bridgeHtml]) {
    assert.doesNotMatch(html, /refreshing candidates/i);
    assert.doesNotMatch(html, /being rebuilt/i);
    assert.doesNotMatch(html, /refreshed public candidates/i);
  }
});

test('uses canonical availability vocabulary and raw exact candidate versions', async () => {
  const candidateProducts = [
    ['/products/runic-toolkit', '0.1.0-preview.30.1'],
    ['/products/runic-flow', '0.1.0-preview.18.1'],
    ['/products/runic-assets', '0.1.0-preview.23.1'],
    ['/products/runic-translations', '0.1.0-preview.8.1'],
    ['/products/runic-command-line', '0.1.0-preview.4.1'],
  ];
  const packageHtml = await render('/packages');
  const releaseHtml = await render('/releases');

  for (const [path, version] of candidateProducts) {
    const product = products.find(
      (candidate) => `/products/${candidate.slug}` === path,
    );
    assert.equal(product?.version, version, `${path} canonical version`);
    assert.doesNotMatch(product.version, /verified|published|candidate/i, path);
    const html = await render(path);
    assert.match(html, /Verified candidate · not yet published/, path);
    assert.match(
      html,
      new RegExp(`<code>${version.replaceAll('.', '\\.')}<\\/code>`),
      path,
    );
    assert.doesNotMatch(html, /verified, unpublished/i, path);
  }
  for (const version of [
    '0.1.0-preview.4.1',
    '0.1.0-preview.8.1',
    '0.1.0-preview.14.1',
    '0.1.0-preview.30.1',
    '0.1.0-preview.23.1',
    '0.1.0-preview.18.1',
  ]) {
    const exactCode = new RegExp(
      `<code>${version.replaceAll('.', '\\.')}<\\/code>`,
    );
    assert.match(packageHtml, exactCode, `package catalog ${version}`);
    assert.match(releaseHtml, exactCode, `release table ${version}`);
  }
  for (const html of [packageHtml, releaseHtml]) {
    assert.doesNotMatch(html, /verified, unpublished/i);
    assert.doesNotMatch(html, /<code>[^<]*Verified candidate/i);
  }
});

test('describes the published CS-WebUI family and registry version precisely', async () => {
  const html = await render('/products/cs-webui');

  assert.match(html, /CS-WebUI provides \.NET bindings for WebUI/);
  assert.match(html, /The raw package follows the WebUI C ABI/);
  assert.match(html, /<h2>Install CS-WebUI<\/h2>/);
  assert.match(html, /Available on NuGet/);
  assert.match(html, /Version 2\.5\.0-beta\.4\.4 is available on NuGet/);
  assert.match(
    html,
    /Registry version[\s\S]*?<code>2\.5\.0-beta\.4\.4<\/code>/,
  );
  assert.match(html, /<code>CsWebUi\.Native<\/code>/);
  assert.match(html, /<code>CsWebUi<\/code>/);
});

test('keeps the footer release-status label and withholds unpublished install commands', async () => {
  const installCommand =
    /(?:dotnet add package|npm (?:install|i)|pnpm add|yarn add|bun add)/i;
  const unpublishedRoutes = [
    '/',
    '/products',
    '/architecture',
    '/application-bridge',
    '/packages',
    '/releases',
    '/products/runic-toolkit',
    '/products/runic-flow',
    '/products/runic-assets',
    '/products/runic-translations',
    '/products/runic-translations-editor',
    '/products/runic-command-line',
  ];

  for (const path of primaryRoutes) {
    assert.match(
      await render(path),
      /<a href="[^"#]*releases">Release status<\/a>/,
      path,
    );
  }
  for (const path of unpublishedRoutes) {
    assert.doesNotMatch(await render(path), installCommand, path);
  }
  assert.match(
    await render('/getting-started'),
    /dotnet add package CsWebUi --version 2\.5\.0-beta\.4\.4/,
  );
  assert.match(
    await render('/products/cs-webui'),
    /dotnet add package CsWebUi --version 2\.5\.0-beta\.4\.4/,
  );
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
