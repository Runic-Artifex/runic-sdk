// Field descriptors (F1): `fieldErrors`, which generated `BridgeField.errors` calls.
import assert from "node:assert/strict";
import { test } from "node:test";
import { fieldErrors } from "../dist/generated.js";
import type { BridgeValidationState } from "../dist/index.js";

const validation: BridgeValidationState = {
  hasErrors: true, truncated: false,
  errors: [
    { path: [], message: "Approval is required." },
    { path: ["title"], message: "Title required" },
    { path: ["profile", "postal-code"], message: "Postal code required" },
    { path: ["profile"], message: "Profile incomplete" },
    { path: ["titles", 0], message: "First title" },
    { path: ["profile", "city"], message: "City required" },
  ],
};

test("a member's own messages come from {name}Errors, then the messages below it", () => {
  // The member's own validation entry is the same message as its {name}Errors entry, so it is not repeated.
  assert.deepEqual(fieldErrors(validation, "title", ["Title required"]), ["Title required"]);
  assert.deepEqual(fieldErrors(validation, "profile", ["Profile incomplete"]),
    ["Profile incomplete", "Postal code required", "City required"]);
});

test("without {name}Errors the member's own messages come from validation", () => {
  assert.deepEqual(fieldErrors(validation, "title"), ["Title required"]);
  assert.deepEqual(fieldErrors(validation, "profile"), ["Postal code required", "Profile incomplete", "City required"]);
  // Another member whose name starts with the same text is not below it.
  assert.deepEqual(fieldErrors(validation, "titles"), ["First title"]);
  assert.deepEqual(fieldErrors(validation, "body"), []);
});

test("the same inputs return the same array", () => {
  const own = ["Profile incomplete"];
  assert.equal(fieldErrors(validation, "profile", own), fieldErrors(validation, "profile", own));
  assert.equal(fieldErrors(validation, "title"), fieldErrors(validation, "title"));
  assert.equal(fieldErrors(validation, "body"), fieldErrors(validation, "missing"));
  // Only {name}Errors, and nothing below the member: its own array.
  const titleErrors = ["Title required"];
  assert.equal(fieldErrors(validation, "title", titleErrors), titleErrors);
  assert.equal(fieldErrors(undefined, "title", titleErrors), titleErrors);
});
