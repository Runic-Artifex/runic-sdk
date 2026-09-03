import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { CI_NPM_CANDIDATES } from "./ci-npm-candidates.mjs";

const manifests = [
  "package.json",
  "samples/03-SetupApplication/Frontend/package.json",
  "samples/04-SvelteKitSetupApplication/Frontend/package.json",
  "samples/05-RunicTranslationsSetup/Frontend/package.json",
];

test("candidate inventory covers every Runic npm dependency", async () => {
  const referenced = new Set();
  for (const path of manifests) {
    const manifest = JSON.parse(await readFile(path, "utf8"));
    for (const name of Object.keys({ ...manifest.dependencies, ...manifest.devDependencies })) {
      if (name.startsWith("@runic-artifex/")) referenced.add(name);
    }
  }
  assert.deepEqual([...CI_NPM_CANDIDATES].sort(), [...referenced].sort());
});
