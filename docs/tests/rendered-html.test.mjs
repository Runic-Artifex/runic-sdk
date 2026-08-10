import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import test from 'node:test';

const buildDirectory = fileURLToPath(new URL('../build/', import.meta.url));

function render(path = '/') {
  const relativePath =
    path === '/' ? 'index.html' : `${path.slice(1)}/index.html`;
  return readFile(join(buildDirectory, relativePath), 'utf8');
}

test('renders the documentation home with complete metadata and branding', async () => {
  const html = await render();
  assert.match(html, /Build one application model across \.NET surfaces/);
  assert.match(
    html,
    /<title>Build one application model across \.NET surfaces · Runic Artifex<\/title>/,
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
  assert.match(
    html,
    /background-image:\s*url\(\/products\/runic-toolkit\.png\)/,
  );
  assert.match(html, /Runic Toolkit/);
  assert.match(html, /CsWebUi/);
  assert.doesNotMatch(
    html,
    /codex-preview|SkeletonPreview|Your site is taking shape/,
  );
});

test('renders every primary documentation route', async () => {
  const routes = [
    ['/getting-started', 'Begin at the capability boundary'],
    ['/products', 'Independent products'],
    ['/architecture', 'Ownership and dependency direction'],
    ['/packages', 'One owner for every public package'],
    ['/releases', 'Verify once'],
    ['/products/runic-toolkit', 'Runic Toolkit'],
    ['/application-bridge', 'Named application concepts'],
    ['/products/runic-flow', 'Runic Flow'],
    ['/products/runic-assets', 'Runic Assets'],
    ['/products/runic-translations', 'Runic Translations'],
    ['/products/runic-translations-editor', 'Runic Translations Editor'],
    ['/products/runic-command-line', 'Runic Command Line'],
    ['/products/cs-webui', 'CsWebUi'],
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
  assert.match(html, /Release artifacts/);
  assert.match(html, /Linux x64 self-contained archive/);
  assert.match(html, /First release pending/);
  assert.match(html, /Does not own the compiler, schemas, runtime ABI/);
  assert.doesNotMatch(html, /Prepare to install/);
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
  assert.match(homeHtml, /The source is public/i);
  assert.match(homeHtml, /registry publication remains gated/i);
  assert.match(releaseHtml, /0\.1\.0-preview\.22\.1 · verified, unpublished/);
  assert.match(releaseHtml, /0\.1\.0-preview\.4\.3 · verified, unpublished/);
  assert.doesNotMatch(homeHtml, /packages? (?:are|is) public/i);
  assert.doesNotMatch(
    releaseHtml,
    /<span class="status-pill">Published<\/span>/,
  );
});
