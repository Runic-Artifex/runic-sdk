import assert from 'node:assert/strict';
import test from 'node:test';
import { Server } from '../.svelte-kit/output/server/index.js';
import { manifest } from '../.svelte-kit/output/server/manifest-full.js';

const server = new Server(manifest);
await server.init({ env: {} });

function render(path = '/') {
  return server.respond(
    new Request(`http://localhost${path}`, {
      headers: { accept: 'text/html' },
    }),
    {
      getClientAddress: () => '127.0.0.1',
    },
  );
}

test('renders the documentation home with complete metadata and branding', async () => {
  const response = await render();
  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-type') ?? '', /^text\/html\b/i);
  const html = await response.text();
  assert.match(html, /Build one application model across \.NET surfaces/);
  assert.match(
    html,
    /<title>Build one application model across \.NET surfaces · Runic Artifex<\/title>/,
  );
  assert.match(html, /<small>Documentation<\/small>/);
  assert.match(
    html,
    /property="og:image" content="http:\/\/localhost\/og\.png"/,
  );
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
    const response = await render(path);
    assert.equal(response.status, 200, path);
    assert.match(await response.text(), new RegExp(expected), path);
  }
});

test('renders an accessible branded not-found page', async () => {
  const response = await render('/missing-rune');
  assert.equal(response.status, 404);
  const html = await response.text();
  assert.match(html, /That rune is not in the catalog/);
  assert.match(html, /<title>Page not found · Runic Artifex<\/title>/);
  assert.match(html, /Skip to content/);
});

test('presents the editor as a downstream application with its own artifacts', async () => {
  const html = await (
    await render('/products/runic-translations-editor')
  ).text();
  assert.match(html, /Release artifacts/);
  assert.match(html, /Linux x64 self-contained archive/);
  assert.match(html, /First release pending/);
  assert.match(html, /Does not own the compiler, schemas, runtime ABI/);
  assert.doesNotMatch(html, /Prepare to install/);
});

test('documents the headless Flow surface and package ownership', async () => {
  const flowHtml = await (await render('/products/runic-flow')).text();
  assert.match(flowHtml, /deterministic application processes/i);
  assert.match(flowHtml, /RunicFlow\.ApplicationBridge/);
  assert.doesNotMatch(
    flowHtml,
    /RunicFlow\.(?:Generators|CommunityToolkit|RunicToolkit)/,
  );

  const packageHtml = await (await render('/packages')).text();
  assert.match(packageHtml, /@runic-artifex\/application-bridge/);
  assert.match(packageHtml, /RunicToolkit\.ApplicationBridge\.Generators/);
  assert.match(packageHtml, /RunicTranslations\.Generator/);
  assert.match(packageHtml, /RunicTranslations\.Authoring/);
  assert.match(packageHtml, /@runic-artifex\/vite-plugin-runic-translations/);
});

test('uses the canonical Runic Translations identifiers', async () => {
  const html = await (await render('/products/runic-translations')).text();
  assert.match(html, /<h1>Runic Translations<\/h1>/);
  assert.match(html, /runic\.translations\/1/);
  assert.match(html, /RunicTranslations\.Compiler/);
  assert.match(html, /@runic-artifex\/vite-plugin-runic-translations/);
  assert.doesNotMatch(html, /Runic Text Resources/);
});

test('reports release readiness conservatively', async () => {
  const homeHtml = await (await render('/')).text();
  const releaseHtml = await (await render('/releases')).text();
  assert.match(homeHtml, /package candidates are still being\s+refreshed/i);
  assert.match(releaseHtml, /First headless candidate required/);
  assert.match(releaseHtml, /Candidate refresh required/);
  assert.doesNotMatch(homeHtml, /ready for launch/i);
  assert.doesNotMatch(releaseHtml, /<span class="status-pill">Ready<\/span>/);
});
