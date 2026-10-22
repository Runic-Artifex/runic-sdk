#!/usr/bin/env bun
// PackageDownload packages are outside the NuGet lock files: the package validation baselines
// (the previous release's Runic.* packages), the WindowsDesktop targeting pack on Linux and macOS,
// and the runtime and ILCompiler packs of a RID-specific or NativeAOT restore. After a restore,
// `check` compares every one that a project.assets.json under the checkout names with
// package-downloads.json: its id, its version and its content hash, the SHA-512 that NuGet records
// in .nupkg.metadata when it downloads the package and that packages.lock.json also uses. `write`
// adds the restored ones to the list.
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
export const listPath = join(root, "eng/dependencies/package-downloads.json");
const skipped = new Set([".git", ".cache", ".direnv", "node_modules", "artifacts", "bin"]);

// Every obj/project.assets.json below `base`, skipping caches, outputs and other checkouts' files.
export function assetsFiles(base = root) {
  const found = [];
  const walk = directory => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (!entry.isDirectory() || skipped.has(entry.name)) continue;
      const path = join(directory, entry.name);
      if (entry.name === "obj" && existsSync(join(path, "project.assets.json"))) found.push(join(path, "project.assets.json"));
      walk(path);
    }
  };
  walk(base);
  return found.sort();
}

// The PackageDownload items of one project.assets.json. PackageDownload takes exact versions only.
export function downloadsOf(assets) {
  return Object.values(assets.project?.frameworks ?? {}).flatMap(framework => (framework.downloadDependencies ?? []).map(item => {
    const version = /^\[([^,\]\s]+)(?:,\s*\1)?\]$/.exec(item.version)?.[1];
    if (!version) throw new Error(`PackageDownload ${item.name} has no exact version: ${item.version}`);
    return { id: item.name, version };
  }));
}

const key = ({ id, version }) => `${id.toLowerCase()}/${version.toLowerCase()}`;
const unique = items => [...new Map(items.map(item => [key(item), item])).values()]
  .sort((a, b) => key(a).localeCompare(key(b), "en"));

// The content hash NuGet recorded for a package in the global packages folder: the base64 SHA-512
// of the package without its repository signature, so it does not change when nuget.org signs it.
export function contentHash({ id, version }, packages) {
  const metadata = join(packages, id.toLowerCase(), version.toLowerCase(), ".nupkg.metadata");
  if (!existsSync(metadata)) throw new Error(`${id} ${version} has no ${metadata}.`);
  const hash = JSON.parse(readFileSync(metadata, "utf8")).contentHash;
  if (!/^[A-Za-z0-9+/]{86}==$/.test(hash ?? "")) throw new Error(`${metadata} has no SHA-512 content hash.`);
  return hash;
}

// Problems with the restored downloads, compared with the list. A listed package that this restore
// did not download is fine: other platforms and RIDs download other packs.
export function compare(list, downloads, hashOf) {
  const listed = new Map(list.map(item => [key(item), item]));
  const problems = [];
  for (const item of unique(downloads)) {
    const entry = listed.get(key(item));
    if (!entry) { problems.push(`${item.id} ${item.version} is not listed.`); continue; }
    let hash;
    try { hash = hashOf(item); } catch (error) { problems.push(error.message); continue; }
    if (hash !== entry.contentHash) problems.push(`${item.id} ${item.version} has content hash ${hash}; the list has ${entry.contentHash}.`);
  }
  return problems;
}

// The list with the restored downloads added. An entry for another version of a restored id is
// dropped, so moving a baseline or the SDK replaces the old versions.
export function merge(list, restored) {
  const ids = new Set(restored.map(item => item.id.toLowerCase()));
  return unique([...list.filter(item => !ids.has(item.id.toLowerCase())), ...restored]);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const command = process.argv[2] ?? "check";
  const packages = process.env.NUGET_PACKAGES || join(homedir(), ".nuget", "packages");
  const files = assetsFiles();
  const downloads = unique(files.flatMap(file => downloadsOf(JSON.parse(readFileSync(file, "utf8")))));
  const list = JSON.parse(readFileSync(listPath, "utf8"));
  if (command === "check") {
    const problems = compare(list, downloads, item => contentHash(item, packages));
    if (problems.length) {
      console.error(problems.join("\n"));
      console.error("Restore again from nuget.org; if the change is intended (a new SDK or validation baseline), run `bun eng/dependencies/package-downloads.mjs write` after the restore and review the diff.");
      process.exit(1);
    }
    console.log(`${downloads.length} PackageDownload packages from ${files.length} restored projects match eng/dependencies/package-downloads.json.`);
  } else if (command === "write") {
    const restored = downloads.map(item => ({ id: item.id, version: item.version, contentHash: contentHash(item, packages) }));
    writeFileSync(listPath, `${JSON.stringify(merge(list, restored), null, 2)}\n`);
    console.log(`Recorded ${restored.length} PackageDownload packages.`);
  } else throw new Error("Use check or write.");
}
