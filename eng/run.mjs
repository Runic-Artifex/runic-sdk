#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { readFileSync, mkdirSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

export const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
export const workspace = JSON.parse(
  readFileSync(resolve(root, "eng/workspace.json"), "utf8"),
);
export const configuration = process.env.CONFIGURATION ?? "Debug";
const executable = (name) =>
  process.platform === "win32" && ["bun", "npm", "pnpm"].includes(name)
    ? `${name}.cmd`
    : name;
export function run(command, args, cwd = root, extraEnv = {}) {
  console.log(
    `\n> ${command} ${args.join(" ")} (${cwd === root ? "/" : cwd.startsWith(root) ? cwd.slice(root.length) : cwd})`,
  );
  const result = spawnSync(executable(command), args, {
    cwd,
    stdio: "inherit",
    env: { ...process.env, ...extraEnv },
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
      run("bun", ["run", command], resolve(root, p.path));
  }
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
  run("bun", ["run", "build"], resolve(root, "docs"));
}
function test() {
  run("node", ["--test", "eng/workspace.test.mjs"]);
  run("node", ["--test", "examples/eng/current-*/*.test.mjs"]);
  run("dotnet", [
    "test",
    "packages/runic-desktop/tests/Runic.Desktop.Tests",
    "-c",
    configuration,
    "--no-build",
    "--nologo",
  ]);
  const solution = readFileSync(resolve(root, "RunicSdk.Core.slnx"), "utf8");
  for (const [, path] of solution.matchAll(/<Project Path="([^"]+)"/g)) {
    const project = readFileSync(resolve(root, path), "utf8");
    if (
      /Tests\.csproj$/.test(path) &&
      /<OutputType>Exe<\/OutputType>/.test(project)
    ) {
      run(
        "dotnet",
        [
          "run",
          "--project",
          resolve(root, path),
          "-c",
          configuration,
          "--no-build",
        ],
        resolve(root, path.split("/").slice(0, 2).join("/")),
      );
    }
  }
  run("dotnet", [
    "run",
    "--project",
    "examples/current/counter/Counter.csproj",
    "-c",
    configuration,
    "--no-build",
  ]);
  web("test");
  for (const path of [
    "packages/runic-svelte/packages/svelte",
    "packages/runic-svelte/packages/sveltekit",
    "apps/translations-editor/Frontend",
  ]) {
    run("bun", ["run", "check"], resolve(root, path));
  }
  run(
    "bun",
    ["run", "verify:application-bridge-artifacts"],
    resolve(root, "packages/runic-toolkit"),
  );
  run("bun", ["run", "check"], resolve(root, "docs"));
  run("bun", ["run", "test"], resolve(root, "docs"));
}
function pack() {
  // Pack is also usable on a clean checkout: bundled tools and generators must exist first.
  core();
  web("build");
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
      "--no-restore",
      "-o",
      nuget,
      `-p:PackageVersion=${workspace.version}`,
    ]);
  }
  for (const p of orderedPackages())
    run("bun", ["pm", "pack", "--destination", npm], resolve(root, p.path));
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
  const components = Object.entries(workspace.components);
  const global = files.some(
    (file) => !components.some(([, c]) => file.startsWith(`${c.path}/`)),
  );
  const selected = new Set(
    components
      .filter(
        ([, c]) =>
          global || files.some((file) => file.startsWith(`${c.path}/`)),
      )
      .map(([name]) => name),
  );
  let previous;
  do {
    previous = selected.size;
    for (const [name, c] of components)
      if (c.dependsOn.some((d) => selected.has(d))) selected.add(name);
  } while (selected.size !== previous);
  console.log(JSON.stringify([...selected], null, 2));
}
async function main() {
  const command = process.argv[2];
  process.env.NUGET_PACKAGES ??= resolve(root, ".cache/nuget");
  // Always use this checkout's compiler, even if an older checkout exported an override.
  process.env.RUNIC_BRIDGE_INSPECTOR = resolve(
    root,
    `packages/runic-toolkit/tools/Runic.Application.Bridge.Inspector/bin/${configuration}/net10.0/Runic.Application.Bridge.Inspector.dll`,
  );
  switch (command) {
    case "bootstrap":
      run("bun", ["install", "--frozen-lockfile"]);
      run("dotnet", ["restore", "RunicSdk.Core.slnx"]);
      break;
    case "build":
      build();
      break;
    case "test":
      test();
      break;
    case "verify":
      build();
      test();
      pack();
      await (await import("./verify-packages.mjs")).verifyPackages();
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
        "Use bootstrap, build, test, verify, pack, verify-packages, affected, dev:docs, or dev:editor.",
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
