import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { readOperationCatalog, validateScenario, verifyContract } from "./verify-desktop-contract.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const invalid = join(root, "tests/fixtures/desktop-contract/invalid-scenarios");
const operations = readOperationCatalog(join(root, "specs/desktop/conformance/operations.json"), "operations.json", (message) => {
  throw new Error(message);
});

const errorsOf = (file) => {
  const errors = [];
  validateScenario(JSON.parse(readFileSync(join(invalid, file), "utf8")), file, (message) => errors.push(message), operations);
  return errors;
};

test("the Runic Desktop contract and every conformance scenario verify", () => {
  assert.equal(verifyContract(root).identity, "runic.desktop.presentation/1");
});

// Each invalid scenario is otherwise well formed; only its resource use is wrong.
const expectedErrors = {
  "unknown-surface.json": [
    /arrange\[0\] \(session\.createAuthenticated\)\.arguments\.surface uses surface surface-1, which arrange or an earlier step did not create/,
  ],
  "unnamed-surface.json": [/steps\[0\] \(session\.connect\) must name its surface argument/],
  "used-before-created.json": [/steps\[0\] \(session\.authenticate\)\.target uses session session-1, which arrange or an earlier step did not create/],
  "uncreated-subject.json": [/expect\.states\[0\] names listener-1, which arrange or steps did not create/],
};

test("every invalid scenario fixture has an expected rejection", () => {
  assert.deepEqual(readdirSync(invalid).sort(), Object.keys(expectedErrors).sort());
});

for (const [file, patterns] of Object.entries(expectedErrors)) {
  test(`contract verification rejects ${file}`, () => {
    const errors = errorsOf(file);
    assert.equal(errors.length, patterns.length, errors.join("\n"));
    patterns.forEach((pattern, index) => assert.match(errors[index], pattern));
  });
}
