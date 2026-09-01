import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import { prepareWebPackage } from "./prepare-web-package.mjs";

test("GitHub candidates carry immutable version and revision metadata", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "runic-desktop-package-"));
  const packageDirectory = path.join(root, "web", "packages", "desktop");
  fs.mkdirSync(packageDirectory, { recursive: true });
  fs.writeFileSync(
    path.join(packageDirectory, "package.json"),
    `${JSON.stringify({ name: "@runic-artifex/desktop", version: "1.0.0" })}\n`,
  );

  const revision = "a".repeat(40);
  prepareWebPackage(root, "1.0.0-ci.shaaaaaaaaaaaaaaaaa", revision);
  const manifest = JSON.parse(
    fs.readFileSync(path.join(packageDirectory, "package.json"), "utf8"),
  );

  assert.equal(manifest.version, "1.0.0-ci.shaaaaaaaaaaaaaaaaa");
  assert.equal(manifest.gitHead, revision);
  assert.deepEqual(manifest.publishConfig, {
    access: "restricted",
    registry: "https://npm.pkg.github.com",
  });
  assert.deepEqual(manifest.runicCandidate, {
    sourceRevision: revision,
    dependencies: [],
  });
});

test("invalid versions and revisions are rejected", () => {
  assert.throws(() => prepareWebPackage("/tmp", "latest", "a".repeat(40)));
  assert.throws(() => prepareWebPackage("/tmp", "1.0.0", "not-a-revision"));
});
