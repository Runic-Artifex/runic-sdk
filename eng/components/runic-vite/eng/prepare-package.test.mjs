import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { preparePackage } from "./prepare-package.mjs";

const revision = "0123456789abcdef0123456789abcdef01234567";

function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "runic-vite-pack-"));
  fs.writeFileSync(
    path.join(root, "package.json"),
    `${JSON.stringify({ name: "@runic-artifex/vite-plugin-runic", version: "1.0.0", publishConfig: { access: "public" } }, null, 2)}\n`,
  );
  return root;
}

test("GitHub candidates are private and carry exact provenance", (context) => {
  const root = fixture();
  context.after(() => fs.rmSync(root, { recursive: true, force: true }));
  preparePackage(root, "1.0.0-ci.sha0123456789abcdef", revision, "github");
  const manifest = JSON.parse(
    fs.readFileSync(path.join(root, "package.json"), "utf8"),
  );
  assert.equal(manifest.publishConfig.access, "restricted");
  assert.equal(manifest.publishConfig.registry, "https://npm.pkg.github.com");
  assert.equal(manifest.runicCandidate.sourceRevision, revision);
});

test("public packages use npmjs.org", (context) => {
  const root = fixture();
  context.after(() => fs.rmSync(root, { recursive: true, force: true }));
  preparePackage(root, "1.0.0-preview.2", revision, "public");
  const manifest = JSON.parse(
    fs.readFileSync(path.join(root, "package.json"), "utf8"),
  );
  assert.equal(manifest.publishConfig.registry, "https://registry.npmjs.org");
});

test("invalid versions and revisions are rejected", () => {
  const root = fixture();
  try {
    assert.throws(() => preparePackage(root, "latest", revision, "github"));
    assert.throws(() => preparePackage(root, "1.0.0", "short", "github"));
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
