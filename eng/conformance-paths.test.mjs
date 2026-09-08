import assert from "node:assert/strict";
import { readFileSync, statSync } from "node:fs";
import { resolve, dirname } from "node:path";
import test from "node:test";
import { root } from "./run.mjs";

test("Desktop conformance evidence points to maintained source files", () => {
  const path = resolve(root, "specs/desktop/conformance/cs-webui-example-parity.json");
  const manifest = JSON.parse(readFileSync(path, "utf8"));
  for (const entry of manifest.entries) {
    assert.ok(statSync(resolve(dirname(path), entry.target)).isFile(), entry.id);
  }
});

test("CommandLine protocol fixtures resolve relative to their manifest", () => {
  const path = resolve(root, "specs/command-line/protocol/manifest.json");
  const manifest = JSON.parse(readFileSync(path, "utf8"));
  for (const fixture of manifest.fixtures) {
    assert.ok(statSync(resolve(dirname(path), fixture.path)).isFile(), fixture.id);
  }
});
