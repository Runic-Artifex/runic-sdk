#!/usr/bin/env bun
import assert from "node:assert/strict";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { readToolchain } from "../../../eng/toolchain.mjs";

const root = fileURLToPath(new URL("../../..", import.meta.url));
const target = fileURLToPath(new URL("runic.compatibility-set.json", import.meta.url));
const workspace = JSON.parse(readFileSync(resolve(root, "eng/workspace.json"), "utf8"));
// The installed CLI needs an offline snapshot, not repository import provenance.
const metadata = {
  schemaVersion: 1,
  id: `runic-sdk-${workspace.version}`,
  releaseTrainVersion: workspace.version,
  toolchain: readToolchain(root),
  packages: ["nuget", "npm"].flatMap(ecosystem => workspace[ecosystem].map(packageEntry => ({
    ecosystem, identity: packageEntry.name, version: workspace.version,
  }))),
};
const output = JSON.stringify(metadata, null, 2) + "\n";
const command = process.argv[2] ?? "--check";
assert(["--check", "--write"].includes(command), "Use --check or --write");
if (command === "--write") writeFileSync(target, output);
else assert.equal(readFileSync(target, "utf8"), output,
  "CLI compatibility metadata is stale; run bun tools/dotnet-runic/metadata/generate.mjs --write");
console.log(`CLI compatibility metadata ${command === "--write" ? "updated" : "verified"}: ${metadata.packages.length} packages.`);
