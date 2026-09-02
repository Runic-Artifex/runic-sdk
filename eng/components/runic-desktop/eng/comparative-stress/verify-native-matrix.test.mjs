import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";
import { receiptSchema, summarize } from "./run.mjs";
import { createMatrix, platforms, schema } from "./verify-native-matrix.mjs";

const desktopRevision = "b".repeat(40);
const csWebuiRevision = "c".repeat(40);
const hash = "a".repeat(64);

function receipt(host) {
  const workload = { schema: "runic.comparative-stress-workload/1", payloadBytes: 1, warmupRequests: 1, repetitions: 1, requestsPerRepetition: 1, concurrency: 1, requestTimeoutMs: 1, startupTimeoutMs: 1 };
  const repetition = (native = false) => ({ startupMs: 1, completionMs: 2, throughputRequestsPerSecond: 0.5, managedAllocatedBytes: 3, peakWorkingSetBytes: 4, requestLatenciesMs: [1], adapterAssemblySha256: hash, implementationAssemblySha256: hash, shutdownBoundary: native ? "process-exit-after-native-server-only-destroy-fault" : "graceful-dispose", ...(native ? { nativeLibrarySha256: hash } : {}) });
  const desktop = repetition();
  const csWebui = repetition(true);
  return {
    schema: receiptSchema,
    workload,
    workloadSha256: createHash("sha256").update(JSON.stringify(workload)).digest("hex"),
    host: { ...host, release: "fixture", cpuModel: "fixture", logicalCpuCount: 8, node: "v24.18.0", dotnetSdk: "10.0.302" },
    observations: [
      { implementation: "runic-desktop", revision: desktopRevision, repetitions: [desktop], summary: summarize([desktop]) },
      { implementation: "cs-webui", revision: csWebuiRevision, repetitions: [csWebui], summary: summarize([csWebui]) },
    ],
    interpretation: "raw-observation-only-not-a-cross-host-or-release-sla",
    externalActions: { publications: 0, uploads: 0, releases: 0, tags: 0 },
  };
}

function entries() {
  return platforms.map((item) => {
    const value = receipt(item);
    const bytes = Buffer.from(JSON.stringify(value));
    return { rid: item.rid, path: `${item.rid}.json`, bytes, receipt: value };
  });
}

test("closes the three supported native receipts into one exact matrix", () => {
  const matrix = createMatrix(entries(), desktopRevision, csWebuiRevision);
  assert.equal(matrix.schema, schema);
  assert.deepEqual(matrix.platforms.map((item) => item.rid), ["win-x64", "osx-x64", "osx-arm64"]);
  assert.deepEqual(matrix.toolchain, { node: "v24.18.0", dotnetSdk: "10.0.302" });
  assert.equal(new Set(matrix.platforms.map((item) => item.receipt.sha256)).size, 3);
});

test("rejects missing profiles, host substitutions, and source drift", () => {
  assert.throws(() => createMatrix(entries().slice(1), desktopRevision, csWebuiRevision), /win-x64: receipt is missing/);
  const changedHost = entries();
  changedHost[0].receipt.host.platform = "darwin";
  assert.throws(() => createMatrix(changedHost, desktopRevision, csWebuiRevision), /win-x64: host profile mismatch/);
  assert.throws(() => createMatrix(entries(), "d".repeat(40), csWebuiRevision), /Desktop revision mismatch/);
});
