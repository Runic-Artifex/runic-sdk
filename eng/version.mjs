#!/usr/bin/env bun
// eng/workspace.json owns the SDK release-train version. eng/Versions.props,
// every npm manifest, the manifests' bun.lock workspace entries and the project
// references in every committed NuGet packages.lock.json carry copies that
// `check` compares and `bump` rewrites, as does the version constant that
// @runic-artifex/views compares with the .NET Views version it connects to.
// Directory.Packages.props and the template package read RunicSdkVersion, the
// CLI compatibility metadata is regenerated, and template locks are stamped
// from the packed archives. Runic.CommandLine is released independently.
//
// A NuGet package listed in `separateVersions` may ship on a version of its own:
// its eng/workspace.json entry has a "version" right after "name" (absent means
// the train version), and eng/Versions.props carries a literal copy in the listed
// property. `packageVersion` gives the version of any package. `bump <version>`
// moves such a package with the train while their versions are equal and leaves
// it otherwise; `bump <version> --cswebui <cswebui-version>` sets the
// Runic.Application.Views.CsWebUi version explicitly.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const versionPattern = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?$/;
const workspaceVersion = /^(  "version": ")([^"]*)(",?)$/m;
const manifestVersion = /^(  "version": ")([^"]*)(",?)$/m;
const sdkVersion = /(<RunicSdkVersion>)([^<]*)(<\/RunicSdkVersion>)/;
// The browser runtime reports a .NET Runic.Application.Views of another release (D02).
const viewsVersionFile = "packages/web/views/src/version.ts";
const viewsVersion = /^(export const viewsVersion = ")([^"]*)(";)$/m;
const escapeRegExp = text => text.replace(/[.*+?^${}()|[\]\\/]/g, match => `\\${match}`);
// The eng/Versions.props property that carries each separately versioned package's version.
export const separateVersions = { "Runic.Application.Views.CsWebUi": "RunicCsWebUiPackageVersion" };
const propertyVersion = property => new RegExp(`(<${property}\\b[^>]*>)([^<]*)(</${property}>)`);
const entryVersion = name => new RegExp(`("name": "${escapeRegExp(name)}",\\s*"version": ")([^"]*)(")`);
const findEntry = (workspace, name) => [...workspace.nuget ?? [], ...workspace.npm ?? []]
  .find(entry => entry.name.toLowerCase() === name.toLowerCase());
// The version a workspace package ships on: its entry's own version, else the
// release train. Projects outside the inventory carry the train version.
export const packageVersion = (workspace, name) => findEntry(workspace, name)?.version ?? workspace.version;
// bun.lock records each workspace package's name and version under its path.
const lockVersion = entry => new RegExp(
  `^(    "${escapeRegExp(entry.path)}": \\{\\n      "name": "${escapeRegExp(entry.name)}",\\n      "version": ")([^"]*)(",?)$`, "m");

function single(text, pattern, label) {
  const global = new RegExp(pattern.source, pattern.flags.includes("g") ? pattern.flags : `${pattern.flags}g`);
  const matches = [...text.matchAll(global)];
  assert.equal(matches.length, 1, `Expected one ${label}`);
  return matches[0][2];
}
const replaceVersion = (text, pattern, version) => text.replace(pattern, (_, prefix, _old, suffix) => `${prefix}${version}${suffix}`);
// A project may name its lock file packages.<project>.lock.json (NuGetLockFilePath).
const isNugetLock = path => /(?:^|\/)packages(?:\.[^/]+)?\.lock\.json$/.test(path);
// A NuGet lock file records each referenced project's dependency on another
// project as "Name": "[<version>, )", with the version from eng/Versions.props.
// Locked restore does not compare these ranges, so a stale one survives in CI.
function* nugetProjectRanges(text) {
  for (const [framework, entries] of Object.entries(JSON.parse(text).dependencies ?? {})) {
    const projects = new Set(Object.entries(entries).filter(([, entry]) => entry.type === "Project").map(([name]) => name.toLowerCase()));
    for (const [owner, entry] of Object.entries(entries))
      for (const [name, range] of Object.entries(entry.dependencies ?? {}))
        if (projects.has(name.toLowerCase())) yield { key: `${framework}.${owner}.${name}`, name, range };
  }
}

export function readVersionFiles(base = root) {
  const read = path => readFileSync(resolve(base, path), "utf8");
  const workspace = read("eng/workspace.json");
  return {
    "eng/workspace.json": workspace,
    "eng/Versions.props": read("eng/Versions.props"),
    "bun.lock": read("bun.lock"),
    ...(existsSync(resolve(base, viewsVersionFile)) ? { [viewsVersionFile]: read(viewsVersionFile) } : {}),
    ...Object.fromEntries(JSON.parse(workspace).npm.map(p => [`${p.path}/package.json`, read(`${p.path}/package.json`)])),
    ...Object.fromEntries(execFileSync("git", ["ls-files", "-z", "--", "*packages*.lock.json"], { cwd: base, encoding: "utf8" })
      .split("\0").filter(isNugetLock).map(path => [path, read(path)])),
  };
}

// Returns each version copy, keyed by file, with the version eng/workspace.json expects there.
export function versionCopies(files) {
  const workspace = JSON.parse(files["eng/workspace.json"]);
  const npm = workspace.npm;
  const npmNames = new Set(npm.map(p => p.name));
  const train = value => ({ value, expected: workspace.version });
  const copies = {
    "eng/workspace.json": train(single(files["eng/workspace.json"], workspaceVersion, "workspace version")),
    "eng/Versions.props": train(single(files["eng/Versions.props"], sdkVersion, "RunicSdkVersion")),
  };
  for (const [name, property] of Object.entries(separateVersions))
    if (findEntry(workspace, name)) copies[`eng/Versions.props ${property}`] = {
      value: single(files["eng/Versions.props"], propertyVersion(property), property), expected: packageVersion(workspace, name) };
  for (const entry of npm)
    copies[`bun.lock ${entry.path}`] = train(single(files["bun.lock"], lockVersion(entry), `bun.lock workspace entry for ${entry.path}`));
  if (files[viewsVersionFile] !== undefined)
    copies[viewsVersionFile] = train(single(files[viewsVersionFile], viewsVersion, "viewsVersion"));
  for (const [path, text] of Object.entries(files)) {
    if (isNugetLock(path)) {
      for (const { key, name, range } of nugetProjectRanges(text))
        copies[`${path} ${key}`] = { value: /^\[([^,\]]+), \)$/.exec(range)?.[1] ?? range, expected: packageVersion(workspace, name) };
      continue;
    }
    if (!path.endsWith("package.json")) continue;
    copies[path] = train(single(text, manifestVersion, `${path} version`));
    const manifest = JSON.parse(text);
    for (const field of ["dependencies", "peerDependencies", "optionalDependencies"])
      for (const [name, range] of Object.entries(manifest[field] ?? {}))
        if (npmNames.has(name) && !range.startsWith("workspace:")) copies[`${path} ${field}.${name}`] = train(range);
  }
  return copies;
}

