#!/usr/bin/env node
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const authorityPath = resolve(repository, "../.github/runic.compatibility-set.json");
const projectionPath = resolve(repository, "eng/runic.compatibility-set.json");
const authority = JSON.parse(readFileSync(authorityPath, "utf8"));
const verificationRepositories = new Set([
  "runic-command-line",
  "runic-assets",
  "runic-translations",
  "runic-desktop",
  "runic-svelte",
  "runic-vite",
]);
const projection = {
  schemaVersion: authority.schemaVersion,
  id: authority.id,
  releaseTrainVersion: authority.releaseTrainVersion,
  toolchain: authority.toolchain,
  // Exclude Toolkit's own revision: a checked-in projection cannot also pin
  // the commit that contains it. Keep only sources consumed by eng/verify.sh.
  sources: authority.sources.filter((source) => verificationRepositories.has(source.repository)),
  packages: authority.packages,
};
const rendered = `${JSON.stringify(projection, null, 2)}\n`;

if (process.argv.includes("--check")) {
  const current = readFileSync(projectionPath, "utf8");
  if (current !== rendered) {
    console.error("eng/runic.compatibility-set.json is stale; run node eng/sync-compatibility-set.mjs.");
    process.exitCode = 1;
  }
} else {
  writeFileSync(projectionPath, rendered);
}
