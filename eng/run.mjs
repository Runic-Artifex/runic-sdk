#!/usr/bin/env bun
import { nodeCompatibility } from "./node-compatibility.mjs";
import { spawnSync, execFileSync } from "node:child_process";
import { packNpm } from "./release/pack-npm.mjs";
import { authority, scan } from "./release/artifacts.mjs";
import { stageAndPromote } from "./release/stage.mjs";
import { readFileSync, mkdirSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

export const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
export const workspace = JSON.parse(
  readFileSync(resolve(root, "eng/workspace.json"), "utf8"),
);
export const configuration = process.env.CONFIGURATION ?? "Debug";
const executable = (name) =>
  process.platform === "win32" && ["npm", "pnpm"].includes(name)
    ? `${name}.cmd`
    : name;
export function run(command, args, cwd = root, extraEnv = {}) {
  console.log(
    `\n> ${command} ${args.join(" ")} (${cwd === root ? "/" : cwd.startsWith(root) ? cwd.slice(root.length) : cwd})`,
  );
  const launchArgs = command === "bun" && args[0] === "run" && !args.includes("--bun")
    ? ["run", "--bun", ...args.slice(1)] : args;
  const compatibility = ["node", "npm", "pnpm"].includes(command) ? nodeCompatibility() : null;
  const result = spawnSync(command === "node" ? compatibility.executable : executable(command), launchArgs, {
    cwd,
    stdio: "inherit",
    env: { ...(compatibility?.env ?? process.env), ...extraEnv },
  });
  if (result.error) throw result.error;
  if (result.status !== 0)
    throw new Error(`${command} exited with ${result.status ?? result.signal}`);
}
const manifest = (path) =>
  JSON.parse(readFileSync(resolve(root, path, "package.json"), "utf8"));
function orderedPackages() {
  const pending = new Map(workspace.npm.map((p) => [p.name, p]));
  const ordered = [];
  while (pending.size) {
    const ready = [...pending.values()].filter((p) => {
      const m = manifest(p.path);
      return Object.keys({
        ...m.dependencies,
        ...m.devDependencies,
        ...m.peerDependencies,
      }).every((name) => !pending.has(name));
    });
    if (!ready.length)
      throw new Error(`npm dependency cycle: ${[...pending.keys()]}`);
    for (const p of ready) {
      ordered.push(p);
      pending.delete(p.name);
    }
  }
  return ordered;
}
function web(command, built = []) {
  for (const p of orderedPackages()) {
    if (manifest(p.path).scripts?.[command] && !built.includes(p.name))
      run("bun", ["run", "--bun", command], resolve(root, p.path));
  }
  // On a clean checkout Bun cannot link workspace executables until their
  // compiled entry files exist. Refresh links before building consuming apps.
  if (command === "build") run("bun", ["install", "--frozen-lockfile"]);
}
// Examples and test projects bundle or type-check generated Views clients,
// which import the shared runtime package. Build it before managed projects.
export function viewsRuntime() {
  run("bun", ["run", "--bun", "build"], resolve(root, "packages/web/views"));
}
function core() {
  viewsRuntime();
  run("dotnet", [
    "build",
    "RunicSdk.Core.slnx",
    "-c",
    configuration,
    "-m:1",
    "--nologo",
  ]);
}
function build() {
  core();
  // core() has just built the Views runtime.
  web("build", ["@runic-artifex/views"]);
}
export const packages = resolve(root, "artifacts/packages");
function pack(built = false) {
  run("bun", ["eng/generate-shipping-projects.mjs", "--check"]);
  if (!built) build();
  const revision = execFileSync("git", ["rev-parse", "HEAD"], { cwd: root, encoding: "utf8" }).trim();
  // Build the complete candidate in a sibling directory and replace
  // artifacts/packages only after every package and check succeeds.
  stageAndPromote(packages, (staging) => {
    const nuget = resolve(staging, "nuget");
    const npm = resolve(staging, "npm");
    mkdirSync(nuget);
    mkdirSync(npm);
    // Template lock integrities must describe the final gitHead-stamped archives.
    for (const p of orderedPackages())
      packNpm(resolve(root, p.path), npm, revision);
    for (const p of workspace.nuget) {
      run("dotnet", [
        "pack",
        p.project,
        "-c",
        configuration,
        ...(built ? ["--no-build"] : ["--no-restore"]),
        "-o",
        nuget,
        `-p:PackageVersion=${workspace.version}`,
        ...(p.name.endsWith(".Templates") ? [`-p:RunicTemplateNpmDirectory=${npm}`] : []),
      ]);
    }
    run("bun", ["eng/release/verify-template-locks.mjs", nuget, npm]);
    const count = scan(staging, authority(workspace), revision).length;
    console.log(`Staged ${count} packages; promoting to ${packages}.`);
  });
}
async function verifyCandidate() {
  pack();
  await (await import("./verify-packages.mjs")).verifyPackages();
  run("bun", ["eng/verify-templates.mjs"]);
  console.log(`Candidate ${workspace.version} in ${packages} passed package and template verification.`);
}
function affected() {
  const base = process.argv[3];
  if (!base) throw new Error("Usage: bun run affected <base-ref>");
  const result = spawnSync("git", ["diff", "--name-only", base, "--"], {
    cwd: root,
    encoding: "utf8",
  });
  if (result.status !== 0) throw new Error(result.stderr);
  const untracked = spawnSync(
    "git",
    ["ls-files", "--others", "--exclude-standard"],
    { cwd: root, encoding: "utf8" },
  );
  if (untracked.status !== 0) throw new Error(untracked.stderr);
  const files = `${result.stdout}\n${untracked.stdout}`
    .trim()
    .split("\n")
    .filter(Boolean);
  console.log(JSON.stringify(affectedComponents(files), null, 2));
}

export function affectedComponents(files) {
  const components = Object.entries(workspace.components);
  const owns = (component, file) =>
    component.paths.some(
      (path) => file === path || file.startsWith(`${path}/`),
    );
  const global = files.some(
    (file) => !components.some(([, c]) => owns(c, file)),
  );
  const selected = new Set(
    components
      .filter(([, c]) => global || files.some((file) => owns(c, file)))
      .map(([name]) => name),
  );
  let previous;
  do {
    previous = selected.size;
    for (const [name, c] of components)
      if (c.dependsOn.some((d) => selected.has(d))) selected.add(name);
  } while (selected.size !== previous);
  return [...selected];
}
async function main() {
  const command = process.argv[2];
  process.env.NUGET_PACKAGES ??= resolve(root, ".cache/nuget");
  switch (command) {
    case "bootstrap":
      run("bun", ["install", "--frozen-lockfile"]);
      run("dotnet", ["restore", "RunicSdk.Core.slnx"]);
      break;
    case "build":
      build();
      break;
    case "build-core":
      core();
      break;
    case "pack-built":
      pack(true);
      break;
    case "build-web":
      web("build");
      break;
    case "pack":
      pack();
      break;
    case "verify-packages": {
      const args = process.argv.slice(3);
      await (await import("./verify-packages.mjs")).verifyPackages(
        args.find((arg) => !arg.startsWith("--")),
        { keep: args.includes("--keep") },
      );
      break;
    }
    case "verify:candidate":
      await verifyCandidate();
      break;
    case "affected":
      affected();
      break;
    case "example:first-window":
    case "example:notes":
      core();
      web("build");
      run("dotnet", [
        "run",
        "--project",
        command === "example:notes"
          ? "examples/notes-view-first/NotesViewFirst.csproj"
          : "examples/first-window/FirstWindow.csproj",
        "-c",
        configuration,
        "--",
        ...process.argv.slice(3),
      ]);
      break;
    default:
      throw new Error(
        "Use bootstrap, build, build-core, build-web, pack, pack-built, verify-packages [--keep] [package], verify:candidate, affected, example:first-window, example:notes. Run bun run ci for workflow verification.",
      );
  }
}
if (
  process.argv[1] &&
  resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
