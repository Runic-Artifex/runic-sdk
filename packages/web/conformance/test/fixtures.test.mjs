import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import {
  createFetchFixtureSource,
  createFixtureSource,
  joinFixturePath,
  loadFixtureManifest,
  readFixtureBytes,
  readFixtureText,
  splitTopLevelArray,
  validateProtocolManifest,
  validateScenarioDocument,
} from "../dist/esm/index.js";

const fixtureRoot = new URL("../../../fixtures/conformance/", import.meta.url);
const upstreamRoot = new URL("../../../../protocol/mvvm/corpus/v1/", import.meta.url);

const readJson = async (url) => JSON.parse(await readFile(url, "utf8"));
const fixtureSource = createFixtureSource((path) => readFile(new URL(path, fixtureRoot)));

test("the fixture entry point has stable complete totals", async () => {
  const manifest = await readJson(new URL("manifest.json", fixtureRoot));
  assert.equal(manifest.formatVersion, 1);
  assert.equal(manifest.protocolIdentity, "runic.toolkit.mvvm/1");
  assert.deepEqual(manifest.totals, {
    cases: 94,
    upstreamProtocolCases: 45,
    webSdkCases: 49,
  });
  assert.deepEqual(
    manifest.suites.map(({ id, caseCount }) => [id, caseCount]),
    [
      ["protocol-schema", 33],
      ["protocol-semantic", 12],
      ["state-lifecycle", 8],
      ["command-lifecycle", 6],
      ["reconnect-lifecycle", 5],
      ["hostile-input", 28],
      ["flow-projection", 2],
    ],
  );
});

test("the web protocol corpus is a byte-identical mirror", async (t) => {
  const protocolManifest = await readJson(new URL("manifest.json", upstreamRoot));
  const protocolEntries = [
    "manifest.json",
    ...protocolManifest.cases.map(({ file }) => file),
    ...protocolManifest.semanticCases.map(({ file }) => file),
  ];
  assert.equal(new Set(protocolEntries).size, 46);
  for (const relative of protocolEntries) {
    await t.test(relative, async () => {
      assert.deepEqual(
        await readFile(new URL(relative, new URL("protocol/v1/", fixtureRoot))),
        await readFile(new URL(relative, upstreamRoot)),
      );
    });
  }
});

test("manifest suite counts match their executable case collections", async (t) => {
  const manifest = await loadFixtureManifest(fixtureSource);
  for (const suite of manifest.suites) {
    await t.test(suite.id, async () => {
      const document = await readJson(new URL(suite.file, fixtureRoot));
      assert.ok(Array.isArray(document[suite.caseProperty]));
      assert.equal(document[suite.caseProperty].length, suite.caseCount);
      assert.equal(new Set(document[suite.caseProperty].map(({ id }) => id)).size, suite.caseCount);
    });
  }
});

test("fixture source helpers preserve UTF-8 bytes in a browser-like adapter", async () => {
  const expected = Uint8Array.from([0x7b, 0x22, 0xc3, 0xa9, 0x22, 0x3a, 0x31, 0x7d]);
  const requested = [];
  const source = createFetchFixtureSource("https://fixtures.invalid/conformance/", async (url) => {
    requested.push(String(url));
    return new Response(expected, { status: 200 });
  });

  assert.equal(await readFixtureText(source, "vectors/example.json"), "{\"é\":1}");
  assert.deepEqual(await readFixtureBytes(source, "vectors/example.json"), expected);
  assert.deepEqual(requested, [
    "https://fixtures.invalid/conformance/vectors/example.json",
    "https://fixtures.invalid/conformance/vectors/example.json",
  ]);
});

test("fixture paths reject traversal and URL escape forms", () => {
  assert.equal(joinFixturePath("protocol/v1/manifest.json", "valid/client-open.json"), "protocol/v1/valid/client-open.json");
  for (const hostilePath of [
    "../package.json",
    "valid/../../package.json",
    "/absolute.json",
    "\\windows.json",
    "C:/windows.json",
    "https://attacker.invalid/case.json",
    "valid/case.json?redirect=1",
    "valid/case.json#fragment",
    "valid\\case.json",
    "./valid/case.json",
    "valid//case.json",
    "%2e%2e/package.json",
    "%2E%2E/package.json",
    "%252e%252e/package.json",
    "valid/%2e%2e/package.json",
    "valid%2f..%2fpackage.json",
    "%2e%2e%2fpackage.json",
    "%2e%2e%5cpackage.json",
    "%00/package.json",
    "valid/\0case.json",
    decodeURIComponent("%2e%2e%2fpackage.json"),
  ]) {
    assert.throws(() => joinFixturePath("protocol/v1/manifest.json", hostilePath), TypeError, hostilePath);
  }
});

