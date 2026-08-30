import assert from "node:assert/strict";
import test from "node:test";
import { verifyReceipt } from "./verify.mjs";

const linked = {
  schema: "runic.unsigned-candidate-set/1", publication: "forbidden",
  releaseAuthority: { distribution: { id: "translations-editor-archive", version: { state: "unassigned", value: null } } },
  platforms: [{ runtimeIdentifier: "linux-x64" }, { runtimeIdentifier: "osx-arm64" }, { runtimeIdentifier: "win-x64" }],
};
const journey = () => ({ schema: "runic.unsigned-candidate-set-consumer/1", isolation: { workingDirectory: "temporary-empty" }, noProductProjectReference: true, candidateSet: structuredClone(linked) });

test("accepts deterministic publication-forbidden candidate-set evidence", () => {
  const receipt = { schema: "runic.unsigned-candidate-set-consumer-repeat/1", journeys: [journey(), journey()] };
  assert.deepEqual(verifyReceipt(receipt, linked), { ok: true, errors: [] });
});

test("rejects a forged receipt or softened publication authority", () => {
  const receipt = { schema: "runic.unsigned-candidate-set-consumer-repeat/1", journeys: [journey(), journey()] };
  receipt.journeys[1].candidateSet.publication = "allowed";
  receipt.journeys[1].candidateSet.releaseAuthority.distribution.version = { state: "published", value: "1.0.0" };
  assert.equal(verifyReceipt(receipt, linked).ok, false);
});
