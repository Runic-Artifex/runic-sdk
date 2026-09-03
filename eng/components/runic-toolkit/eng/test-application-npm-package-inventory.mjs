#!/usr/bin/env node

import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { applicationNpmPackages } from "./application-npm-packages.mjs";

const root = resolve(import.meta.dirname, "..");
const packageRoot = join(root, "web", "packages");
const declared = applicationNpmPackages.map(({ directory, identity }) => {
  const manifest = JSON.parse(readFileSync(join(packageRoot, directory, "package.json"), "utf8"));
  assert.equal(manifest.name, identity, `${directory} has the wrong package identity`);
  assert.notEqual(manifest.private, true, `${identity} must be publishable`);
  return identity;
});
const publishable = readdirSync(packageRoot, { withFileTypes: true })
  .filter((entry) => entry.isDirectory())
  .map((entry) => JSON.parse(readFileSync(join(packageRoot, entry.name, "package.json"), "utf8")))
  .filter((manifest) => manifest.private !== true && manifest.publishConfig)
  .map((manifest) => manifest.name)
  .sort();

assert.deepEqual([...declared].sort(), publishable);
console.log(`Application npm package inventory covers ${declared.length} publishable packages.`);