export function checkVersions(files) {
  const workspace = JSON.parse(files["eng/workspace.json"]);
  const authority = workspace.version;
  assert.match(authority, versionPattern, "eng/workspace.json version is not a SemVer version");
  for (const entry of workspace.npm ?? [])
    assert.equal(entry.version, undefined, `${entry.name}: npm packages ship on the release-train version`);
  for (const entry of (workspace.nuget ?? []).filter(entry => entry.version !== undefined)) {
    assert(Object.hasOwn(separateVersions, entry.name), `${entry.name} has a version of its own but no eng/Versions.props property in eng/version.mjs separateVersions`);
    assert.match(entry.version, versionPattern, `${entry.name} version in eng/workspace.json is not a SemVer version`);
    single(files["eng/workspace.json"], entryVersion(entry.name), `"version" right after "name" in the ${entry.name} entry`);
  }
  for (const [path, { value, expected }] of Object.entries(versionCopies(files)))
    assert.equal(value, expected, `${path} has ${value}; eng/workspace.json has ${expected}. Run ${isNugetLock(path.split(" ")[0]) ? "bun run lock:nuget" : `bun run version:bump ${authority}`}`);
  return authority;
}

// `packageVersions` maps a separately versioned package to the version to set.
export function bumpVersionFiles(files, version, packageVersions = {}) {
  assert.match(version ?? "", versionPattern, "Usage: bun run version:bump <major.minor.patch[-prerelease]> [--cswebui <version>]");
  const previous = checkVersions(files);
  const before = JSON.parse(files["eng/workspace.json"]);
  const npm = before.npm;
  let workspaceText = replaceVersion(files["eng/workspace.json"], workspaceVersion, version);
  for (const [name, separate] of Object.entries(packageVersions)) {
    assert(Object.hasOwn(separateVersions, name), `${name} has no version of its own`);
    assert.match(separate ?? "", versionPattern, `${name} version is not a SemVer version`);
    assert.notEqual(findEntry(before, name)?.version, undefined, `Add "version" after "name" to the ${name} entry in eng/workspace.json`);
  }
  // A separate version moves with the train while they are equal; an absent one follows it implicitly.
  for (const entry of (before.nuget ?? []).filter(entry => entry.version !== undefined)) {
    const target = packageVersions[entry.name] ?? (entry.version === previous ? version : entry.version);
    workspaceText = replaceVersion(workspaceText, entryVersion(entry.name), target);
  }
  const after = JSON.parse(workspaceText);
  const next = {};
  for (const [path, text] of Object.entries(files)) {
    let updated;
    if (path === "eng/workspace.json") updated = workspaceText;
    else if (path === "eng/Versions.props")
      updated = Object.entries(separateVersions).filter(([name]) => findEntry(after, name))
        .reduce((props, [name, property]) => replaceVersion(props, propertyVersion(property), packageVersion(after, name)), replaceVersion(text, sdkVersion, version));
    else if (path === "bun.lock") updated = npm.reduce((lock, entry) => replaceVersion(lock, lockVersion(entry), version), text);
    else if (path === viewsVersionFile) updated = replaceVersion(text, viewsVersion, version);
    else if (isNugetLock(path))
      updated = [...new Set([...nugetProjectRanges(text)].map(({ name }) => name))]
        .reduce((lock, name) => lock.replaceAll(`"${name}": "[${packageVersion(before, name)}, )"`, `"${name}": "[${packageVersion(after, name)}, )"`), text);
    else {
      updated = replaceVersion(text, manifestVersion, version);
      for (const { name } of npm)
        updated = updated.replaceAll(`"${name}": "${previous}"`, `"${name}": "${version}"`);
    }
    next[path] = updated;
  }
  assert.equal(checkVersions(next), version);
  return next;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const usage = "Usage: bun eng/version.mjs check | bun eng/version.mjs bump <version> [--cswebui <version>]";
  const [command, version, ...options] = process.argv.slice(2);
  if (command === "check") {
    const files = readVersionFiles();
    const train = checkVersions(files);
    const workspace = JSON.parse(files["eng/workspace.json"]);
    const separate = Object.keys(separateVersions).filter(name => packageVersion(workspace, name) !== train)
      .map(name => `; ${name} ${packageVersion(workspace, name)}`).join("");
    console.log(`Release-train version ${train} is consistent${separate}.`);
  } else if (command === "bump") {
    if (options.length && (options.length !== 2 || options[0] !== "--cswebui")) throw new Error(usage);
    const packageVersions = options.length ? { "Runic.Application.Views.CsWebUi": options[1] } : {};
    const files = readVersionFiles();
    for (const [path, text] of Object.entries(bumpVersionFiles(files, version, packageVersions)))
      if (text !== files[path]) writeFileSync(resolve(root, path), text);
    // Let Bun normalize the lockfile, then confirm it still agrees.
    execFileSync(process.execPath, ["install", "--lockfile-only"], { cwd: root, stdio: "inherit" });
    assert.equal(checkVersions(readVersionFiles()), version, "bun install --lockfile-only changed a version copy");
    execFileSync(process.execPath, [resolve(root, "tools/dotnet-runic/metadata/generate.mjs"), "--write"], { stdio: "inherit" });
    console.log(`Set the release-train version to ${version}. Record user-facing changes in eng/release/notes/${version}.md; RunicPackageValidationBaselineVersion stays at the last published release.`);
  } else {
    throw new Error(usage);
  }
}
