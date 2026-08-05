import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import {
  CONFORMANCE_FORMAT,
  createSdkConformanceRuntime,
  createFixtureSource,
  createReport,
  runConformance,
  runHostileInputCorpus,
  runProtocolCorpus,
  runScenarioCorpus,
  runSemanticCorpus,
} from "../dist/esm/index.js";

const fixtureRoot = new URL("../../../fixtures/conformance/", import.meta.url);
const source = createFixtureSource((path) => readFile(new URL(path, fixtureRoot)));

function createSemanticRuntime(log = []) {
  return Object.freeze({
    name: "semantic-test-adapter",
    runSemanticCase(context) {
      assert.equal(typeof context.reason, "string");
      assert.equal(typeof context.document, "object");
      log.push(`semantic:${context.id}`);
      return { passed: true };
    },
  });
}

test("the protocol runner executes every valid and invalid manifest case", async () => {
  const results = await runProtocolCorpus(source);
  assert.equal(results.length, 33);
  assert.equal(results.filter(({ status }) => status === "passed").length, 33);
  assert.equal(results.filter(({ status }) => status !== "passed").length, 0);
  assert.deepEqual(results.map(({ id }) => id), [
    "client-handshake", "client-open", "client-set-property", "client-execute",
    "client-control-messages", "all-capabilities-handshake", "boundary-identifiers", "boundary-numbers",
    "host-handshake-result", "host-opened", "host-result", "host-snapshot",
    "host-patch", "host-binding-vocabulary", "host-fault", "host-closed", "unknown-kind", "uppercase-uuid",
    "missing-capability", "negative-revision", "unknown-fault-code", "unsanitized-fault",
    "extra-envelope-property", "extra-payload-property", "bad-capability-token",
    "unsupported-version", "zero-member", "overlong-contract", "revision-overflow",
    "duplicate-capability", "empty-patch", "opened-nonzero-revision", "unknown-collection-operation",
  ]);
});

test("eachItem protocol cases validate every item and reject empty collections", async () => {
  const files = new Map([
    ["manifest.json", JSON.stringify({
      protocolIdentity: "runic.toolkit.mvvm/1",
      cases: [
        { id: "mixed-invalid", file: "mixed.json", schema: "client", documentMode: "eachItem", valid: false, reason: "mixed" },
        { id: "empty-valid", file: "empty.json", schema: "client", documentMode: "eachItem", valid: true, reason: "empty" },
      ],
      semanticCases: [],
    })],
    ["mixed.json", `[{},{"v":1,"kind":"handshake","request":"00000000-0000-4000-8000-000000000001","payload":{"supportedVersions":[1],"capabilities":[]}}]`],
    ["empty.json", "[]"],
  ]);
  const customSource = createFixtureSource((path) => files.get(path) ?? "");
  const results = await runProtocolCorpus(customSource, "manifest.json");
  assert.deepEqual(results.map(({ id, status }) => [id, status]), [
    ["mixed-invalid", "failed"],
    ["empty-valid", "failed"],
  ]);
});

test("the SDK runtime executes every state, command, reconnect, semantic, and hostile facet", async () => {
  const log = [];
  const runtime = createSdkConformanceRuntime();
  const [semantic, state, command, reconnect, hostile] = await Promise.all([
    runSemanticCorpus(source, undefined, undefined, createSemanticRuntime(log)),
    runScenarioCorpus(source, "vectors/state-lifecycle.json", runtime),
    runScenarioCorpus(source, "vectors/command-lifecycle.json", runtime),
    runScenarioCorpus(source, "vectors/reconnect-lifecycle.json", runtime),
    runHostileInputCorpus(source, undefined, runtime),
  ]);

  assert.deepEqual(
    [semantic.length, state.length, command.length, reconnect.length, hostile.length],
    [12, 8, 6, 5, 28],
  );
  assert.ok(semantic.every(({ status }) => status === "passed"));
  assert.ok([...state, ...command, ...reconnect].every(({ status }) => status === "passed"));
  assert.equal(hostile.filter(({ status }) => status === "passed").length, 28);
  assert.equal(hostile.filter(({ status }) => status === "skipped").length, 0);
  assert.equal(hostile.filter(({ status }) => status === "failed").length, 0);
  assert.equal(log.filter((entry) => entry.startsWith("semantic:")).length, 12);
});

