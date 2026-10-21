import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

// The package's entries are its public API (W319-003 D02, D07, D35). Each test imports through
// the exports map, as a consumer does.
const manifest = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as {
  exports: Record<string, string | Record<string, string>>;
};

test("every entry resolves for import, require-style default and types", () => {
  for (const [entry, target] of Object.entries(manifest.exports)) {
    if (entry === "./package.json") continue;
    assert.equal(typeof target, "object", entry);
    const conditions = target as Record<string, string>;
    assert.deepEqual(Object.keys(conditions), ["types", "import", "default"], `${entry} conditions`);
    assert.equal(conditions["default"], conditions["import"], `${entry}: default is the ESM file`);
  }
  assert.deepEqual(Object.keys(manifest.exports),
    [".", "./bindings", "./generated", "./generated/wire", "./generated/mock", "./mock", "./package.json"]);
});

test("binding and generator APIs are not in the application entries; graduated APIs are", async () => {
  const root = await import("@runic-artifex/views");
  for (const name of ["viewSourceIdentity", "isViewClient"])
    assert.equal(name in root, false, `${name} must not be exported from the root entry`);
  // Graduated from ./experimental in 0.10, which removed that entry (docs/experimental.md).
  for (const name of ["collectionViewport", "createCollectionViewportController", "createLatestOperationController"])
    assert.equal(typeof (root as Record<string, unknown>)[name], "function", `${name} is a root export`);
  // Node reports ERR_PACKAGE_PATH_NOT_EXPORTED and Bun "Cannot find module"; the exports test pins the map.
  await assert.rejects(import(["@runic-artifex/views", "experimental"].join("/")));
  const bindings = await import("@runic-artifex/views/bindings");
  assert.deepEqual(Object.keys(bindings).sort(), ["isViewClient", "viewSourceIdentity"]);

  const mock = await import("@runic-artifex/views/mock");
  for (const name of ["mockBridgeInternals", "mockFailureOf", "mockTypedView"])
    assert.equal(name in mock, false, `${name} must not be exported from ./mock`);
  assert.deepEqual(Object.keys(mock).sort(), ["createMockBridge", "installMockBridge", "mockFailure"]);
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
