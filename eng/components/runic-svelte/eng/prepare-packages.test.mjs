import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { preparePackages } from "./prepare-packages.mjs";

const revision = "0123456789abcdef0123456789abcdef01234567";

function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "runic-svelte-pack-"));
  for (const directory of ["packages/svelte", "packages/sveltekit"]) {
    fs.mkdirSync(path.join(root, directory), { recursive: true });
    const name = directory.endsWith("sveltekit")
      ? "@runic-artifex/sveltekit"
      : "@runic-artifex/svelte";
    fs.writeFileSync(
      path.join(root, directory, "package.json"),
      `${JSON.stringify({ name, version: "1.0.0", publishConfig: { access: "public" } }, null, 2)}\n`,
    );
  }
  return root;
}

test("GitHub candidates are private and carry exact provenance", (context) => {
  const root = fixture();
  context.after(() => fs.rmSync(root, { recursive: true, force: true }));
  preparePackages(root, "1.0.0-ci.sha0123456789abcdef", revision, "github");

  const svelte = JSON.parse(
    fs.readFileSync(path.join(root, "packages/svelte/package.json"), "utf8"),
  );
  const sveltekit = JSON.parse(
    fs.readFileSync(path.join(root, "packages/sveltekit/package.json"), "utf8"),
  );
  assert.equal(svelte.publishConfig.access, "restricted");
  assert.equal(svelte.publishConfig.registry, "https://npm.pkg.github.com");
  assert.equal(svelte.runicCandidate.sourceRevision, revision);
  assert.deepEqual(sveltekit.runicCandidate.dependencies, [
    {
      ecosystem: "npm",
      package: "@runic-artifex/svelte",
      version: "1.0.0-ci.sha0123456789abcdef",
    },
  ]);
});

test("public packages preserve provenance and use npmjs.org", (context) => {
  const root = fixture();
  context.after(() => fs.rmSync(root, { recursive: true, force: true }));
  preparePackages(root, "1.0.0-preview.2", revision, "public");
  const manifest = JSON.parse(
    fs.readFileSync(path.join(root, "packages/svelte/package.json"), "utf8"),
  );
  assert.equal(manifest.publishConfig.access, "public");
  assert.equal(manifest.publishConfig.registry, "https://registry.npmjs.org");
});

test("invalid versions and revisions are rejected", () => {
  const root = fixture();
  try {
    assert.throws(
      () => preparePackages(root, "latest", revision, "github"),
      /version/,
    );
    assert.throws(
      () => preparePackages(root, "1.0.0", "short", "github"),
      /revision/,
    );
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
