import assert from "node:assert/strict";
import { test } from "node:test";
import { BridgeError, BridgeOperationUncertainError, FieldError, describeBridgeError, isBridgeError } from "../dist/index.js";

test("isBridgeError tells Bridge errors and narrows them by kind", () => {
  const released = new BridgeError("released", "This View was released.");
  assert.equal(isBridgeError(released), true);
  assert.equal(isBridgeError(released, "released"), true);
  assert.equal(isBridgeError(released, "disconnected"), false);
  assert.equal(isBridgeError(released, ["disconnected", "released"]), true);
  assert.equal(isBridgeError(new Error("x")), false);
  assert.equal(isBridgeError(undefined), false);
  // Another copy of the package brands its errors with the same registered symbol.
  const foreign = Object.assign(new Error("x"), { kind: "timeout" });
  Object.defineProperty(foreign, Symbol.for("runic.views.BridgeError"), { value: true });
  assert.equal(isBridgeError(foreign, "timeout"), true);
  const error: unknown = released;
  if (isBridgeError(error, "released")) {
    const kind: "released" = error.kind;
    void kind;
  }
});

test("describeBridgeError gives one sentence per condition, and the producer's message for rejections", () => {
  assert.equal(describeBridgeError(undefined), "");
  assert.equal(describeBridgeError(null), "");
  assert.match(describeBridgeError(new BridgeError("unavailable", "No __runicBridge.")), /not running in a Runic host/);
  assert.match(describeBridgeError(new BridgeError("timeout", "Timed out after 5000 ms.")), /did not answer in time/);
  assert.match(describeBridgeError(new BridgeError("disconnected", "The Bridge is disconnected.")), /connection to the application was lost/);
  assert.match(describeBridgeError(new BridgeError("released", "This View was released.")), /closed or replaced/);
  assert.match(describeBridgeError(new BridgeError("cancelled", "")), /cancelled/);
  assert.equal(describeBridgeError(new BridgeError("rejected", "Step must be positive.", { reason: "invalid-input" })), "Step must be positive.");
  assert.equal(describeBridgeError(new BridgeError("rejected", "", { reason: "invalid-input" })), "The application rejected the request.");
  assert.equal(describeBridgeError(new BridgeError("rejected", "", { reason: "unavailable" })), "This action is not available right now.");
  assert.match(describeBridgeError(new BridgeError("rejected", "Too many.", { reason: "capacity" })), /busy/);
  assert.match(describeBridgeError(new BridgeError("failed", "x", { reason: "protocol-mismatch" })), /different Runic versions/);
  assert.match(describeBridgeError(new BridgeError("failed", "Saved.", { reason: "state-withheld" })), /cannot show it yet/);
  assert.equal(describeBridgeError(new BridgeError("failed", "The command failed.", { detail: { message: "at C:\\secret" } })), "The command failed.");
  assert.equal(describeBridgeError(new FieldError("conflict", "Someone else changed the title.")), "Someone else changed the title.");
  assert.match(describeBridgeError(new BridgeOperationUncertainError("lost", { contract: "c", requestId: "r" })), /not known whether/);
  assert.equal(describeBridgeError(new TypeError("Bad value.")), "Bad value.");
  assert.equal(describeBridgeError("offline"), "offline");
});
