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

test("validation-pending/1: pending paths decode, and fieldValidating finds a member's (C3)", async () => {
  const { decodeBridgeValidation, fieldValidating } = await import("../dist/generated.js");
  const { createFieldController } = await import("../dist/index.js");
  const decoded = decodeBridgeValidation({ hasErrors: false, truncated: false, errors: [], pending: [["user-name"], ["items", 0, "label"], []] });
  assert.deepEqual(decoded.pending, [["user-name"], ["items", 0, "label"], []]);
  assert.equal("pending" in decodeBridgeValidation({ hasErrors: false, truncated: false, errors: [] }), false, "absent stays absent");
  assert.throws(() => decodeBridgeValidation({ hasErrors: false, truncated: false, errors: [], pending: [[-1]] }));
  assert.equal(fieldValidating(decoded, "user-name"), true);
  assert.equal(fieldValidating(decoded, "items"), true, "a value below the member");
  assert.equal(fieldValidating(decoded, "title"), false, "the object's own path is no member's");
  assert.equal(fieldValidating(undefined, "user-name"), false);
  assert.equal(fieldValidating({ hasErrors: false, truncated: false, errors: [] }, "user-name"), false);

  type State = { readonly name: string; readonly validation: BridgeValidationState };
  const states: State[] = [];
  const controller = createFieldController<State, object, string>({
    kind: "text", read: state => state.name, write: async () => undefined,
    validating: state => fieldValidating(state.validation, "name"),
  });
  const notified: boolean[] = [];
  controller.subscribe(() => notified.push(controller.current.validating));
  controller.setSource({ state: { name: "a", validation: { hasErrors: false, truncated: false, errors: [], pending: [["name"]] } }, client: {} });
  assert.equal(controller.current.validating, true);
  controller.setSource({ state: { name: "a", validation: { hasErrors: false, truncated: false, errors: [] } }, client: {} });
  assert.equal(controller.current.validating, false);
  assert.deepEqual(notified, [], "setSource does not notify");
  void states;
});
