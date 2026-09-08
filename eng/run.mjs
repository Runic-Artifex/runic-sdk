#!/usr/bin/env bun
import { nodeCompatibility } from "./node-compatibility.mjs";
import { spawnSync, execFileSync } from "node:child_process";
import { packNpm } from "./preview/pack-npm.mjs";
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
function web(command) {
  for (const p of orderedPackages()) {
    if (manifest(p.path).scripts?.[command])
      run("bun", ["run", "--bun", command], resolve(root, p.path));
  }
  // On a clean checkout Bun cannot link workspace executables until their
  // compiled entry files exist. Refresh links before building consuming apps.
  if (command === "build") run("bun", ["install", "--frozen-lockfile"]);
}
function core() {
  run("dotnet", [
    "build",
    "RunicSdk.Core.slnx",
    "-c",
    configuration,
    "--nologo",
  ]);
}
function build() {
  core();
  web("build");
  run("dotnet", [
    "build",
    "apps/translations-editor/Runic.Translations.Editor.csproj",
    "-c",
    configuration,
    "--nologo",
  ]);
  run("dotnet", [
    "build",
    "examples/customer-migration/Host/CustomerDesktop.csproj",
    "-c",
    configuration,
    "--nologo",
  ]);
  run("dotnet", [
    "build",
    "examples/document-migration/Host/DocumentDesktop.csproj",
    "-c",
    configuration,
    "--nologo",
  ]);
  run("bun", ["run", "--bun", "build"], resolve(root, "docs"));
}
function pack(built = false) {
  if (!built) { core(); web("build"); }
  const nuget = resolve(root, "artifacts/packages/nuget");
  const npm = resolve(root, "artifacts/packages/npm");
  mkdirSync(nuget, { recursive: true });
  mkdirSync(npm, { recursive: true });
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
    ]);
  }
  const revision = execFileSync("git", ["rev-parse", "HEAD"], { cwd: root, encoding: "utf8" }).trim();
  for (const p of orderedPackages())
    packNpm(resolve(root, p.path), npm, revision);
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
  // Always use this checkout's compiler, even if an older checkout exported an override.
  process.env.RUNIC_BRIDGE_INSPECTOR = resolve(
    root,
    `tools/Runic.Application.Bridge.Inspector/bin/${configuration}/net10.0/Runic.Application.Bridge.Inspector.dll`,
  );
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
    case "verify-packages":
      await (await import("./verify-packages.mjs")).verifyPackages();
      break;
    case "affected":
      affected();
      break;
    case "example:customers":
      core();
      web("build");
      run("dotnet", [
        "run",
        "--project",
        "examples/customer-migration/Host/CustomerDesktop.csproj",
        "-c",
        configuration,
        "--",
        ...process.argv.slice(3),
      ]);
      break;
    case "verify:customers":
      core();
      web("build");
      run("dotnet", [
        "build",
        "examples/customer-migration/Host/CustomerDesktop.csproj",
        "-c",
        configuration,
      ]);
      run(
        "bun",
        ["run", "test:browser"],
        resolve(root, "examples/customer-migration/Host/Frontend"),
      );
      break;
    case "dev:docs":
      run("bun", ["run", "dev"], resolve(root, "docs"));
      break;
    case "dev:editor":
      core();
      web("build");
      run("dotnet", [
        "run",
        "--project",
        "apps/translations-editor/Runic.Translations.Editor.csproj",
        "-c",
        configuration,
      ]);
      break;
    default:
      throw new Error(
        "Use bootstrap, build, build-core, build-web, pack, pack-built, verify-packages, affected, example:customers, verify:customers, dev:docs, or dev:editor. Run bun run ci for workflow verification.",
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
