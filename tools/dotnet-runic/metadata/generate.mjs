#!/usr/bin/env bun
import assert from "node:assert/strict";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { readToolchain } from "../../../eng/toolchain.mjs";
import { readSupport, renderReadme } from "../../../eng/support.mjs";
import { packageVersion } from "../../../eng/version.mjs";
import { repos } from "../../../eng/train/nuget.mjs";
import { trainVersions, UPSTREAMS } from "../../../eng/ci/train-feed.mjs";

const root = fileURLToPath(new URL("../../..", import.meta.url));
const target = fileURLToPath(new URL("runic.compatibility-set.json", import.meta.url));
const readmePath = resolve(root, "README.md");
const workspace = JSON.parse(readFileSync(resolve(root, "eng/workspace.json"), "utf8"));
const { $comment, schemaVersion, ...support } = readSupport(root);
// The installed CLI needs an offline snapshot, not repository import provenance.
// Schema 2 adds `support` from eng/support.json. `train` names the release train's other repositories (D060): their
// version in this train (Directory.Packages.props) and their packages, by the train gate's package id patterns
// (eng/train/nuget.mjs) and npm names. `packages` lists runic-sdk's own packages exactly. Together they define which
// packages `dotnet runic doctor`'s compatibility-set check covers; it ignores every other package (#1087).
const versions = trainVersions(readFileSync(resolve(root, "Directory.Packages.props"), "utf8"));
const metadata = {
  schemaVersion: 2,
  id: `runic-sdk-${workspace.version}`,
  releaseTrainVersion: workspace.version,
  toolchain: readToolchain(root),
  packages: ["nuget", "npm"].flatMap(ecosystem => workspace[ecosystem].map(packageEntry => ({
    ecosystem, identity: packageEntry.name, version: packageVersion(workspace, packageEntry.name),
  }))),
  train: UPSTREAMS.map(key => ({
    name: repos[key].title, repository: repos[key].repository, version: versions[key],
    nuget: repos[key].patterns, npm: repos[key].npm,
  })),
  support,
};
const output = JSON.stringify(metadata, null, 2) + "\n";
const readme = renderReadme(readFileSync(readmePath, "utf8"), support);
const command = process.argv[2] ?? "--check";
assert(["--check", "--write"].includes(command), "Use --check or --write");
if (command === "--write") {
  writeFileSync(target, output);
  writeFileSync(readmePath, readme);
} else {
  const fix = "run bun tools/dotnet-runic/metadata/generate.mjs --write";
  assert.equal(readFileSync(target, "utf8"), output, `CLI compatibility metadata is stale; ${fix}`);
  assert.equal(readFileSync(readmePath, "utf8"), readme, `README.md support table is stale; ${fix}`);
}
console.log(`CLI compatibility metadata and README support table ${command === "--write" ? "updated" : "verified"}: ${metadata.packages.length} packages, ${support.hosts.length} hosts.`);
