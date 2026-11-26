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
// `packages` lists the frontend packages that the frontend depends on, dependencies first: each dependency (also a
// dependency's own) that names a workspace member, by `workspace:` or by name, or a folder, by `file:`, `link:` or
// `portal:`. A package with a `build` script is `compiled`, otherwise `source`; `exports` are the files that its
// package.json's `exports`, `main`, `module` and `types` name, and `outputs` the folders of a compiled package's that
// its build writes (the first folder of each export other than `src`).
//
// --out writes the graph as JSON:
//   { "schema": "rax.frontend-graph/1", "workspaceRoot", "packageManager", "lockFile", "member",
//     "application": <package>, "members": [{ "directory", "package" }], "packages": [<package>] }
//   <package>: { "directory", "package", "kind", "build", "watch", "exports", "generatedExport", "outputs" }
// --msbuild writes the same facts as `name=value` lines for MSBuild's ReadLinesFromFile: `root=`, `member=`,
// `lockFile=`, one `manifest=` per package.json of the workspace and of each package (the root's first), the
// frontend's own `kind=`, `export=` (one per file) and `generatedExport=` (the folder of the `./generated` export), and
// per package `package=<directory>|<name>|<kind>|<build command>|<watch command>`, `package.export=<directory>|<file>`
// and `package.output=<directory>|<folder>`. Paths are absolute.
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

const readManifest = directory => {
  try { return readJson(join(directory, "package.json")) ?? {}; } catch { return {}; }
};
const packageName = directory => readManifest(directory).name ?? null;

const DEPENDENCY_FIELDS = ["dependencies", "devDependencies", "optionalDependencies"];

/** The command that runs a package script with the workspace's package manager, as the Views targets run it. */
export const scriptCommand = (packageManager, script) =>
  packageManager === "bun" ? `bun run --bun ${script}` : `${packageManager === "pnpm" ? "pnpm" : "npm"} run ${script}`;

// The files an `exports` value names, depth first: a string, an array, or an object of subpaths or conditions.
function exportTargets(value, found = []) {
  if (typeof value === "string") found.push(value);
  else if (Array.isArray(value)) for (const item of value) exportTargets(item, found);
  else if (value && typeof value === "object") for (const item of Object.values(value)) exportTargets(item, found);
  return found;
}

// The file of the `./generated` export that declares its types: `types`, else `default` or `import`, a level deep.
function generatedExportTarget(exports) {
  let value = exports && typeof exports === "object" && !Array.isArray(exports) ? exports["./generated"] : undefined;
  for (let depth = 0; depth < 3 && value && typeof value === "object" && !Array.isArray(value); depth++)
    value = value.types ?? value.default ?? value.import;
  return typeof value === "string" ? value : undefined;
}

const relativeTarget = target => target.replace(/\\/g, "/").replace(/^(?!\.\/)/, "./");

/** A frontend package as the graph lists it; `packageManager` selects the build and watch commands. */
export function describePackage(directory, packageManager) {
  const manifest = readManifest(directory);
  const scripts = manifest.scripts && typeof manifest.scripts === "object" ? manifest.scripts : {};
  const compiled = typeof scripts.build === "string";
  const named = [manifest.main, manifest.module, manifest.types, manifest.typings].filter(value => typeof value === "string");
  // A pattern export (`./*`) names no single file.
  const exports = [...new Set([...exportTargets(manifest.exports), ...named].filter(target => typeof target === "string" && !target.includes("*")).map(relativeTarget))];
  const outputs = compiled
    ? [...new Set(exports.map(target => target.slice(2).split("/")).filter(parts => parts.length > 1 && parts[0] !== "src" && parts[0] !== "..").map(parts => parts[0]))]
    : [];
  const generated = generatedExportTarget(manifest.exports);
  return {
    directory,
    package: manifest.name ?? null,
    kind: compiled ? "compiled" : "source",
    build: compiled ? scriptCommand(packageManager, "build") : null,
    watch: typeof scripts.watch === "string" ? scriptCommand(packageManager, "watch") : null,
    exports,
    generatedExport: generated ? relativeTarget(dirname(relativeTarget(generated))) : null,
    outputs: outputs.map(folder => `./${folder}`),
  };
}