test("the aggregate SDK report has no skipped mandatory cases", async () => {
  const report = await runConformance({ source, runtime: createSdkConformanceRuntime() });
  assert.equal(report.format, CONFORMANCE_FORMAT);
  assert.equal(report.protocolIdentity, "runic.toolkit.mvvm/1");
  assert.equal(report.runtime, "runic-toolkit-mvvm-sdk");
  assert.equal(report.success, true);
  assert.deepEqual(report.totals, { total: 94, passed: 94, failed: 0, skipped: 0 });
  assert.equal(new Set(report.cases.map(({ id, suite }) => `${suite}:${id}`)).size, 94);
  assert.equal(report.cases[0].id, "client-handshake");
  assert.equal(report.cases.at(-1).id, "flow.projection.communitytoolkit.submit-command.v1");
});

test("repeat runs produce byte-identical deterministic reports", async () => {
  const first = await runConformance({ source, runtime: createSdkConformanceRuntime() });
  const second = await runConformance({ source, runtime: createSdkConformanceRuntime() });
  assert.equal(JSON.stringify(first), JSON.stringify(second));
});

test("the aggregate runner defaults to the SDK runtime", async () => {
  const report = await runConformance({ source });
  assert.deepEqual(report.totals, { total: 94, passed: 94, failed: 0, skipped: 0 });
  assert.equal(report.success, true);
});

test("hostile generators reject excessive allocation requests deterministically", async () => {
  const hostileSource = createFixtureSource(() => JSON.stringify({
    format: "runic.toolkit.mvvm.hostile-input/1",
    protocolIdentity: "runic.toolkit.mvvm/1",
    cases: [
      {
        id: "excessive-array-allocation",
        input: { kind: "generated", generator: "repeatedArray", parameters: { count: Number.MAX_SAFE_INTEGER, value: null } },
        expect: { accepted: false },
      },
      {
        id: "excessive-depth-allocation",
        input: { kind: "generated", generator: "nestedArrays", parameters: { depth: Number.MAX_SAFE_INTEGER, leaf: null } },
        expect: { accepted: false },
      },
      {
        id: "excessive-frame-allocation",
        input: { kind: "generated", generator: "spacePaddedDocument", parameters: { document: "{}", totalUtf8Bytes: Number.MAX_SAFE_INTEGER } },
        expect: { accepted: false },
      },
      {
        id: "excessive-multiplied-value-allocation",
        input: { kind: "generated", generator: "repeatedArray", parameters: { count: 10_001, value: "x".repeat(1_000) } },
        expect: { accepted: false },
      },
    ],
  }));
  const results = await runHostileInputCorpus(hostileSource, "hostile.json");
  assert.deepEqual(results.map(({ id, suite, status, diagnostics }) => ({
    id,
    suite,
    status,
    diagnostics,
  })), [
    "excessive-array-allocation",
    "excessive-depth-allocation",
    "excessive-frame-allocation",
    "excessive-multiplied-value-allocation",
  ].map((id) => ({
    id,
    suite: "hostile-input",
    status: "failed",
    diagnostics: [{
      code: "hostile-input-generation-failed",
      message: "The hostile input recipe exceeded conformance generator bounds or was invalid.",
    }],
  })));
});

test("expectation mismatches and adapter exceptions become deterministic failures", async () => {
  const mismatched = await runScenarioCorpus(source, "vectors/state-lifecycle.json", {
    name: "mismatch",
    createScenarioDriver() {
      return { perform: () => ({ revision: "wrong" }) };
    },
  });
  assert.equal(mismatched.length, 8);
  assert.ok(mismatched.every(({ status, diagnostics }) => status === "failed" && diagnostics.length > 0));

  const report = createReport("mismatch", mismatched);
  assert.equal(report.success, false);
  assert.deepEqual(report.totals, { total: 8, passed: 0, failed: 8, skipped: 0 });
});
