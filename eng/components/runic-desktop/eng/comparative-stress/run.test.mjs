import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { arch, platform } from "node:os";
import test from "node:test";
import { percentile, receiptSchema, summarize, verifyReceipt } from "./run.mjs";

const hash = "a".repeat(64);

function receipt(host = { platform: "win32", architecture: "x64", release: "fixture", cpuModel: "fixture", logicalCpuCount: 8, node: "v24.18.0", dotnetSdk: "10.0.302" }) {
  const workload = { schema: "runic.comparative-stress-workload/1", payloadBytes: 1, warmupRequests: 1, repetitions: 1, requestsPerRepetition: 1, concurrency: 1, requestTimeoutMs: 1, startupTimeoutMs: 1 };
  const repetition = (native = false) => ({ startupMs: 1, completionMs: 2, throughputRequestsPerSecond: 0.5, managedAllocatedBytes: 3, peakWorkingSetBytes: 4, requestLatenciesMs: [1], adapterAssemblySha256: hash, implementationAssemblySha256: hash, shutdownBoundary: native ? "process-exit-after-native-server-only-destroy-fault" : "graceful-dispose", ...(native ? { nativeLibrarySha256: hash } : {}) });
  const desktop = repetition();
  const csWebui = repetition(true);
  return {
    schema: receiptSchema,
    workload,
    workloadSha256: createHash("sha256").update(JSON.stringify(workload)).digest("hex"),
    host,
    observations: [
      { implementation: "runic-desktop", revision: "b".repeat(40), repetitions: [desktop], summary: summarize([desktop]) },
      { implementation: "cs-webui", revision: "c".repeat(40), repetitions: [csWebui], summary: summarize([csWebui]) },
    ],
    interpretation: "raw-observation-only-not-a-cross-host-or-release-sla",
    externalActions: { publications: 0, uploads: 0, releases: 0, tags: 0 },
  };
}

test("computes nearest-rank latency and raw summaries", () => {
  assert.equal(percentile([4, 1, 3, 2], 0.5), 2);
  const repetitions = [{ startupMs: 1, completionMs: 2, throughputRequestsPerSecond: 3, managedAllocatedBytes: 4, peakWorkingSetBytes: 5, requestLatenciesMs: [1, 2, 3, 4] }];
  assert.deepEqual(summarize(repetitions).latencyMs, { p50: 2, p95: 4, p99: 4 });
});

test("rejects incomplete comparative observations", () => {
  assert.ok(verifyReceipt({ schema: "runic.comparative-stress-observation/1" }).length > 0);
});

test("verifies supported foreign-host receipts offline but binds fresh measurements to the current host", () => {
  const foreign = platform() === "win32"
    ? { platform: "darwin", architecture: "arm64", release: "fixture", cpuModel: "fixture", logicalCpuCount: 8, node: "v24.18.0", dotnetSdk: "10.0.302" }
    : { platform: "win32", architecture: "x64", release: "fixture", cpuModel: "fixture", logicalCpuCount: 8, node: "v24.18.0", dotnetSdk: "10.0.302" };
  assert.deepEqual(verifyReceipt(receipt(foreign)), []);
  assert.match(verifyReceipt(receipt(foreign), { requireCurrentHost: true }).join("\n"), /current host/);
  assert.deepEqual(verifyReceipt(receipt({ ...foreign, platform: platform(), architecture: arch() }), { requireCurrentHost: true }), []);
  assert.match(verifyReceipt(receipt({ ...foreign, platform: "freebsd" })).join("\n"), /host fingerprint/);
  const historical = receipt(foreign);
  historical.schema = "runic.comparative-stress-observation/1";
  delete historical.host.dotnetSdk;
  assert.deepEqual(verifyReceipt(historical), []);
});