// The local packages that one package.json depends on: workspace members, by `workspace:` or by name, and folders.
function localDependencies(directory, membersByName) {
  const manifest = readManifest(directory);
  const found = [];
  for (const field of DEPENDENCY_FIELDS) {
    const dependencies = manifest[field];
    if (!dependencies || typeof dependencies !== "object") continue;
    for (const [name, specification] of Object.entries(dependencies)) {
      if (typeof specification !== "string") continue;
      let target;
      const path = /^(?:file|link|portal|workspace):(\.{1,2}[\\/].*|\/.*)$/.exec(specification)?.[1];
      if (path !== undefined) {
        const folder = resolve(directory, path);
        if (existsSync(join(folder, "package.json"))) target = canonical(folder);
      } else if (specification.startsWith("workspace:")) {
        // workspace:*, workspace:^1.0.0, or an alias, workspace:@acme/real-name@*.
        target = membersByName.get(/^workspace:(@?[^@]+)@/.exec(specification)?.[1] ?? name);
      } else if (!/^[a-z]+:/i.test(specification)) {
        // npm workspaces link a member that a dependency names with a version range.
        target = membersByName.get(name);
      }
      if (target) found.push(target);
    }
  }
  return found;
}

/** The frontend packages that `frontend` depends on, dependencies first (a cycle is cut where it closes). */
export function dependencyPackages(frontend, members, packageManager) {
  const membersByName = new Map(members.filter(member => member.package).map(member => [member.package, member.directory]));
  const seen = new Set();
  const order = [];
  const visit = directory => {
    for (const dependency of localDependencies(directory, membersByName)) {
      const key = process.platform === "win32" || process.platform === "darwin" ? dependency.toLowerCase() : dependency;
      if (samePath(dependency, frontend) || seen.has(key)) continue;
      seen.add(key);
      visit(dependency);
      order.push(dependency);
    }
  };
  visit(canonical(frontend));
  return order.map(directory => describePackage(directory, packageManager));
}

/** The workspace graph of one frontend (rax.frontend-graph/1). */
export function resolveFrontendGraph({ frontend, root, packageManager }) {
  const directory = canonical(frontend);
  const workspaceRoot = canonical(root ?? findWorkspaceRoot(directory) ?? directory);
  const standalone = samePath(workspaceRoot, directory);
  const members = (standalone ? [] : workspaceMembers(workspaceRoot)).map(folder => ({ directory: folder, package: packageName(folder) }));
  const member = standalone || members.some(candidate => samePath(candidate.directory, directory));
  const lock = LOCK_FILES.map(name => join(workspaceRoot, name)).find(existsSync) ?? null;
  const manager = packageManager ?? "npm";
  return {
    schema: SCHEMA,
    workspaceRoot,
    packageManager: packageManager ?? null,
    lockFile: lock,
    member,
    application: describePackage(directory, manager),
    members,
    packages: dependencyPackages(directory, members, manager),
  };
}

const absolute = (directory, target) => resolve(directory, target);

function msbuildLines(graph) {
  const manifests = [join(graph.workspaceRoot, "package.json"), ...graph.members.map(member => join(member.directory, "package.json"))];
  if (!graph.members.some(member => samePath(member.directory, graph.application.directory)))
    manifests.push(join(graph.application.directory, "package.json"));
  manifests.push(...graph.packages.map(entry => join(entry.directory, "package.json")));
  const self = graph.application;
  const lines = [`root=${graph.workspaceRoot}`, `member=${graph.member}`, `lockFile=${graph.lockFile ?? ""}`,
    ...[...new Set(manifests)].filter(existsSync).map(file => `manifest=${file}`),
    `kind=${self.kind}`, ...self.exports.map(target => `export=${absolute(self.directory, target)}`)];
  if (self.generatedExport) lines.push(`generatedExport=${absolute(self.directory, self.generatedExport)}`);
  for (const entry of graph.packages) {
    lines.push(`package=${[entry.directory, entry.package ?? "", entry.kind, entry.build ?? "", entry.watch ?? ""].join("|")}`);
    for (const target of entry.exports) lines.push(`package.export=${entry.directory}|${absolute(entry.directory, target)}`);
    for (const folder of entry.outputs) lines.push(`package.output=${entry.directory}|${absolute(entry.directory, folder)}`);
  }
  return lines;
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