test("fetch fixture sources reject traversal before invoking fetch", async () => {
  let fetchCount = 0;
  const source = createFetchFixtureSource("https://fixtures.invalid/conformance/", async () => {
    fetchCount += 1;
    return new Response("{}", { status: 200 });
  });

  for (const hostilePath of [
    "../secret.json",
    "%2e%2e/secret.json",
    "%252e%252e/secret.json",
    "%2e%2e%2fsecret.json",
    "%2e%2e%5csecret.json",
    "%00/secret.json",
    " https://attacker.invalid/secret.json",
  ]) {
    await assert.rejects(source.read(hostilePath), TypeError, hostilePath);
  }
  assert.equal(fetchCount, 0);
});

test("fetch fixture sources enforce response byte bounds", async () => {
  const declaredOversize = createFetchFixtureSource("https://fixtures.invalid/conformance/", async () =>
    new Response("{}", { status: 200, headers: { "content-length": "2097153" } }));
  await assert.rejects(declaredOversize.read("manifest.json"), /maximum byte length/u);

  const actualOversize = createFetchFixtureSource("https://fixtures.invalid/conformance/", async () =>
    new Response(new Uint8Array(2_097_153), { status: 200 }));
  await assert.rejects(actualOversize.read("manifest.json"), /maximum byte length/u);
});

test("top-level array splitting retains exact item text and rejects trailing data", () => {
  const source = " [ {\"revision\":9223372036854775807}, [1,{\"x\":\"]}\"}], \"last\" ] \r\n";
  assert.deepEqual(splitTopLevelArray(source), [
    "{\"revision\":9223372036854775807}",
    "[1,{\"x\":\"]}\"}]",
    "\"last\"",
  ]);
  assert.throws(() => splitTopLevelArray("[{}] null"), TypeError);
  assert.throws(() => splitTopLevelArray("[{]"), TypeError);
  assert.throws(() => splitTopLevelArray("{}"), TypeError);
  assert.throws(() => splitTopLevelArray(`[${Array.from({ length: 10_001 }, () => "null").join(",")}]`), /too many items/u);
  assert.throws(() => splitTopLevelArray(`[${"[".repeat(65)}null${"]".repeat(65)}]`), /maximum depth/u);
  assert.throws(() => splitTopLevelArray(`["${"é".repeat(1_048_577)}"]`), /maximum byte length/u);
});

test("fixture document validators enforce identity, uniqueness, and structure", async () => {
  const protocol = validateProtocolManifest(
    await readJson(new URL("protocol/v1/manifest.json", fixtureRoot)),
  );
  assert.equal(protocol.cases.length, 33);
  assert.equal(protocol.semanticCases.length, 12);

  for (const name of ["state-lifecycle", "command-lifecycle", "reconnect-lifecycle"]) {
    const document = validateScenarioDocument(
      await readJson(new URL(`vectors/${name}.json`, fixtureRoot)),
    );
    assert.equal(document.category, name);
    assert.ok(document.scenarios.every(({ steps }) => steps.length > 0));
  }

  assert.throws(
    () => validateProtocolManifest({ protocolIdentity: "wrong", cases: [], semanticCases: [] }),
    TypeError,
  );
  assert.throws(
    () =>
      validateScenarioDocument({
        format: "runic.toolkit.mvvm.conformance-scenarios/1",
        protocolIdentity: "runic.toolkit.mvvm/1",
        category: "duplicate",
        scenarios: [
          { id: "same", initial: {}, steps: [{ action: "noop", expect: {} }] },
          { id: "same", initial: {}, steps: [{ action: "noop", expect: {} }] },
        ],
      }),
    TypeError,
  );
});

test("fixture manifests require the canonical seven-suite 94-case inventory", async () => {
  const canonical = await readJson(new URL("manifest.json", fixtureRoot));
  const mutations = [
    { ...structuredClone(canonical), formatVersion: 2 },
    { ...structuredClone(canonical), suites: canonical.suites.slice(1) },
    { ...structuredClone(canonical), suites: [canonical.suites[1], canonical.suites[0], ...canonical.suites.slice(2)] },
    {
      ...structuredClone(canonical),
      suites: canonical.suites.map((suite, index) => index === 0 ? { ...suite, caseCount: 0 } : suite),
    },
  ];
  for (const mutation of mutations) {
    const mutationSource = createFixtureSource(() => JSON.stringify(mutation));
    await assert.rejects(loadFixtureManifest(mutationSource), TypeError);
  }
});
