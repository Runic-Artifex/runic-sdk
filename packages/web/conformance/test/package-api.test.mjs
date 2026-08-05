import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const packageRoot = new URL("../", import.meta.url);
const expectedRuntimeExports = [
  "CONFORMANCE_FORMAT",
  "assertFixturePath",
  "createFetchFixtureSource",
  "createFixtureSource",
  "createReport",
  "createSdkConformanceRuntime",
  "joinFixturePath",
  "loadFixtureJson",
  "loadFixtureManifest",
  "readFixtureBytes",
  "readFixtureText",
  "runConformance",
  "runHostileInputCorpus",
  "runProtocolCorpus",
  "runScenarioCorpus",
  "runSemanticCorpus",
  "splitTopLevelArray",
  "validateProtocolManifest",
  "validateScenarioDocument",
];

test("ESM exposes the documented package-root API", async () => {
  const esm = await import("../dist/esm/index.js");
  assert.deepEqual(Object.keys(esm).sort(), expectedRuntimeExports);
  assert.equal("default" in esm, false);
  for (const name of expectedRuntimeExports) assert.notEqual(typeof esm[name], "undefined", name);
});

test("package metadata points exports and types at emitted public roots", async () => {
  const metadata = JSON.parse(await readFile(new URL("package.json", packageRoot), "utf8"));
  assert.equal(metadata.name, "@runic-artifex/mvvm-conformance");
  assert.equal(metadata.type, "module");
  assert.equal(metadata.sideEffects, false);
  assert.deepEqual(metadata.exports["."], {
    types: "./dist/esm/index.d.ts",
    import: "./dist/esm/index.js",
    default: "./dist/esm/index.js",
  });
  assert.match(await readFile(new URL("dist/esm/index.d.ts", packageRoot), "utf8"), /runConformance/);
});

test("browser ESM artifacts contain no Node runtime dependencies or globals", async () => {
  for (const file of ["index.js", "fixtures.js", "runner.js", "sdk-runtime.js", "types.js"]) {
    const source = await readFile(new URL(`dist/esm/${file}`, packageRoot), "utf8");
    assert.doesNotMatch(source, /(?:from|import\s*)[ (]*["']node:/u, file);
    assert.doesNotMatch(source, /\b(?:process|Buffer|__dirname|__filename)\b/u, file);
  }

  const originalProcess = globalThis.process;
  const originalBuffer = globalThis.Buffer;
  try {
    globalThis.process = undefined;
    globalThis.Buffer = undefined;
    const api = await import("../dist/esm/index.js?browser-smoke=1");
    const fixture = api.createFixtureSource(() => "{}");
    assert.equal(await api.readFixtureText(fixture, "case.json"), "{}");
  } finally {
    globalThis.process = originalProcess;
    globalThis.Buffer = originalBuffer;
  }
});
