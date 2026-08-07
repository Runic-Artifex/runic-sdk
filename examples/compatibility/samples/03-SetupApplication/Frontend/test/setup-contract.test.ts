import assert from "node:assert/strict";
import test from "node:test";
import { Schema } from "effect";
import { SetupCommand, SetupEvent, SetupSnapshot } from "../src/setup-contract.ts";

test("the Setup wire contract accepts named domain values", () => {
  const command = Schema.decodeUnknownSync(SetupCommand, { onExcessProperty: "error" })({
    _tag: "StartInstallation",
    destinationSelectionId: "7e510a78-3c9a-4bed-8c31-2d93e5bbb835",
    selectedFeatures: ["core", "examples"],
  });
  assert.equal(command._tag, "StartInstallation");
  const event = Schema.decodeUnknownSync(SetupEvent, { onExcessProperty: "error" })({
    _tag: "OperationProgress",
    operationId: "414b9212-6107-4bdf-9601-cc55bf3c2471",
    completed: 2,
    total: 5,
    message: "Installing",
  });
  assert.equal(event._tag, "OperationProgress");
});

test("closed schemas reject incidental ViewModel-shaped fields", () => {
  assert.throws(() => Schema.decodeUnknownSync(SetupSnapshot, { onExcessProperty: "error" })({
    viewId: "Welcome",
    revision: 0,
    selectedFeatures: ["core"],
    canNavigateBack: false,
    canNavigateNext: true,
    properties: [{ memberId: 1, value: "legacy" }],
  }));
});
