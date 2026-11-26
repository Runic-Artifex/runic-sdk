#!/usr/bin/env node
// Resolves a frontend's JavaScript workspace (rax.frontend-graph/1, runic-sdk devtools-library-dx §2). One copy ships in
// the Runic.Application.Views NuGet package (buildTransitive/runic-frontend-graph.mjs), whose targets run it, and the
// same file ships in @runic-artifex/views as the `./frontend-graph` export, so that other build systems resolve a
// workspace the way MSBuild does. Both come from one commit and carry one version. It needs Node.js 20 or Bun and no
// dependency:
//
//   runic-frontend-graph.mjs --frontend <directory> [--root <workspace root>] [--package-manager <npm|pnpm|bun>]
//     [--out <graph.json>] [--msbuild <lines file>]
//
// --root is the folder the build took as the workspace root: the nearest folder above the frontend whose package.json
// has `workspaces` or that has pnpm-workspace.yaml (without --root, the script looks for it the same way). The script
// expands the root's `workspaces` (package.json, an array or `{ "packages": [...] }`) or `packages`
// (pnpm-workspace.yaml) and reports whether the frontend is a member.
//
// --out writes the graph as JSON:
//   { "schema": "rax.frontend-graph/1", "workspaceRoot", "packageManager", "lockFile", "member",
//     "application": { "directory", "package" }, "members": [{ "directory", "package" }], "packages": [] }
// --msbuild writes the same facts as `name=value` lines for MSBuild's ReadLinesFromFile: `root=`, `member=`,
// `lockFile=`, and one `manifest=` per package.json of the workspace (the root's first).
//
// Exit code 0 when the graph was written, also when the frontend is not a member (the build reports that as
// RUNICBRIDGE087); 1 with an `error RUNICBRIDGE024:` line when an input cannot be read.
import { existsSync, mkdirSync, readFileSync, readdirSync, realpathSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

export const SCHEMA = "rax.frontend-graph/1";
const LOCK_FILES = ["bun.lock", "bun.lockb", "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml"];

function parseArguments(argv) {
  const names = new Map([["--frontend", "frontend"], ["--root", "root"], ["--package-manager", "packageManager"],
    ["--out", "out"], ["--msbuild", "msbuild"]]);
  const options = {};
  for (let index = 0; index < argv.length; index++) {
    const name = names.get(argv[index]);
    if (name === undefined || index + 1 >= argv.length) throw new Error(`unknown or incomplete argument ${argv[index]}.`);
    options[name] = argv[++index];
  }
  if (!options.frontend) throw new Error("--frontend is required.");
  return options;
}

const readJson = file => JSON.parse(readFileSync(file, "utf8"));
const canonical = directory => {
  try { return realpathSync.native(directory); } catch { return resolve(directory); }
};
const samePath = (left, right) => process.platform === "win32" || process.platform === "darwin"
  ? left.toLowerCase() === right.toLowerCase() : left === right;

/** The patterns of a workspace root: package.json `workspaces`, else pnpm-workspace.yaml `packages`. */
export function workspacePatterns(root) {
  const manifest = join(root, "package.json");
  if (existsSync(manifest)) {
    const workspaces = readJson(manifest).workspaces;
    const patterns = Array.isArray(workspaces) ? workspaces : Array.isArray(workspaces?.packages) ? workspaces.packages : undefined;
    if (patterns) return patterns.filter(pattern => typeof pattern === "string");
  }
  const pnpm = join(root, "pnpm-workspace.yaml");
  if (existsSync(pnpm)) return pnpmPackages(readFileSync(pnpm, "utf8"));
  return [];
}

// The `packages:` list of pnpm-workspace.yaml, in its block form; other keys and comments are skipped.
function pnpmPackages(text) {
  const patterns = [];
  let inPackages = false;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.replace(/\s+#.*$/, "");
    if (/^packages\s*:/.test(line)) {
      inPackages = true;
      const inline = /^packages\s*:\s*\[(.*)\]\s*$/.exec(line);
      if (inline) {
        patterns.push(...inline[1].split(",").map(unquote).filter(Boolean));
        inPackages = false;
      }
      continue;
    }
    if (!inPackages) continue;
    const item = /^\s+-\s*(.+?)\s*$/.exec(line);
    if (item) patterns.push(unquote(item[1]));
    else if (/^\S/.test(line)) inPackages = false;
  }
  return patterns;
}

const unquote = value => value.trim().replace(/^(["'])(.*)\1$/, "$2");

const segmentPattern = segment => new RegExp(`^${segment.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".")}$`);
const skipped = name => name === "node_modules" || name.startsWith(".");

function directories(folder) {
  try {
    return readdirSync(folder, { withFileTypes: true }).filter(entry => entry.isDirectory() && !skipped(entry.name)).map(entry => entry.name);
  } catch {
    return [];
  }
}

// Expands one pattern segment by segment, so that only the folders it can match are read.
function expand(root, segments, index, found) {
  if (index === segments.length) {
    if (existsSync(join(root, "package.json"))) found.add(root);
    return;
  }
  const segment = segments[index];
  if (segment === "" || segment === ".") return expand(root, segments, index + 1, found);
  if (segment === "**") {
    expand(root, segments, index + 1, found);
    for (const name of directories(root)) expand(join(root, name), segments, index, found);
    return;
  }
  if (!/[*?]/.test(segment)) {
    const next = join(root, segment);
    if (existsSync(next) && statSync(next).isDirectory()) expand(next, segments, index + 1, found);
    return;
  }
  const matcher = segmentPattern(segment);
  for (const name of directories(root)) if (matcher.test(name)) expand(join(root, name), segments, index + 1, found);
}

const globPattern = pattern => new RegExp(`^${pattern.split("/").map(segment => segment === "**" ? "(?:.*)" : segment
  .replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, "[^/]*").replace(/\?/g, "[^/]")).join("/").replace(/\/\(\?:\.\*\)/g, "(?:/.*)?")}/?$`);

/** The member folders (absolute, canonical) of the workspace at `root`, in pattern order. */
export function workspaceMembers(root) {
  const patterns = workspacePatterns(root).map(pattern => pattern.replace(/\\/g, "/").replace(/^\.\//, "").replace(/\/+$/, ""));
  const included = new Set();
  for (const pattern of patterns.filter(pattern => !pattern.startsWith("!"))) expand(root, pattern.split("/"), 0, included);
  const excluded = patterns.filter(pattern => pattern.startsWith("!")).map(pattern => globPattern(pattern.slice(1).replace(/^\.\//, "")));
  return [...included]
    .filter(member => !excluded.some(matcher => matcher.test(relative(root, member).split(sep).join("/"))))
    .map(canonical);
}

/** The nearest folder above `directory` that declares a workspace, or undefined. */
export function findWorkspaceRoot(directory) {
  for (let folder = dirname(resolve(directory)); ; folder = dirname(folder)) {
    if (existsSync(join(folder, "pnpm-workspace.yaml"))) return folder;
    const manifest = join(folder, "package.json");
    if (existsSync(manifest)) {
      try { if (readJson(manifest).workspaces !== undefined) return folder; } catch {}
    }
    if (dirname(folder) === folder) return undefined;
  }
}

const packageName = directory => {
  try { return readJson(join(directory, "package.json")).name ?? null; } catch { return null; }
};

/** The workspace graph of one frontend (rax.frontend-graph/1). */
export function resolveFrontendGraph({ frontend, root, packageManager }) {
  const directory = canonical(frontend);
  const workspaceRoot = canonical(root ?? findWorkspaceRoot(directory) ?? directory);
  const standalone = samePath(workspaceRoot, directory);
  const members = standalone ? [] : workspaceMembers(workspaceRoot);
  const member = standalone || members.some(candidate => samePath(candidate, directory));
  const lock = LOCK_FILES.map(name => join(workspaceRoot, name)).find(existsSync) ?? null;
  return {
    schema: SCHEMA,
    workspaceRoot,
    packageManager: packageManager ?? null,
    lockFile: lock,
    member,
    application: { directory, package: packageName(directory) },
    members: members.map(folder => ({ directory: folder, package: packageName(folder) })),
    packages: [],
  };
}

function msbuildLines(graph) {
  const manifests = [join(graph.workspaceRoot, "package.json"), ...graph.members.map(member => join(member.directory, "package.json"))];
  if (!graph.members.some(member => samePath(member.directory, graph.application.directory)))
    manifests.push(join(graph.application.directory, "package.json"));
  return [`root=${graph.workspaceRoot}`, `member=${graph.member}`, `lockFile=${graph.lockFile ?? ""}`,
    ...[...new Set(manifests)].filter(existsSync).map(file => `manifest=${file}`)];
}

function write(file, text) {
  mkdirSync(dirname(resolve(file)), { recursive: true });
  // Unchanged content keeps the file's time, so that MSBuild's inputs stay up to date.
  if (existsSync(file) && readFileSync(file, "utf8") === text) return;
  writeFileSync(file, text);
}

function main(argv) {
  let options;
  try {
    options = parseArguments(argv);
    const graph = resolveFrontendGraph(options);
    if (options.out) write(options.out, `${JSON.stringify(graph, null, 2)}\n`);
    if (options.msbuild) write(options.msbuild, `${msbuildLines(graph).join("\n")}\n`);
    if (!options.out && !options.msbuild) process.stdout.write(`${JSON.stringify(graph, null, 2)}\n`);
    return 0;
  } catch (error) {
    console.log(`runic-frontend-graph: error RUNICBRIDGE024: the frontend workspace of ${options?.frontend ?? "the frontend"} could not be resolved: ${error instanceof Error ? error.message : String(error)}`);
    return 1;
  }
}

const invoked = process.argv[1] && samePath(canonical(process.argv[1]), canonical(fileURLToPath(import.meta.url)));
if (invoked) process.exitCode = main(process.argv.slice(2));
