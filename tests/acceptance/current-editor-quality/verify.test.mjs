import assert from "node:assert/strict";
import test from "node:test";
import { verifyReceipt } from "./verify.mjs";

const supplied = {
  toolkit: { revision: "a".repeat(40), tree: "b".repeat(40) },
  editor: { revision: "c".repeat(40), tree: "d".repeat(40) },
};
const journey = () => ({
  schema: "runic.editor-structural-quality/1",
  localProfiles: supplied,
  bridge: { returnedFrames: "exact", schemaValidatedDelivery: "exact", fixedBatches: [1, 256, 1024] },
  model: { messages: 50_000, reviewLocales: 100, retainedHeapMiBMaximum: 256, timing: "observation-only" },
  keyboardAccessibility: { commandSearch: "focused", recoveryFocusOrder: ["rollback", "complete"], labelsAndLandmarks: "checked", forcedColors: "checked" },
  phases: ["bridge-structural-gate", "bridge-bounded-command-tests", "editor-generated-manifest-build", "editor-svelte-check", "editor-model-scale", "editor-keyboard-a11y", "editor-command-palette"].map((name) => ({ name, status: "passed", exitCode: 0 })),
});

test("accepts repeated local structural quality evidence", () => {
  assert.equal(verifyReceipt({ schema: "runic.editor-structural-quality-repeat/1", journeys: [journey(), journey()] }, supplied).ok, true);
});

test("rejects forged timing, softened bounds, or incomplete focus evidence", () => {
  const receipt = { schema: "runic.editor-structural-quality-repeat/1", journeys: [journey(), journey()] };
  receipt.journeys[1].model.timing = "SLA: 10 seconds";
  receipt.journeys[1].bridge.fixedBatches.pop();
  receipt.journeys[1].keyboardAccessibility.recoveryFocusOrder = ["complete"];
  assert.equal(verifyReceipt(receipt, supplied).ok, false);
});
