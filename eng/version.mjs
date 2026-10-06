#!/usr/bin/env bun
// eng/workspace.json owns the SDK release-train version. eng/Versions.props and
// every npm manifest carry copies that `check` compares and `bump` rewrites.
// Directory.Packages.props and the template package read RunicSdkVersion, the
// CLI compatibility metadata is regenerated, and template locks are stamped
// from the packed archives. Runic.CommandLine is released independently.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const versionPattern = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?$/;
const workspaceVersion = /^(  "version": ")([^"]*)(",?)$/m;
const manifestVersion = /^(  "version": ")([^"]*)(",?)$/m;
const sdkVersion = /(<RunicSdkVersion>)([^<]*)(<\/RunicSdkVersion>)/;

function single(text, pattern, label) {
  const global = new RegExp(pattern.source, pattern.flags.includes("g") ? pattern.flags : `${pattern.flags}g`);
  const matches = [...text.matchAll(global)];
  assert.equal(matches.length, 1, `Expected one ${label}`);
  return matches[0][2];
}

export function readVersionFiles(base = root) {
  const read = path => readFileSync(resolve(base, path), "utf8");
  const workspace = read("eng/workspace.json");
  return {
    "eng/workspace.json": workspace,
    "eng/Versions.props": read("eng/Versions.props"),
    ...Object.fromEntries(JSON.parse(workspace).npm.map(p => [`${p.path}/package.json`, read(`${p.path}/package.json`)])),
  };
}

// Returns each copy of the release-train version, keyed by file.
export function versionCopies(files) {
  const npmNames = new Set(JSON.parse(files["eng/workspace.json"]).npm.map(p => p.name));
  const copies = {
    "eng/workspace.json": single(files["eng/workspace.json"], workspaceVersion, "workspace version"),
    "eng/Versions.props": single(files["eng/Versions.props"], sdkVersion, "RunicSdkVersion"),
  };
  for (const [path, text] of Object.entries(files)) {
    if (!path.endsWith("package.json")) continue;
    copies[path] = single(text, manifestVersion, `${path} version`);
    const manifest = JSON.parse(text);
    for (const field of ["dependencies", "peerDependencies", "optionalDependencies"])
      for (const [name, range] of Object.entries(manifest[field] ?? {}))
        if (npmNames.has(name) && !range.startsWith("workspace:")) copies[`${path} ${field}.${name}`] = range;
  }
  return copies;
}

export function checkVersions(files) {
  const copies = versionCopies(files);
  const authority = copies["eng/workspace.json"];
  assert.match(authority, versionPattern, "eng/workspace.json version is not a SemVer version");
  for (const [path, value] of Object.entries(copies))
    assert.equal(value, authority, `${path} has ${value}; eng/workspace.json has ${authority}. Run bun run version:bump ${authority}`);
  return authority;
}

export function bumpVersionFiles(files, version) {
  assert.match(version ?? "", versionPattern, "Usage: bun run version:bump <major.minor.patch[-prerelease]>");
  const previous = checkVersions(files);
  const npmNames = JSON.parse(files["eng/workspace.json"]).npm.map(p => p.name);
  const next = {};
  for (const [path, text] of Object.entries(files)) {
    let updated = path === "eng/Versions.props"
      ? text.replace(sdkVersion, `$1${version}$3`)
      : text.replace(path === "eng/workspace.json" ? workspaceVersion : manifestVersion, `$1${version}$3`);
    if (path.endsWith("package.json"))
      for (const name of npmNames)
        updated = updated.replaceAll(`"${name}": "${previous}"`, `"${name}": "${version}"`);
    next[path] = updated;
  }
  assert.equal(checkVersions(next), version);
  return next;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [command, version] = process.argv.slice(2);
  if (command === "check") {
    console.log(`Release-train version ${checkVersions(readVersionFiles())} is consistent.`);
  } else if (command === "bump") {
    const files = readVersionFiles();
    for (const [path, text] of Object.entries(bumpVersionFiles(files, version)))
      if (text !== files[path]) writeFileSync(resolve(root, path), text);
    execFileSync(process.execPath, [resolve(root, "tools/dotnet-runic/metadata/generate.mjs"), "--write"], { stdio: "inherit" });
    console.log(`Set the release-train version to ${version}. Record user-facing changes in eng/release/notes/${version}.md; RunicPackageValidationBaselineVersion stays at the last published release.`);
  } else {
    throw new Error("Usage: bun eng/version.mjs check | bun eng/version.mjs bump <version>");
  }
}
