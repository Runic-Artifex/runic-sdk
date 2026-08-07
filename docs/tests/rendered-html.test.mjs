import assert from "node:assert/strict";
import test from "node:test";

const workerUrl = new URL("../dist/server/index.js", import.meta.url);
workerUrl.searchParams.set("test", `${process.pid}-${Date.now()}`);
const { default: worker } = await import(workerUrl.href);

async function render(path = "/") {
  return worker.fetch(
    new Request(`http://localhost${path}`, { headers: { accept: "text/html" } }),
    { ASSETS: { fetch: async () => new Response("Not found", { status: 404 }) } },
    { waitUntil() {}, passThroughOnException() {} },
  );
}

test("renders the documentation home without starter metadata", async () => {
  const response = await render();
  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-type") ?? "", /^text\/html\b/i);
  const html = await response.text();
  assert.match(html, /Build one application model across \.NET surfaces/);
  assert.match(html, /<title>Build one application model across \.NET surfaces · Runic Artifex<\/title>/);
  assert.match(html, /<small>Documentation<\/small>/);
  assert.match(html, /property="og:image" content="http:\/\/localhost:3000\/og\.png"/);
  assert.match(html, /name="twitter:card" content="summary_large_image"/);
  assert.match(html, /Runic Toolkit/);
  assert.match(html, /CsWebUi/);
  assert.doesNotMatch(html, /codex-preview|SkeletonPreview|Your site is taking shape/);
});

test("renders every primary documentation route", async () => {
  const routes = [
    ["/getting-started", "Begin at the capability boundary"],
    ["/products", "Independent products"],
    ["/architecture", "Ownership and dependency direction"],
    ["/packages", "One owner for every public package"],
    ["/releases", "Verify once"],
    ["/products/runic-toolkit", "Runic Toolkit"],
    ["/application-bridge", "Named application concepts"],
    ["/products/runic-flow", "Runic Flow"],
    ["/products/runic-assets", "Runic Assets"],
    ["/products/runic-text-resources", "Runic Text Resources"],
    ["/products/runic-command-line", "Runic Command Line"],
    ["/products/cs-webui", "CsWebUi"],
  ];

  for (const [path, expected] of routes) {
    const response = await render(path);
    assert.equal(response.status, 200, path);
    assert.match(await response.text(), new RegExp(expected), path);
  }
});

test("documents the exact first preview train and package ownership", async () => {
  const releaseHtml = await (await render("/releases")).text();
  assert.match(releaseHtml, /0\.1\.0-preview\.21\.1/);
  assert.match(releaseHtml, /0\.1\.0-preview\.16\.1/);
  assert.match(releaseHtml, /2\.5\.0-beta\.4\.4/);
  assert.match(releaseHtml, /After Runic Toolkit/);
  assert.match(releaseHtml, /documentation gate/i);

  const packageHtml = await (await render("/packages")).text();
  assert.match(packageHtml, /RunicFlow\.RunicToolkit/);
  assert.doesNotMatch(packageHtml, /@runic-artifex\/mvvm/);
  assert.match(packageHtml, /@runic-artifex\/application-bridge/);
  assert.match(packageHtml, /RunicToolkit\.ApplicationBridge\.Generators/);
  assert.match(packageHtml, /RunicTextResources\.Generator/);
});
