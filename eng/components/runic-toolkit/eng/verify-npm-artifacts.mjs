#!/usr/bin/env node

import { readFileSync, readdirSync } from "node:fs";
import { basename, resolve } from "node:path";
import { spawnSync } from "node:child_process";

const [, , version, suppliedDirectory] = process.argv;
if (version === undefined || suppliedDirectory === undefined) {
  console.error("Usage: node eng/verify-npm-artifacts.mjs <package-version> <package-directory>");
  process.exit(2);
}

const directory = resolve(suppliedDirectory);
const expected = new Set([
  "@runic-artifex/mvvm",
  "@runic-artifex/mvvm-conformance",
  "@runic-artifex/mvvm-react",
  "@runic-artifex/mvvm-vue",
  "@runic-artifex/mvvm-svelte",
  "@runic-artifex/mvvm-angular",
]);
const archives = readdirSync(directory).filter((file) => file.endsWith(".tgz"));
if (archives.length !== expected.size) throw new Error(`Expected ${expected.size} npm archives, found ${archives.length}.`);

for (const archive of archives) {
  const result = spawnSync("tar", ["-xOf", resolve(directory, archive), "package/package.json"], {
    encoding: "utf8",
  });
  if (result.status !== 0) throw new Error(`Could not inspect ${basename(archive)}.`);
  const manifest = JSON.parse(result.stdout);
  if (!expected.delete(manifest.name)) throw new Error(`Unexpected npm package '${manifest.name}'.`);
  if (manifest.version !== version) throw new Error(`${manifest.name} has version ${manifest.version}.`);
  if (manifest.license !== "MIT") throw new Error(`${manifest.name} must use MIT.`);
  if (manifest.repository?.url !== "git+https://github.com/Runic-Artifex/runic-toolkit.git") {
    throw new Error(`${manifest.name} has invalid repository provenance.`);
  }
  if (manifest.dependencies?.["@runic-artifex/mvvm"] !== undefined &&
      manifest.dependencies["@runic-artifex/mvvm"] !== version) {
    throw new Error(`${manifest.name} does not pin the matching MVVM core.`);
  }
}
if (expected.size !== 0) throw new Error(`Missing npm packages: ${[...expected].join(", ")}.`);
console.log(`Verified six Runic Toolkit npm artifacts for ${version}.`);
