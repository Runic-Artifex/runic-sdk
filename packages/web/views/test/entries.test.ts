import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";

// The package's entries are its public API (W319-003 D02, D07, D35). Each test imports through
// the exports map, as a consumer does.
const manifest = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as {
  exports: Record<string, string | Record<string, string>>;
};

test("every entry resolves for types, the development build, import and require-style default", () => {
  for (const [entry, target] of Object.entries(manifest.exports)) {
    if (entry === "./package.json") continue;
    assert.equal(typeof target, "object", entry);
    const conditions = target as Record<string, string>;
    // `development` before `import` and `default`, which resolve the production build; one set of declarations.
    assert.deepEqual(Object.keys(conditions), ["types", "development", "import", "default"], `${entry} conditions`);
    assert.equal(conditions["default"], conditions["import"], `${entry}: default is the ESM file`);
    assert.equal(conditions["development"], conditions["default"]!.replace("./dist/", "./dist/development/"), `${entry}: development build`);
    assert.equal(conditions["types"], conditions["default"]!.replace(/\.js$/, ".d.ts"), `${entry}: types`);
  }
  assert.deepEqual(Object.keys(manifest.exports),
    [".", "./bindings", "./generated", "./generated/wire", "./generated/mock", "./mock", "./experimental", "./package.json"]);
});

test("binding and generator APIs are not in the application entries; graduated APIs are", async () => {
  const root = await import("@runic-artifex/views");
  for (const name of ["viewSourceIdentity", "isViewClient"])
    assert.equal(name in root, false, `${name} must not be exported from the root entry`);
  // Graduated from ./experimental in 0.10 (docs/experimental.md).
  for (const name of ["collectionViewport", "createCollectionViewportController", "createLatestOperationController"])
    assert.equal(typeof (root as Record<string, unknown>)[name], "function", `${name} is a root export`);
  // Hosted regions (0.18), the close drain's flag (0.19) and trusted component mode (0.18, moved from ./component in
  // 0.21) are experimental: only ./experimental exports them.
  const experimental = await import("@runic-artifex/views/experimental");
  assert.deepEqual(Object.keys(experimental).sort(),
    ["connectComponentBridge", "defineHostedRegion", "defineRunicComponent", "hostedRegionTag", "onBridgeDraining"]);
  for (const name of Object.keys(experimental)) assert.equal(name in root, false, `${name} must not be exported from the root entry`);
  const bindings = await import("@runic-artifex/views/bindings");
  assert.deepEqual(Object.keys(bindings).sort(), [
    "checkKeepAlive", "defaultKeepAlive", "isViewClient", "missingViewMessage", "reportMissingView", "translationsTextRevision", "viewSourceIdentity",
    "viewSourcePreview",
  ]);

  const mock = await import("@runic-artifex/views/mock");
  for (const name of ["mockBridgeInternals", "mockFailureOf", "mockTypedView"])
    assert.equal(name in mock, false, `${name} must not be exported from ./mock`);
  assert.deepEqual(Object.keys(mock).sort(), ["createMockBridge", "installMockBridge", "installMockMenuBar", "mockBlob", "mockFailure", "releaseKeptViews"]);
  // Generated clients add Symbol.dispose through ./generated, not the application entry (0.11).
  const generated = await import("@runic-artifex/views/generated");
  assert.equal(typeof generated.disposable, "function");
  assert.equal("disposable" in root, false, "disposable must not be exported from the root entry");
  const generatedMock = await import("@runic-artifex/views/generated/mock");
  assert.deepEqual(Object.keys(generatedMock), ["mockTypedView"]);
});

test("the declarations of ./mock do not expose the mock's internal state", () => {
  for (const file of ["mock.d.ts", "mock-bridge.d.ts", "mock-typed.d.ts"]) {
    const declarations = readFileSync(new URL(`../dist/${file}`, import.meta.url), "utf8");
    for (const name of ["mockBridgeInternals", "MockBridgeInternals", "mockFailureOf"])
      assert.equal(declarations.includes(name), false, `${file} declares ${name}`);
  }
});

test("no example or test script bundles the source of this package, which only scripts/build.mjs can build", () => {
  // The source references __RUNIC_DEVELOPMENT__ (src/development.d.ts), which an esbuild or Bun bundle of src/ leaves
  // undefined, so such a page throws a ReferenceError (#958). Scripts bundle dist/, the package's own build.
  const repository = new URL("../../../../", import.meta.url);
  const scripts = ["examples", "tests"].flatMap(folder =>
    readdirSync(new URL(folder, repository), { recursive: true, withFileTypes: false }).map(name => `${folder}/${String(name)}`))
    .filter(name => /\.(?:mjs|js|ts)$/.test(name) && !name.includes("node_modules") && !/\/(?:dist|bin|obj)\//.test(name));
  const offenders = scripts.filter(name => /packages\/web\/views\/src\/[\w-]+\.ts/.test(readFileSync(new URL(name, repository), "utf8")));
  assert.deepEqual(offenders, []);
});
