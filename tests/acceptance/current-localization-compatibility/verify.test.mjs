import assert from "node:assert/strict";
import test from "node:test";
import { OWNERSHIP_BOUNDARY, PORTABLE_BOUNDARY, RECEIPT_SCHEMA, REPEAT_RECEIPT_SCHEMA, verifyReceipt } from "./verify.mjs";

const journey = () => ({
  schema: RECEIPT_SCHEMA,
  portableBoundary: structuredClone(PORTABLE_BOUNDARY),
  ownershipBoundary: structuredClone(OWNERSHIP_BOUNDARY),
  inputs: { portable: { sha256: "a".repeat(64) }, hosted: { sha256: "b".repeat(64) }, desktop: { sha256: "c".repeat(64) } },
  profiles: {
    hosted: { catalog: "product", contractFingerprint: "sha256:" + "a".repeat(64), localeEvidence: ["en-url-over-cookie", "de-url-over-cookie"] },
    desktop: { catalog: "editor", contractFingerprint: "sha256:" + "b".repeat(64), localeEvidence: ["en", "de", "structured-interchange"] },
  },
});

test("accepts deterministic compatibility evidence for the two bounded profiles", () => {
  const value = journey();
  assert.deepEqual(verifyReceipt({ schema: REPEAT_RECEIPT_SCHEMA, journeys: [value, structuredClone(value)] }), { ok: true, errors: [] });
});

test("fails closed when a forged portable boundary or catalog fingerprint is linked", () => {
  const first = journey();
  const second = journey();
  second.portableBoundary.mf2Profile = "full-mf2/1";
  second.profiles.desktop.contractFingerprint = first.profiles.hosted.contractFingerprint;
  const report = verifyReceipt({ schema: REPEAT_RECEIPT_SCHEMA, journeys: [first, second] });
  assert.equal(report.ok, false);
  assert.match(report.errors.join("\n"), /compatibility boundary mismatch/);
  assert.match(report.errors.join("\n"), /profile identity mismatch/);
});
