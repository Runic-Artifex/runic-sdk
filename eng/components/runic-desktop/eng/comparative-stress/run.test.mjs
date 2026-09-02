import assert from "node:assert/strict";
import test from "node:test";
import { percentile, summarize, verifyReceipt } from "./run.mjs";

test("computes nearest-rank latency and raw summaries", () => {
  assert.equal(percentile([4, 1, 3, 2], 0.5), 2);
  const repetitions = [{ startupMs: 1, completionMs: 2, throughputRequestsPerSecond: 3, managedAllocatedBytes: 4, peakWorkingSetBytes: 5, requestLatenciesMs: [1, 2, 3, 4] }];
  assert.deepEqual(summarize(repetitions).latencyMs, { p50: 2, p95: 4, p99: 4 });
});

test("rejects incomplete comparative observations", () => {
  assert.ok(verifyReceipt({ schema: "runic.comparative-stress-observation/1" }).length > 0);
});
