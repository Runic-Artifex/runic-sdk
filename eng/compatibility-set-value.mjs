#!/usr/bin/env node
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const compatibility = JSON.parse(
  readFileSync(resolve(repository, "eng/runic.compatibility-set.json"), "utf8"),
);

export function compatibilitySetValue(kind, identity) {
  if (kind === "release-train-version" && identity === undefined) {
    return compatibility.releaseTrainVersion;
  }
  if (kind === "source" && identity) {
    const source = compatibility.sources.find((entry) => entry.repository === identity);
    if (source) return source.revision;
  }
  if (kind === "toolchain" && identity && compatibility.toolchain[identity]) {
    return compatibility.toolchain[identity];
  }
  throw new Error(`Unknown compatibility-set value '${[kind, identity].filter(Boolean).join(" ")}'.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    console.log(compatibilitySetValue(...process.argv.slice(2)));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
