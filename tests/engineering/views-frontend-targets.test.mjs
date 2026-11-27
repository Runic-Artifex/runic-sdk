import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, readlinkSync, realpathSync, rmSync, statSync, symlinkSync, utimesSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { configuration, root } from "../../eng/run.mjs";

// The Views MSBuild targets install and build the frontend with external
// commands. These tests replace both commands with small scripts and check
// when the install runs, and that a failing command is reported once with a
// code instead of as MSB3073.
const targets = resolve(root, "packages/dotnet/Runic.Application.Views/build/Runic.Application.Views.targets");
let directory;

before(() => {
  directory = mkdtempSync(join(tmpdir(), "runic-views-frontend-"));
});

after(() => {
  rmSync(directory, { recursive: true, force: true });
});

function fixture(name, packageJson, projectItems = "", { defaultInstall = false } = {}) {
  const project = join(directory, name);
  const frontend = join(project, "frontend");
  mkdirSync(frontend, { recursive: true });
  writeFileSync(join(frontend, "package.json"), JSON.stringify(packageJson));
  writeFileSync(join(frontend, "install.mjs"), `
import { appendFileSync, existsSync, mkdirSync } from "node:fs";
appendFileSync("../commands.log", "install\\n");
if (existsSync("install-fails")) { console.error("npm error code E404"); process.exit(1); }
if (!existsSync("install-skips-modules")) mkdirSync("node_modules", { recursive: true });
`);
  writeFileSync(join(frontend, "build.mjs"), `
import { appendFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { sep } from "node:path";
appendFileSync("../commands.log", "build\\n");
appendFileSync("../host.log", (process.env.RUNIC_APPLICATION_HOST ?? "unset") + "\\n");
appendFileSync("../node-env.log", (process.env.NODE_ENV || "unset") + "\\n");
if (existsSync("emits-dist")) {
  // As Vite does: empty dist/, then write the page and an asset named by its content hash.
  const asset = existsSync("asset-name") ? readFileSync("asset-name", "utf8").trim() : "index-1";
  rmSync("dist", { recursive: true, force: true });
  mkdirSync("dist/assets", { recursive: true });
  writeFileSync("dist/index.html", \`<script src="runic-views.js"></script><script type="module" src="./assets/\${asset}.js"></script>\`);
  writeFileSync(\`dist/assets/\${asset}.js\`, "export {};");
}
if (existsSync("emits-build")) {
  // A frontend whose build writes to build/ instead of dist/.
  rmSync("build", { recursive: true, force: true });
  mkdirSync("build", { recursive: true });
  writeFileSync("build/index.html", "<p>build</p>");
}
if (existsSync("build-fails")) { console.error("error during build:"); console.error("[vite]: Rollup failed to resolve import"); process.exit(1); }
if (existsSync("unresolved-import")) { console.error(\`[vite]: Rolldown failed to resolve import "\${readFileSync("unresolved-import", "utf8").trim()}" from "src/main.ts".\`); process.exit(1); }
if (existsSync("emits-package")) {
  // A compiled library package, as tsc writes it: no .contract.d.ts in dist/generated.
  rmSync("dist", { recursive: true, force: true });
  mkdirSync("dist/generated", { recursive: true });
  if (!existsSync("skips-index")) writeFileSync("dist/index.js", "export {};");
  writeFileSync("dist/generated/runic-clients.js", "export {};");
  writeFileSync("dist/generated/runic-clients.d.ts", "export * from './about.contract';");
}
if (existsSync("build-reports")) { console.log("src/App.ts(3,7): error TS2322: Type 'string' is not assignable to type 'number'."); process.exit(2); }
if (existsSync("build-warns")) console.log("src/App.ts(1,1): warning TS6133: 'unused' is declared but never read.");
if (existsSync("svelte-check-human")) {
  // svelte-check's default output, colored as on Windows or in CI.
  const file = process.cwd() + sep + "\u001b[32msrc/settings.ts\u001b[39m";
  console.log("Loading svelte-check in workspace: " + process.cwd());
  console.log(file + ":5:7");
  console.log("\u001b[31mError\u001b[39m: Type 'string' is not assignable to type 'number'. (ts)");
  console.log("\u001b[36mconst count: number = '1';\u001b[39m");
  console.log(process.cwd() + sep + "src/App.svelte:2:1");
  console.log("\u001b[33mWarn\u001b[39m: A11y: <img> element should have an alt attribute (svelte)");
  console.log("svelte-check found 1 error and 1 warning in 2 files");
  process.exit(1);
}
if (existsSync("svelte-check-machine")) {
  console.log("1791786106400 START " + JSON.stringify(process.cwd()));
  console.log("1791786106401 ERROR " + JSON.stringify("src/settings.ts") + " 5:7 " + JSON.stringify("Type 'string' is not assignable to type 'number'."));
  console.log("1791786106402 WARNING " + JSON.stringify("src/App.svelte") + " 2:1 " + JSON.stringify('A11y: "alt" missing'));
  console.log("1791786106403 COMPLETED 4 FILES 1 ERRORS 1 WARNINGS 2 FILES_WITH_PROBLEMS");
  process.exit(1);
}
if (existsSync("svelte-check-warns")) {
  console.log(process.cwd() + sep + "src/App.svelte:2:1");
  console.log("Warn: A11y: <img> element should have an alt attribute (svelte)");
}
`);
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <RunicApplicationFrontendGenerationEnabled>true</RunicApplicationFrontendGenerationEnabled>
    <RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/frontend</RunicApplicationFrontendDirectory>
    <RunicApplicationFrontendGeneratedDirectory>$(RunicApplicationFrontendDirectory)/generated</RunicApplicationFrontendGeneratedDirectory>
${defaultInstall ? "" : "    <RunicApplicationFrontendInstallCommand>node install.mjs</RunicApplicationFrontendInstallCommand>\n"}    <RunicApplicationFrontendBuildCommand>node build.mjs</RunicApplicationFrontendBuildCommand>
    <Configuration>Debug</Configuration>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
${projectItems}
  <Import Project="${targets}" />
</Project>
`);
  return { project, frontend };
}

function build({ project, frontend }, { targets = "RunicViewsCollectFrontendInputs", properties = [], env = {} } = {}) {
  rmSync(join(project, "commands.log"), { force: true });
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), `-t:${targets}`,
    ...properties.map(property => `-p:${property}`),
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"],
    { cwd: project, encoding: "utf8", env: { ...process.env, ...env } });
  let commands = [];
  try {
    commands = readFileSync(join(project, "commands.log"), "utf8").trim().split("\n").filter(Boolean);
  } catch {}
  return { status: result.status, output: `${result.stdout}${result.stderr}`, commands };
}

// At minimal verbosity MSBuild indents the command's own output lines, not the errors it logs.
const errors = output => [...new Set(output.split("\n").filter(line => /^\S.* error (?:[A-Z]+\d+)?\s*:/.test(line))
  .map(line => line.replace(/\s*\[[^\]]*\]\s*$/, "").trim()))];

const age = (file, seconds) => {
  const time = new Date(Date.now() - seconds * 1000);
  utimesSync(file, time, time);
};

test("installs once, again when package.json changes, and again when node_modules is gone", () => {
  const app = fixture("dependencies", { name: "app", private: true, devDependencies: { vite: "8.3.2" } });
  let result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["install", "build"]);

  result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, [], "An unchanged frontend neither installs nor builds again.");

  // A pull that changes the declared packages installs them, even though node_modules exists.
  writeFileSync(join(app.frontend, "package.json"), JSON.stringify({ name: "app", private: true, devDependencies: { vite: "8.3.3" } }));
  result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["install", "build"]);

  // A lock file counts as well.
  writeFileSync(join(app.frontend, "package-lock.json"), "{}");
  result = build(app);
  assert.deepEqual(result.commands, ["install", "build"]);

  rmSync(join(app.frontend, "node_modules"), { recursive: true });
  for (const file of ["package.json", "package-lock.json"]) age(join(app.frontend, file), 3600);
  result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["install"], "A deleted node_modules is installed again.");
});

test("a package.json without dependencies needs no node_modules", () => {
  const app = fixture("no-dependencies", { name: "app", private: true, scripts: { build: "node build.mjs" } });
  writeFileSync(join(app.frontend, "install-skips-modules"), "");
  let result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["install", "build"]);
  assert.deepEqual(errors(result.output), []);

  result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, [], "The missing node_modules does not repeat the install.");
});

test("an install that creates no node_modules for declared packages fails with RUNICBRIDGE016", () => {
  const app = fixture("missing-modules", { name: "app", private: true, dependencies: { react: "19.0.0" } });
  writeFileSync(join(app.frontend, "install-skips-modules"), "");
  const result = build(app);
  assert.notEqual(result.status, 0);
  const reported = errors(result.output);
  assert.equal(reported.length, 1, result.output);
  assert.match(reported[0], /error RUNICBRIDGE016: 'node install\.mjs' did not create .*node_modules/);
});

// Package manager stand-ins on PATH that log the install command they were given.
function packageManagers() {
  const bin = join(directory, "package-managers");
  mkdirSync(bin, { recursive: true });
  for (const name of ["npm", "pnpm", "bun"]) {
    writeFileSync(join(bin, `${name}.mjs`), `
import { spawnSync } from "node:child_process";
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
// The build asks the manager for its version (RUNICBRIDGE090, and RUNICBRIDGE033 for Bun).
if (process.argv[2] === "--version") {
  if (process.env.RUNIC_TEST_VERSION_LOG) appendFileSync(process.env.RUNIC_TEST_VERSION_LOG, "${name} --version\\n");
  console.log(process.env.RUNIC_TEST_VERSION ?? "1.4.4");
  process.exit(0);
}
// The build runs the workspace resolver with Bun in a Bun workspace.
if (process.argv[2]?.endsWith(".mjs")) process.exit(spawnSync(process.execPath, process.argv.slice(2), { stdio: "inherit" }).status ?? 1);
// RUNIC_TEST_COMMANDS logs to one file with the folder the command ran in, as the workspace install runs at the root.
const log = process.env.RUNIC_TEST_COMMANDS;
appendFileSync(log ?? "../commands.log", ["${name}", ...process.argv.slice(2)].join(" ") + (log ? " in " + process.cwd() : "") + "\\n");
// "run <script>" runs the package's script, as a package build does.
const run = process.argv.indexOf("run");
if (run >= 2) {
  const script = JSON.parse(readFileSync("package.json", "utf8")).scripts?.[process.argv.at(-1)];
  process.exit(script === undefined ? 1 : spawnSync(script, { shell: true, stdio: "inherit" }).status ?? 1);
}
if (process.env.RUNIC_TEST_INSTALL_DELAY) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, Number(process.env.RUNIC_TEST_INSTALL_DELAY));
mkdirSync("node_modules", { recursive: true });
// The record a complete install leaves: npm's hidden lock file, pnpm's .modules.yaml.
const record = { npm: ".package-lock.json", pnpm: ".modules.yaml" }["${name}"];
if (record) writeFileSync("node_modules/" + record, "{}");
`);
    writeFileSync(join(bin, name), `#!/bin/sh\nexec node "$(dirname "$0")/${name}.mjs" "$@"\n`, { mode: 0o755 });
    writeFileSync(join(bin, `${name}.cmd`), `@node "%~dp0${name}.mjs" %*\r\n`);
  }
  return { PATH: `${bin}${process.platform === "win32" ? ";" : ":"}${process.env.PATH}` };
}

test("the default install without a lock file fails with RUNICBRIDGE026 and runs nothing", () => {
  const env = packageManagers();
  for (const [name, packageManager, lockFile] of [
    ["unlocked-npm", undefined, "package-lock.json"],
    ["unlocked-pnpm", "pnpm@10.0.0", "pnpm-lock.yaml"],
    ["unlocked-bun", "bun@1.4.2", "bun.lock"],
  ]) {
    const app = fixture(name, { name: "app", private: true, packageManager, devDependencies: { vite: "8.3.2" } }, "", { defaultInstall: true });
    const result = build(app, { env });
    assert.notEqual(result.status, 0, result.output);
    assert.deepEqual(result.commands, [], `${name}: nothing was installed or built.`);
    const reported = errors(result.output);
    assert.equal(reported.length, 1, result.output);
    assert.match(reported[0], new RegExp(`error RUNICBRIDGE026: .*frontend declares dependencies but has no ${lockFile.replace(".", "\\.")}`));
  }
});

test("the default install is locked, and RunicApplicationFrontendAllowUnlockedInstall opts out", () => {
  const env = packageManagers();
  const cases = [
    ["npm-lock", undefined, "package-lock.json", "npm ci --ignore-scripts"],
    ["npm-shrinkwrap", undefined, "npm-shrinkwrap.json", "npm ci --ignore-scripts"],
    ["pnpm-lock", "pnpm@10.0.0", "pnpm-lock.yaml", "pnpm install --frozen-lockfile --ignore-scripts"],
    ["bun-lock", "bun@1.4.2", "bun.lockb", "bun install --frozen-lockfile --ignore-scripts"],
  ];
  for (const [name, packageManager, lockFile, command] of cases) {
    const app = fixture(name, { name: "app", private: true, packageManager, devDependencies: { vite: "8.3.2" } }, "", { defaultInstall: true });
    writeFileSync(join(app.frontend, lockFile), "{}");
    const result = build(app, { env });
    assert.equal(result.status, 0, result.output);
    assert.deepEqual(result.commands, [command, "build"], name);
  }
  for (const [name, packageManager, command] of [
    ["npm-unlocked-opt-in", undefined, "npm install --ignore-scripts"],
    ["pnpm-unlocked-opt-in", "pnpm@10.0.0", "pnpm install --ignore-scripts"],
    ["bun-unlocked-opt-in", "bun@1.4.2", "bun install --ignore-scripts"],
  ]) {
    const app = fixture(name, { name: "app", private: true, packageManager, devDependencies: { vite: "8.3.2" } }, "", { defaultInstall: true });
    const result = build(app, { env, properties: ["RunicApplicationFrontendAllowUnlockedInstall=true"] });
    assert.equal(result.status, 0, result.output);
    assert.deepEqual(result.commands, [command, "build"], name);
  }
  // A package.json without dependencies resolves nothing, and an explicit command is the project's choice.
  const empty = fixture("unlocked-no-dependencies", { name: "app", private: true }, "", { defaultInstall: true });
  let result = build(empty, { env });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["npm ci --ignore-scripts", "build"]);
  const explicit = fixture("unlocked-explicit-command", { name: "app", private: true, devDependencies: { vite: "8.3.2" } });
  result = build(explicit, { env });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["install", "build"]);
});

// devtools-library-dx M1: a member of a JavaScript workspace installs once at the workspace root, with the root's
// package manager and lock file. Each workspace has an application frontend (app/frontend) and a library package (lib).
function workspace(name, rootPackageJson, rootFiles = {}, { members = ["app/frontend", "lib"], appPackageJson } = {}) {
  const root = join(directory, name);
  mkdirSync(root, { recursive: true });
  writeFileSync(join(root, "package.json"), JSON.stringify({ name: "root", private: true, ...rootPackageJson }));
  for (const [file, text] of Object.entries(rootFiles)) writeFileSync(join(root, file), text);
  const app = fixture(`${name}/app`, appPackageJson ?? { name: "@acme/app", private: true, dependencies: { "@acme/lib": "workspace:*" } }, "", { defaultInstall: true });
  mkdirSync(join(root, "lib"), { recursive: true });
  writeFileSync(join(root, "lib/package.json"), JSON.stringify({ name: "@acme/lib", private: true, devDependencies: { typescript: "6.0.3" } }));
  return { root, app, members };
}

const realRoot = path => realpathSync(path);

test("a workspace member installs once at the workspace root with the root's manager and lock file", () => {
  for (const [name, rootPackageJson, rootFiles, command] of [
    ["ws-bun", { workspaces: ["app/frontend", "lib"], packageManager: "bun@1.4.4" }, { "bun.lock": "{}" }, "bun install --frozen-lockfile --ignore-scripts"],
    // npm hoists every member's packages into the root's node_modules and creates none in the member (F8).
    ["ws-npm", { workspaces: ["app/*", "lib"] }, { "package-lock.json": "{}" }, "npm ci --ignore-scripts"],
    ["ws-pnpm", {}, { "pnpm-workspace.yaml": "packages:\n  - 'app/**'\n  - lib\n", "pnpm-lock.yaml": "lockfileVersion: '9.0'\n" }, "pnpm install --frozen-lockfile --ignore-scripts"],
  ]) {
    const { root, app } = workspace(name, rootPackageJson, rootFiles);
    const env = { ...packageManagers(), RUNIC_TEST_COMMANDS: join(app.project, "commands.log") };
    let result = build(app, { env });
    assert.equal(result.status, 0, `${name}: ${result.output}`);
    assert.deepEqual(result.commands, [`${command} in ${realRoot(root)}`, "build"], name);
    assert.ok(existsSync(join(root, "node_modules/.runic-install")), `${name}: the root's install records its hash.`);
    assert.ok(!existsSync(join(app.frontend, "node_modules")), `${name}: nothing is installed in the member.`);
    const graph = JSON.parse(readFileSync(join(app.project, "obj/runic/frontend-graph.json"), "utf8"));
    assert.equal(graph.schema, "rax.frontend-graph/1");
    assert.equal(graph.member, true);
    assert.deepEqual(graph.members.map(member => member.package).sort(), ["@acme/app", "@acme/lib"], name);

    result = build(app, { env });
    assert.equal(result.status, 0, result.output);
    assert.deepEqual(result.commands, [], `${name}: an unchanged workspace runs no JavaScript command.`);

    // A member's package.json is an input of the install.
    writeFileSync(join(root, "lib/package.json"), JSON.stringify({ name: "@acme/lib", private: true, devDependencies: { typescript: "6.0.4" } }));
    result = build(app, { env });
    assert.deepEqual(result.commands, [`${command} in ${realRoot(root)}`, "build"], `${name}: a changed member installs again, and the page builds with it.`);
    result = build(app, { env });
    assert.deepEqual(result.commands, [], `${name}: and then neither again.`);
    rmSync(join(root, "node_modules"), { recursive: true });
    result = build(app, { env });
    assert.deepEqual(result.commands, [`${command} in ${realRoot(root)}`], `${name}: a deleted node_modules installs again; the same packages rebuild nothing.`);
    // A touched lock file with the same content is current; a changed one installs.
    const lock = Object.keys(rootFiles).find(file => file.includes("lock"));
    age(join(root, lock), -60);
    assert.deepEqual(build(app, { env }).commands, [], `${name}: a touched lock file is current.`);
    writeFileSync(join(root, lock), `${readFileSync(join(root, lock), "utf8")}\n`);
    assert.deepEqual(build(app, { env }).commands, [`${command} in ${realRoot(root)}`, "build"], `${name}: a changed lock file installs.`);
    if (name !== "ws-bun") {
      // node_modules without the manager's own record is a half-written install.
      rmSync(join(root, "node_modules", name === "ws-npm" ? ".package-lock.json" : ".modules.yaml"));
      assert.deepEqual(build(app, { env }).commands, [`${command} in ${realRoot(root)}`], `${name}: an incomplete node_modules installs again.`);
    }
  }
});

test("RunicApplicationFrontendInstallMode: Always installs on every build, Never and InstallEnabled=false never", () => {
  const env = packageManagers();
  const app = fixture("install-mode", { name: "app", private: true, packageManager: "bun@1.4.4", devDependencies: { vite: "8.3.2" } }, "", { defaultInstall: true });
  writeFileSync(join(app.frontend, "bun.lock"), "{}");
  const install = "bun install --frozen-lockfile --ignore-scripts";
  assert.deepEqual(build(app, { env }).commands, [install, "build"]);
  assert.deepEqual(build(app, { env }).commands, [], "Auto: an unchanged frontend installs nothing.");
  for (const mode of ["Always", "always"]) {
    const result = build(app, { env, properties: [`RunicApplicationFrontendInstallMode=${mode}`] });
    assert.equal(result.status, 0, result.output);
    assert.deepEqual(result.commands, [install], `${mode}: the locked install runs again; the unchanged packages rebuild nothing.`);
  }
  rmSync(join(app.frontend, "node_modules"), { recursive: true });
  for (const properties of [["RunicApplicationFrontendInstallMode=Never"], ["RunicApplicationFrontendInstallEnabled=false"]]) {
    const result = build(app, { env, properties });
    assert.ok(!result.commands.some(command => command.startsWith("bun install")), `${properties}: ${result.commands}`);
  }
  // The mode wins over the older switch.
  assert.deepEqual(build(app, { env, properties: ["RunicApplicationFrontendInstallMode=Auto", "RunicApplicationFrontendInstallEnabled=false"] }).commands, [install]);
  // An unknown mode warns and installs as Auto.
  const result = build(app, { env, targets: "RunicViewsCheckFrontendPackageManager;RunicViewsCollectFrontendInputs", properties: ["RunicApplicationFrontendInstallMode=sometimes"] });
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /warning RUNICBRIDGE091: RunicApplicationFrontendInstallMode is 'sometimes', which is not Auto, Always or Never, so the build uses Auto/);
  assert.deepEqual(result.commands, []);
});

test("an upgraded package manager installs again; a no-change build runs no package manager", () => {
  const env = packageManagers();
  const app = fixture("manager-identity", { name: "app", private: true, packageManager: "pnpm@10.0.0", devDependencies: { vite: "8.3.2" } }, "", { defaultInstall: true });
  writeFileSync(join(app.frontend, "pnpm-lock.yaml"), "{}");
  const log = join(app.project, "versions.log");
  // The stand-in records every run, --version too.
  const runs = { ...env, RUNIC_TEST_VERSION_LOG: log };
  assert.deepEqual(build(app, { env: runs }).commands, ["pnpm install --frozen-lockfile --ignore-scripts", "build"]);
  rmSync(log, { force: true });
  assert.deepEqual(build(app, { env: runs }).commands, []);
  assert.ok(!existsSync(log), "A no-change build does not even ask the manager for its version.");
  const bin = env.PATH.split(process.platform === "win32" ? ";" : ":")[0];
  age(join(bin, process.platform === "win32" ? "pnpm.cmd" : "pnpm"), -120);
  assert.deepEqual(build(app, { env: runs }).commands, ["pnpm install --frozen-lockfile --ignore-scripts", "build"],
    "Another manager may install other packages, so the page builds again.");
});

test("RUNICBRIDGE026 names the workspace root whose lock file is missing", () => {
  const { app } = workspace("ws-unlocked", { workspaces: ["app/frontend", "lib"], packageManager: "bun@1.4.4" });
  const env = { ...packageManagers(), RUNIC_TEST_COMMANDS: join(app.project, "commands.log") };
  let result = build(app, { env });
  assert.notEqual(result.status, 0, result.output);
  assert.deepEqual(result.commands, []);
  const reported = errors(result.output);
  assert.equal(reported.length, 1, result.output);
  assert.match(reported[0], /error RUNICBRIDGE026: The frontend .*ws-unlocked\/app\/frontend is a member of the bun workspace at .*ws-unlocked, which has no bun\.lock, so the workspace install would not be locked\. Run 'bun install' in .*ws-unlocked and commit bun\.lock/);
  result = build(app, { env, properties: ["RunicApplicationFrontendAllowUnlockedInstall=true"] });
  assert.equal(result.status, 0, result.output);
  assert.match(result.commands[0], /^bun install --ignore-scripts in /);
});

test("RUNICBRIDGE087: a frontend below a workspace that does not list it; RunicApplicationFrontendWorkspaceRoot=none installs it alone", () => {
  const { app } = workspace("ws-not-member", { workspaces: ["lib"], packageManager: "bun@1.4.4" }, { "bun.lock": "{}" });
  const env = { ...packageManagers(), RUNIC_TEST_COMMANDS: join(app.project, "commands.log") };
  let result = build(app, { env });
  assert.notEqual(result.status, 0, result.output);
  assert.deepEqual(result.commands, []);
  const reported = errors(result.output);
  assert.equal(reported.length, 1, result.output);
  assert.match(reported[0], /error RUNICBRIDGE087: The frontend .*ws-not-member\/app\/frontend is below the bun workspace at .*ws-not-member, but the workspaces of .*package\.json do not include it.*RunicApplicationFrontendWorkspaceRoot=none/);
  // On its own, the frontend needs its own lock file (and, without a declaration, npm).
  result = build(app, { env, properties: ["RunicApplicationFrontendWorkspaceRoot=none"] });
  assert.match(errors(result.output).join("\n"), /error RUNICBRIDGE026: .*app\/frontend declares dependencies but has no package-lock\.json/);
  writeFileSync(join(app.frontend, "package-lock.json"), "{}");
  result = build(app, { env });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, [`npm ci --ignore-scripts in ${realRoot(app.frontend)}`, "build"], "A frontend with its own lock file is its own root.");
});

test("RUNICBRIDGE089: a member's own packageManager loses to its workspace's", () => {
  const { root, app } = workspace("ws-disagree", { workspaces: ["app/frontend", "lib"] }, { "bun.lock": "{}" },
    { appPackageJson: { name: "@acme/app", private: true, packageManager: "npm@11.0.0", dependencies: { "@acme/lib": "workspace:*" } } });
  const env = { ...packageManagers(), RUNIC_TEST_COMMANDS: join(app.project, "commands.log") };
  const result = build(app, { env });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, [`bun install --frozen-lockfile --ignore-scripts in ${realRoot(root)}`, "build"]);
  const warnings = [...new Set(result.output.split("\n").filter(line => / warning RUNICBRIDGE089:/.test(line)).map(line => line.trim()))];
  assert.equal(warnings.length, 1, result.output);
  assert.match(warnings[0], /declares npm \(packageManager\), but its workspace .*ws-disagree uses bun \(.*bun\.lock\), so the build installs and builds it with bun/);
  // The workspace detection is the build's only when it installs: with the install off, the member's declaration wins.
  const evaluated = spawnSync("dotnet", ["msbuild", join(app.project, "App.proj"), "-nologo", "-noAutoResponse", "-nodeReuse:false",
    "-p:RunicApplicationFrontendInstallEnabled=false", "-getProperty:RunicApplicationFrontendPackageManager"], { cwd: app.project, encoding: "utf8" });
  assert.equal(evaluated.stdout.trim(), "npm", evaluated.stdout + evaluated.stderr);
});

test("RUNICBRIDGE090: a package manager or runtime that does not run is named before the install and build", () => {
  // Only node and the shell are on PATH.
  const bin = join(directory, "only-node");
  mkdirSync(bin, { recursive: true });
  if (!existsSync(join(bin, "node"))) symlinkSync(process.execPath, join(bin, "node"));
  if (!existsSync(join(bin, "sh"))) symlinkSync(spawnSync("sh", ["-c", "command -v sh"], { encoding: "utf8" }).stdout.trim(), join(bin, "sh"));
  const dotnet = spawnSync("sh", ["-c", "command -v dotnet"], { encoding: "utf8" }).stdout.trim();
  const run = (project, properties = []) => {
    const result = spawnSync(dotnet, ["msbuild", join(project, "App.proj"), "-t:RunicViewsCollectFrontendInputs", ...properties.map(property => `-p:${property}`),
      "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"],
      { cwd: project, encoding: "utf8", env: { ...process.env, PATH: bin } });
    return errors(`${result.stdout}${result.stderr}`);
  };
  // A Bun workspace: Bun resolves the workspace.
  const bunWorkspace = workspace("ws-no-bun", { workspaces: ["app/frontend", "lib"], packageManager: "bun@1.4.4" }, { "bun.lock": "{}" });
  let reported = run(bunWorkspace.app.project);
  assert.equal(reported.length, 1, reported.join("\n"));
  assert.match(reported[0], /error RUNICBRIDGE090: The frontend workspace .*ws-no-bun uses bun \(packageManager in .*ws-no-bun\/package\.json\), and the build runs 'bun' to resolve it, but 'bun' did not run: it is not on PATH\. Install Bun 1\.4\.4 or later/);
  // An npm frontend: Node.js runs, npm does not.
  const npm = fixture("no-npm", { name: "app", private: true, devDependencies: { vite: "8.3.2" } }, "", { defaultInstall: true });
  writeFileSync(join(npm.frontend, "package-lock.json"), "{}");
  reported = run(npm.project);
  assert.equal(reported.length, 1, reported.join("\n"));
  assert.match(reported[0], /error RUNICBRIDGE090: The frontend .*no-npm\/frontend uses npm \(the default, as nothing declares a package manager\), but 'npm --version' failed with exit code 127: npm is not on PATH or does not run\. Install Node\.js, which includes npm/);
  // The frontend build of a Bun frontend that some other tool installed.
  const bun = fixture("no-bun-build", { name: "app", private: true, packageManager: "bun@1.4.4" });
  const project = readFileSync(join(bun.project, "App.proj"), "utf8").replace(/\s*<RunicApplicationFrontendBuildCommand>.*<\/RunicApplicationFrontendBuildCommand>/, "");
  writeFileSync(join(bun.project, "App.proj"), project);
  reported = run(bun.project, ["RunicApplicationFrontendInstallEnabled=false"]);
  assert.equal(reported.length, 1, reported.join("\n"));
  assert.match(reported[0], /error RUNICBRIDGE090: The frontend .*no-bun-build\/frontend uses bun \(packageManager in .*package\.json\), but 'bun --version' failed: bun is not on PATH or does not run, so the frontend build 'bun run --bun build' could not run/);
});

test("an application and a library of one workspace built at once install the workspace once", async () => {
  const { root, app } = workspace("ws-parallel", { workspaces: ["app/frontend", "lib/frontend"], packageManager: "bun@1.4.4" }, { "bun.lock": "{}" });
  const library = fixture("ws-parallel/lib", { name: "@acme/lib", private: true, devDependencies: { typescript: "6.0.3" } }, "", { defaultInstall: true });
  const log = join(root, "commands.log");
  const env = { ...process.env, ...packageManagers(), RUNIC_TEST_COMMANDS: log, RUNIC_TEST_INSTALL_DELAY: "1500" };
  const runs = [app, library].map(({ project }) => new Promise(resolvePromise => {
    const child = spawn("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsCollectFrontendInputs", "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m"],
      { cwd: project, env });
    let output = "";
    child.stdout.on("data", chunk => { output += chunk; });
    child.stderr.on("data", chunk => { output += chunk; });
    child.on("close", status => resolvePromise({ status, output }));
  }));
  for (const result of await Promise.all(runs)) assert.equal(result.status, 0, result.output);
  const installs = readFileSync(log, "utf8").split("\n").filter(line => line.startsWith("bun install"));
  assert.deepEqual(installs, [`bun install --frozen-lockfile --ignore-scripts in ${realRoot(root)}`]);
});

test("RunicApplicationFrontendNodeModulesDirectory links prepared dependencies instead of installing", () => {
  const app = fixture("prepared-modules", { name: "app", private: true, devDependencies: { vite: "8.3.2" } });
  const prepared = join(app.project, "prepared", "node_modules");
  const other = join(app.project, "other", "node_modules");
  mkdirSync(prepared, { recursive: true });
  mkdirSync(other, { recursive: true });
  const modules = join(app.frontend, "node_modules");

  let result = build(app, { properties: [`RunicApplicationFrontendNodeModulesDirectory=${prepared}`] });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["build"], "The prepared dependencies are not installed again.");
  assert.ok(lstatSync(modules).isSymbolicLink());
  assert.equal(readlinkSync(modules), prepared);

  // Another directory replaces the link, not the directory it pointed to.
  result = build(app, { properties: [`RunicApplicationFrontendNodeModulesDirectory=${other}`] });
  assert.equal(result.status, 0, result.output);
  assert.equal(readlinkSync(modules), other);
  assert.ok(existsSync(prepared));

  result = build(app, { properties: [`RunicApplicationFrontendNodeModulesDirectory=${join(app.project, "missing")}`] });
  assert.notEqual(result.status, 0);
  assert.match(errors(result.output).join("\n"), /error RUNICBRIDGE021: RunicApplicationFrontendNodeModulesDirectory is .*missing, which is not a directory/);

  // An installed node_modules is never replaced.
  rmSync(modules);
  mkdirSync(join(modules, "vite"), { recursive: true });
  result = build(app, { properties: [`RunicApplicationFrontendNodeModulesDirectory=${prepared}`] });
  assert.notEqual(result.status, 0);
  assert.match(errors(result.output).join("\n"), /error RUNICBRIDGE021: .*node_modules exists and is not a symbolic link/);
  assert.ok(existsSync(join(modules, "vite")));
  assert.deepEqual(result.commands, []);
});

test("failed frontend commands report one coded error instead of MSB3073", () => {
  const app = fixture("failures", { name: "app", private: true, devDependencies: { vite: "8.3.2" } });
  writeFileSync(join(app.frontend, "install-fails"), "");
  let result = build(app);
  assert.notEqual(result.status, 0);
  assert.doesNotMatch(result.output, /MSB3073/);
  let reported = errors(result.output);
  assert.equal(reported.length, 1, result.output);
  assert.match(reported[0], /error RUNICBRIDGE016: The frontend install 'node install\.mjs' in .* exited with code 1\./);
  assert.match(result.output, /npm error code E404/, "The command's own output stays visible.");
  assert.deepEqual(result.commands, ["install"]);

  rmSync(join(app.frontend, "install-fails"));
  writeFileSync(join(app.frontend, "build-fails"), "");
  result = build(app);
  assert.notEqual(result.status, 0);
  assert.doesNotMatch(result.output, /MSB3073/);
  reported = errors(result.output);
  assert.equal(reported.length, 1, result.output);
  assert.match(reported[0], /error RUNICBRIDGE016: The frontend build 'node build\.mjs' in .* exited with code 1\./);
  assert.match(result.output, /Rollup failed to resolve import/);

  result = build(app);
  assert.deepEqual(result.commands, ["build"], "A failed build is not recorded as up to date.");

  rmSync(join(app.frontend, "build-fails"));
  writeFileSync(join(app.frontend, "build-reports"), "");
  result = build(app);
  assert.notEqual(result.status, 0);
  assert.doesNotMatch(result.output, /MSB3073|RUNICBRIDGE016/);
  reported = errors(result.output);
  assert.equal(reported.length, 1, result.output);
  assert.match(reported[0], /frontend[\\/]src[\\/]App\.ts\(3,7\): error TS2322: Type 'string' is not assignable/,
    "The error keeps its code and location, resolved against the frontend directory.");

  rmSync(join(app.frontend, "build-reports"));
  writeFileSync(join(app.frontend, "build-warns"), "");
  result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /^\S.*App\.ts\(1,1\): warning TS6133: 'unused' is declared but never read\./m);
});

test("svelte-check errors are logged at their file, line and column", () => {
  const app = fixture("svelte-check", { name: "app", private: true, devDependencies: { vite: "8.3.2" } });
  for (const format of ["human", "machine"]) {
    writeFileSync(join(app.frontend, `svelte-check-${format}`), "");
    const result = build(app);
    rmSync(join(app.frontend, `svelte-check-${format}`));
    assert.notEqual(result.status, 0, result.output);
    assert.doesNotMatch(result.output, /MSB3073|RUNICBRIDGE016/, format);
    const reported = errors(result.output);
    assert.equal(reported.length, 1, result.output);
    assert.match(reported[0], /frontend[\\/]src[\\/]settings\.ts\(5,7\): error : Type 'string' is not assignable to type 'number'\./,
      `The ${format} svelte-check error is logged at its location in the frontend directory.`);
    assert.doesNotMatch(reported[0], /\u001b/);
    assert.match(result.output, /^\S.*frontend[\\/]src[\\/]App\.svelte\(2,1\): warning : A11y: /m, result.output);
    if (format === "machine") assert.match(result.output, /warning : A11y: "alt" missing/);
  }

  // A check that only warns builds, and its warnings stay in the command output.
  writeFileSync(join(app.frontend, "svelte-check-warns"), "");
  const result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.doesNotMatch(result.output, /App\.svelte\(2,1\): warning/);
});

// W319-003 D15, D17: the old names no longer apply; each fails the build once with RUNICBRIDGE017.
test("an old property name no longer applies and fails with RUNICBRIDGE017", () => {
  const project = join(directory, "renamed");
  mkdirSync(project, { recursive: true });
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <RunicBridgeFrontendDir>$(MSBuildProjectDirectory)/client</RunicBridgeFrontendDir>
    <RunicBridgeBuildFrontend>false</RunicBridgeBuildFrontend>
    <RunicApplicationId>org.example.app</RunicApplicationId>
    <RunicApplicationName>App</RunicApplicationName>
    <ApplicationTitle>App</ApplicationTitle>
    <RunicApplicationFrontendReactiveUiFlavor>reactive</RunicApplicationFrontendReactiveUiFlavor>
    <Configuration>Debug</Configuration>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <Import Project="${targets}" />
</Project>
`);
  const evaluate = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-nologo", "-noAutoResponse", "-nodeReuse:false",
    "-getProperty:RunicApplicationFrontendDirectory", "-getProperty:RunicApplicationFrontendBuildEnabled"], { cwd: project, encoding: "utf8" });
  assert.equal(evaluate.status, 0, evaluate.stdout + evaluate.stderr);
  const properties = JSON.parse(evaluate.stdout).Properties;
  assert.equal(properties.RunicApplicationFrontendDirectory, `${project}/Frontend`, "The old frontend directory name is ignored.");
  assert.equal(properties.RunicApplicationFrontendBuildEnabled, "", "The old build switch is ignored.");
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsReportRenamedProperties",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  const output = `${result.stdout}${result.stderr}`;
  assert.notEqual(result.status, 0, output);
  const reported = errors(output).filter(line => / error RUNICBRIDGE017:/.test(line));
  assert.equal(reported.length, 4, output);
  assert.ok(reported.some(line => /RunicBridgeFrontendDir is renamed to RunicApplicationFrontendDirectory\. The old name no longer applies/.test(line)), output);
  assert.ok(reported.some(line => /RunicBridgeBuildFrontend is renamed to RunicApplicationFrontendBuildEnabled/.test(line)), output);
  assert.ok(reported.some(line => /property RunicApplicationId is renamed to ApplicationId/.test(line)), output);
  // I-004: the ReactiveUI flavor property is removed; the build infers the flavor from the referenced adapter.
  assert.ok(reported.some(line => /property RunicApplicationFrontendReactiveUiFlavor is removed\. The ReactiveUI flavor is now inferred from the referenced Runic ReactiveUI adapter; remove the property/.test(line)), output);
  // RunicApplicationName is left alone next to ApplicationTitle, as a project may keep its own name.
  assert.ok(!reported.some(line => /RunicApplicationName/.test(line)), output);
});

// SDK-1 (D070): Visual Studio loads Runic DevKit for a solution whose projects declare RunicApplication.
test("the targets declare the RunicApplication project capability, for applications and libraries", () => {
  for (const outputType of ["Exe", "Library"]) {
    const project = join(directory, `capability-${outputType}`);
    mkdirSync(project, { recursive: true });
    writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <OutputType>${outputType}</OutputType>
    <Configuration>Debug</Configuration>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <Import Project="${targets}" />
</Project>
`);
    const evaluate = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-nologo", "-noAutoResponse", "-nodeReuse:false",
      "-getItem:ProjectCapability"], { cwd: project, encoding: "utf8" });
    assert.equal(evaluate.status, 0, evaluate.stdout + evaluate.stderr);
    const capabilities = JSON.parse(evaluate.stdout).Items.ProjectCapability.map(item => item.Identity);
    assert.deepEqual(capabilities.filter(name => name === "RunicApplication"), ["RunicApplication"], `${outputType}: ${capabilities}`);
  }
});

// Q7 (D031.2): RUNICBRIDGE023 checks the resolved references, so a mix of ReactiveUI flavors fails however it
// arrives (directly, transitively, or as a navigation adapter of the other flavor), and one flavor builds.
test("RUNICBRIDGE023 rejects Runic ReactiveUI adapters of both flavors in the resolved references", () => {
  const cases = [
    { name: "views-mix", references: ["Runic.Application.Views.ReactiveUI", "Runic.Application.Views.ReactiveUI.Reactive"], message: /references both Runic\.Application\.Views\.ReactiveUI\.Reactive, which uses ReactiveUI's System\.Reactive flavor, and Runic\.Application\.Views\.ReactiveUI/ },
    { name: "reactive-views-primitives-navigation", references: ["Runic.Application.Views.ReactiveUI.Reactive", "Runic.Navigation.ReactiveUI"], message: /Reference Runic\.Navigation\.ReactiveUI\.Reactive instead/ },
    { name: "primitives-views-reactive-navigation", references: ["Runic.Application.Views.ReactiveUI", "Runic.Navigation.ReactiveUI.Reactive"], message: /Reference Runic\.Navigation\.ReactiveUI instead/ },
    { name: "primitives", references: ["Runic.Application.Views.ReactiveUI", "Runic.Navigation.ReactiveUI"] },
    { name: "reactive", references: ["Runic.Application.Views.ReactiveUI.Reactive", "Runic.Navigation.ReactiveUI.Reactive"] },
  ];
  for (const { name, references, message } of cases) {
    const project = join(directory, `flavors-${name}`);
    mkdirSync(project, { recursive: true });
    // ReferencePath is what ResolveAssemblyReferences produces from direct, package and transitive references.
    writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup><Configuration>Debug</Configuration><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
${references.map(reference => `    <ReferencePath Include="$(MSBuildProjectDirectory)/refs/${reference}.dll" />`).join("\n")}
    <ReferencePath Include="$(MSBuildProjectDirectory)/refs/ReactiveUI.dll" />
  </ItemGroup>
  <Import Project="${targets}" />
</Project>
`);
    const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsRejectMixedReactiveUiFlavors",
      "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
    const output = `${result.stdout}${result.stderr}`;
    const reported = errors(output).filter(line => / error RUNICBRIDGE023:/.test(line));
    if (message) {
      assert.notEqual(result.status, 0, `${name}: ${output}`);
      assert.equal(reported.length, 1, `${name}: ${output}`);
      assert.match(reported[0], message, name);
    } else {
      assert.equal(result.status, 0, `${name}: ${output}`);
      assert.equal(reported.length, 0, `${name}: ${output}`);
    }
  }
});

// W319-003 D16: build and publish copy the frontend's output directory to the web root that dev serves.
test("build copies RunicApplicationFrontendOutputDirectory to RunicApplicationFrontendWebRoot", () => {
  const app = fixture("output-directory", { name: "app", private: true });
  writeFileSync(join(app.frontend, "emits-build"), "");
  const output = join(app.project, "out") + "/";
  const properties = ["RunicApplicationFrontendOutputDirectory=build", "RunicApplicationFrontendWebRoot=wwwroot/", `TargetDir=${output}`];
  const result = build(app, { targets: "RunicViewsCollectFrontendInputs;RunicViewsCopyFrontend", properties });
  assert.equal(result.status, 0, result.output);
  assert.equal(builds(result), 1);
  assert.equal(readFileSync(join(output, "wwwroot/index.html"), "utf8"), "<p>build</p>");
  assert.ok(!existsSync(join(output, "www")), "Nothing goes to www/.");
  // Deleting a file the build left in build/ builds again, as for dist/.
  rmSync(join(app.frontend, "build/index.html"));
  assert.equal(builds(build(app, { targets: "RunicViewsCollectFrontendInputs", properties })), 1);

  const evaluate = spawnSync("dotnet", ["msbuild", join(app.project, "App.proj"), "-nologo", "-noAutoResponse", "-nodeReuse:false",
    ...properties.map(property => `-p:${property}`), "-getProperty:_RunicApplicationFrontendWebRoot", "-getProperty:DefaultItemExcludes",
    "-getItem:RuntimeHostConfigurationOption"], { cwd: app.project, encoding: "utf8" });
  assert.equal(evaluate.status, 0, evaluate.stdout + evaluate.stderr);
  const evaluated = JSON.parse(evaluate.stdout);
  assert.equal(evaluated.Properties._RunicApplicationFrontendWebRoot, "wwwroot");
  assert.ok(evaluated.Properties.DefaultItemExcludes.includes(`${app.frontend}/build/**`), evaluated.Properties.DefaultItemExcludes);
  // The hosts serve the web root that the runtime configuration names.
  assert.deepEqual(evaluated.Items.RuntimeHostConfigurationOption.map(option => [option.Identity, option.Value]),
    [["Runic.Application.FrontendWebRoot", "wwwroot"]]);
});

// dotnet runic dev's development server fallback (#1085, #1096): only a trimmed or NativeAOT publish turns its feature
// switch off, in PrepareForILLink (before ILLink and ILC; the fixture's target stands in for the SDK's). A build of a
// project that sets PublishAot or PublishTrimmed keeps the fallback, and its runtimeconfig.json has no switch.
test("only a trimmed or NativeAOT publish turns the development server fallback off", () => {
  const forwarding = (app, publish) => {
    const result = spawnSync("dotnet", ["msbuild", join(app.project, "App.proj"), "-nologo", "-noAutoResponse", "-nodeReuse:false",
      ...(publish ? ["-t:PrepareForILLink"] : []), "-getItem:RuntimeHostConfigurationOption"], { cwd: app.project, encoding: "utf8" });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    return (JSON.parse(result.stdout).Items.RuntimeHostConfigurationOption ?? [])
      .filter(option => option.Identity === "Runic.Application.DevelopmentServerForwarding")
      .map(option => ({ value: option.Value, trim: option.Trim }));
  };
  for (const [index, property] of ["", "<PublishAot>true</PublishAot>", "<PublishTrimmed>true</PublishTrimmed>"].entries()) {
    const app = fixture(`forwarding-switch-${index}`, { name: "app", private: true },
      `  <PropertyGroup>${property}</PropertyGroup>\n  <Target Name="PrepareForILLink" />`);
    assert.deepEqual(forwarding(app, false), [], `A build turned the fallback off (${property || "no property"}).`);
    assert.deepEqual(forwarding(app, true), [{ value: "false", trim: "true" }], property);
  }
});

// Development-only views and members (D072.12/D072.13): like the fallback above, only a trimmed or NativeAOT publish
// turns them off, and RunicDevelopmentOnlyViews=true keeps them in every publish.
test("only a trimmed or NativeAOT publish leaves development-only views out, unless RunicDevelopmentOnlyViews is true", () => {
  const developmentOnly = (app, publish) => {
    const result = spawnSync("dotnet", ["msbuild", join(app.project, "App.proj"), "-nologo", "-noAutoResponse", "-nodeReuse:false",
      ...(publish ? ["-t:PrepareForILLink"] : []), "-getItem:RuntimeHostConfigurationOption"], { cwd: app.project, encoding: "utf8" });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    return (JSON.parse(result.stdout).Items.RuntimeHostConfigurationOption ?? [])
      .filter(option => option.Identity === "Runic.Application.Views.DevelopmentOnlyViews")
      .map(option => ({ value: option.Value, trim: option.Trim }));
  };
  for (const [index, property] of ["", "<PublishAot>true</PublishAot>", "<PublishTrimmed>true</PublishTrimmed>"].entries()) {
    const app = fixture(`development-only-${index}`, { name: "app", private: true },
      `  <PropertyGroup>${property}</PropertyGroup>\n  <Target Name="PrepareForILLink" />`);
    assert.deepEqual(developmentOnly(app, false), [], `A build left development-only views out (${property || "no property"}).`);
    assert.deepEqual(developmentOnly(app, true), [{ value: "false", trim: "true" }], property);
  }
  const kept = fixture("development-only-kept", { name: "app", private: true },
    "  <PropertyGroup><PublishAot>true</PublishAot><RunicDevelopmentOnlyViews>true</RunicDevelopmentOnlyViews></PropertyGroup>\n  <Target Name=\"PrepareForILLink\" />");
  assert.deepEqual(developmentOnly(kept, true), [], "RunicDevelopmentOnlyViews=true did not keep them in a publish.");
});

// Bridge blobs and uploads (runic-sdk#1193): a trimmed or NativeAOT publish turns blobs/1 off unless the application or
// a referenced assembly carries the assembly metadata Runic.Application.Views.Blobs=true, which the generated codecs of a
// blob or upload member write, or names RunicBlob or RunicUpload in its metadata (Bridges of a generator before 0.23);
// Runic.Application.Views itself, which defines both, does not count. RunicBridgeBlobs=true keeps it. The scan reads the
// attribute's serialized bytes and the NUL-terminated names, so a file that holds them stands in for an assembly. A
// build never turns blobs/1 off.
test("a trimmed or NativeAOT publish turns blobs/1 off unless an assembly's Bridges use blobs", () => {
  const blobs = (app, publish) => {
    const result = spawnSync("dotnet", ["msbuild", join(app.project, "App.proj"), "-nologo", "-noAutoResponse", "-nodeReuse:false",
      ...(publish ? ["-t:PrepareForILLink"] : []), "-getItem:RuntimeHostConfigurationOption"], { cwd: app.project, encoding: "utf8" });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    return (JSON.parse(result.stdout).Items.RuntimeHostConfigurationOption ?? [])
      .filter(option => option.Identity === "Runic.Application.Views.Blobs")
      .map(option => ({ value: option.Value, trim: option.Trim }));
  };
  const marked = (app, name, used) => {
    const key = Buffer.from("Runic.Application.Views.Blobs"), value = Buffer.from(used ? "true" : "false");
    // MZ, some other metadata, then the blob: prolog 01 00, two length-prefixed strings, no named arguments.
    writeFileSync(join(app.project, name), Buffer.concat([Buffer.from("MZ\0\0other"), Buffer.from([1, 0, key.length]), key,
      Buffer.from([value.length]), value, Buffer.from([0, 0])]));
    return name;
  };
  const named = (app, name) => {
    // A string heap: other names, then a longer type name whose end is RunicUpload, as a compiler may share it.
    writeFileSync(join(app.project, name), Buffer.from("MZ\0\0\0Other\0LibraryRunicUpload\0Object\0"));
    return name;
  };
  const project = (name, property, reference, item = "ReferencePath") => fixture(name, { name: "app", private: true },
    `  <PropertyGroup><PublishAot>true</PublishAot>${property}</PropertyGroup>\n` +
    (reference ? `  <ItemGroup><${item} Include="${reference}" /></ItemGroup>\n` : "") + "  <Target Name=\"PrepareForILLink\" />");
  const unused = project("blobs-unused", "", "Unmarked.dll");
  marked(unused, "Unmarked.dll", false);
  assert.deepEqual(blobs(unused, false), [], "A build turned blobs/1 off.");
  assert.deepEqual(blobs(unused, true), [{ value: "false", trim: "true" }]);
  const used = project("blobs-used", "", "Marked.dll");
  marked(used, "Marked.dll", true);
  assert.deepEqual(blobs(used, true), [], "A referenced assembly that uses blobs did not keep blobs/1.");
  const older = project("blobs-named", "", "Library.dll", "ReferenceCopyLocalPaths");
  named(older, "Library.dll");
  assert.deepEqual(blobs(older, true), [], "An unmarked library that names RunicUpload did not keep blobs/1.");
  const runtime = project("blobs-runtime", "", "Runic.Application.Views.dll");
  named(runtime, "Runic.Application.Views.dll");
  assert.deepEqual(blobs(runtime, true), [{ value: "false", trim: "true" }], "Runic.Application.Views kept blobs/1 by defining the types.");
  const kept = project("blobs-kept", "<RunicBridgeBlobs>true</RunicBridgeBlobs>");
  assert.deepEqual(blobs(kept, true), [], "RunicBridgeBlobs=true did not keep blobs/1 in a publish.");
});

// API-30: RunicDevelopmentOnlyViews and RunicBridgeBlobs take auto (the default, also empty) or true, without case.
// Another value, such as false or a typo, is warning RUNICBRIDGE075 in a build, which goes on as auto.
test("RUNICBRIDGE075: RunicDevelopmentOnlyViews and RunicBridgeBlobs other than auto or true warn", () => {
  const app = fixture("auto-properties", { name: "app", private: true });
  const warnings = properties => {
    const result = build(app, { targets: "RunicViewsCheckAutoProperties", properties });
    assert.equal(result.status, 0, result.output);
    return [...new Set(result.output.split("\n").filter(line => / warning RUNICBRIDGE075:/.test(line))
      .map(line => line.match(/RUNICBRIDGE075: (\w+) is '([^']*)'/).slice(1, 3).join("=")))].sort();
  };
  for (const value of ["", "auto", "true", "AUTO", "True"])
    assert.deepEqual(warnings([`RunicDevelopmentOnlyViews=${value}`, `RunicBridgeBlobs=${value}`]), [], `'${value}' warned.`);
  assert.deepEqual(warnings(["RunicDevelopmentOnlyViews=false", "RunicBridgeBlobs=ture"]),
    ["RunicBridgeBlobs=ture", "RunicDevelopmentOnlyViews=false"]);
  assert.deepEqual(warnings(["RunicBridgeBlobs=false"]), ["RunicBridgeBlobs=false"]);
  // The bootstrap pass repeats the project's properties and does not warn again.
  assert.deepEqual(warnings(["RunicBridgeBlobs=false", "RunicApplicationFrontendBootstrap=true"]), []);
});

const builds = result => result.commands.filter(command => command === "build").length;
const lastHost = ({ project }) => readFileSync(join(project, "host.log"), "utf8").trim().split("\n").at(-1);
const files = root => existsSync(root)
  ? readdirSync(root, { recursive: true, withFileTypes: true }).filter(entry => entry.isFile())
    .map(entry => join(entry.parentPath, entry.name).slice(root.length + 1).replaceAll("\\", "/")).sort()
  : [];

// #236: the stamp alone did not notice a deleted dist/.
test("a deleted dist or a missing output file rebuilds the frontend", () => {
  const app = fixture("deleted-dist", { name: "app", private: true });
  writeFileSync(join(app.frontend, "emits-dist"), "");
  let result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.equal(builds(result), 1);
  assert.equal(builds(build(app)), 0, "An unchanged frontend does not build again.");

  rmSync(join(app.frontend, "dist"), { recursive: true });
  result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.equal(builds(result), 1, "A deleted dist/ is built again.");
  assert.ok(existsSync(join(app.frontend, "dist/index.html")));

  rmSync(join(app.frontend, "dist/assets/index-1.js"));
  assert.equal(builds(build(app)), 1, "A file missing from dist/ builds it again.");
  assert.equal(builds(build(app)), 0);

  // A stamp written before the output list existed (0.7.0-preview.8) rebuilds once.
  rmSync(join(app.project, "obj/Debug/net10.0/runic-bridge-frontend.outputs"));
  assert.equal(builds(build(app)), 1);
  assert.equal(builds(build(app)), 0);
});

// #236: RunicApplicationFrontendInput is the extension point for files outside the frontend directory.
test("a RunicApplicationFrontendInput outside the frontend directory rebuilds the frontend", () => {
  const app = fixture("shared-input", { name: "app", private: true },
    `  <ItemGroup><RunicApplicationFrontendInput Include="$(MSBuildProjectDirectory)/../shared-ui/**/*" /></ItemGroup>`);
  const shared = join(app.project, "../shared-ui/lib");
  mkdirSync(shared, { recursive: true });
  writeFileSync(join(shared, "format.ts"), "export const format = 1;");
  age(join(shared, "format.ts"), 3600);
  assert.equal(builds(build(app)), 1);
  assert.equal(builds(build(app)), 0);

  writeFileSync(join(shared, "format.ts"), "export const format = 2;");
  const result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.equal(builds(result), 1, "Editing a shared component outside the frontend directory builds it again.");
});

// WIN-01: Visual Studio's fast up-to-date check knows the frontend and dev's stop stamp, in sets of their own.
test("Visual Studio's up-to-date check gets the frontend inputs and dev's stop stamp, without node_modules", () => {
  const app = fixture("up-to-date", { name: "app", private: true },
    `  <ItemGroup>
    <RunicApplicationFrontendInput Include="$(MSBuildProjectDirectory)/shared/format.ts;$(MSBuildProjectDirectory)/shared/missing.ts" />
  </ItemGroup>`);
  const output = join(app.project, "bin") + "/";
  for (const file of ["src/App.tsx", "bun.lock", "index.html", "node_modules/react/index.js", "dist/index.html",
    "generated/client.ts", ".vite/deps/chunk.js"]) {
    mkdirSync(join(app.frontend, file, ".."), { recursive: true });
    writeFileSync(join(app.frontend, file), "");
  }
  mkdirSync(join(app.project, "shared"), { recursive: true });
  writeFileSync(join(app.project, "shared/format.ts"), "");
  mkdirSync(join(output, "www"), { recursive: true });
  writeFileSync(join(output, "www/index.html"), "");
  const evaluate = (...properties) => {
    const result = spawnSync("dotnet", ["msbuild", join(app.project, "App.proj"), "-nologo", "-noAutoResponse", "-nodeReuse:false",
      `-p:TargetDir=${output}`, ...properties.map(property => `-p:${property}`),
      "-getItem:UpToDateCheckInput", "-getItem:UpToDateCheckBuilt"], { cwd: app.project, encoding: "utf8" });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const items = JSON.parse(result.stdout).Items;
    const paths = name => (items[name] ?? []).map(item => [item.Set, item.FullPath.slice(app.project.length + 1).replaceAll("\\", "/")])
      .sort((a, b) => `${a}`.localeCompare(`${b}`));
    return { inputs: paths("UpToDateCheckInput"), built: paths("UpToDateCheckBuilt") };
  };

  // Outside Visual Studio nothing is evaluated.
  assert.deepEqual(evaluate(), { inputs: [], built: [] });

  let evaluated = evaluate("BuildingInsideVisualStudio=true");
  assert.deepEqual(evaluated.inputs, [
    ["RunicDevSession", "obj/runic/dev-stopped"],
    ["RunicFrontend", "frontend/build.mjs"],
    ["RunicFrontend", "frontend/bun.lock"],
    ["RunicFrontend", "frontend/index.html"],
    ["RunicFrontend", "frontend/install.mjs"],
    ["RunicFrontend", "frontend/package.json"],
    ["RunicFrontend", "frontend/src/App.tsx"],
    ["RunicFrontend", "shared/format.ts"],
  ]);
  assert.deepEqual(evaluated.built, [
    ["RunicDevSession", "obj/Debug/net10.0/runic-www.stamp"],
    ["RunicFrontend", "obj/Debug/net10.0/runic-frontend-current.stamp"],
    ["RunicWebRoot", "bin/www/index.html"],
  ]);

  // #1121: an evaluation while dev runs gives the same items. dev renews its marker every 5 seconds, so as an input it
  // would leave every F5 of the session out of date; Visual Studio does not evaluate again when dev starts or stops.
  mkdirSync(join(app.project, "obj/runic"), { recursive: true });
  writeFileSync(join(app.project, "obj/runic/dev-frontend"), "");
  evaluated = evaluate("BuildingInsideVisualStudio=true", "IntermediateOutputPath=obj/x64/");
  assert.ok(!evaluated.inputs.some(([, path]) => path === "obj/runic/dev-frontend"), `${evaluated.inputs}`);
  assert.ok(evaluated.built.some(([set, path]) => set === "RunicDevSession" && path === "obj/x64/runic-www.stamp"), `${evaluated.built}`);
  rmSync(join(app.project, "obj/runic/dev-frontend"));

  // A library has no www/ of its own; a frontend that another tool builds has no stamp.
  evaluated = evaluate("BuildingInsideVisualStudio=true", "OutputType=Library");
  assert.ok(evaluated.inputs.every(([set]) => set === "RunicFrontend") && evaluated.built.every(([set]) => set === "RunicFrontend"),
    `${evaluated.inputs} ${evaluated.built}`);
  assert.ok(!evaluated.built.some(([, path]) => path.endsWith("index.html")));
  evaluated = evaluate("BuildingInsideVisualStudio=true", "RunicApplicationFrontendBuildEnabled=false");
  assert.ok(evaluated.inputs.every(([set]) => set === "RunicDevSession"), `${evaluated.inputs}`);

  // The build writes the stamps that the sets compare: dev-stopped once, with an old time, and runic-www.stamp whenever
  // it copies the frontend, which a build that keeps a live session's pages does not.
  const stamp = join(app.project, "obj/Debug/net10.0/runic-www.stamp");
  const stopped = join(app.project, "obj/runic/dev-stopped");
  rmSync(join(app.project, "obj"), { recursive: true, force: true });
  let result = build(app, { targets: "RunicViewsWwwRestored", properties: ["RunicApplicationFrontendCopyEnabled=false"] });
  assert.equal(result.status, 0, result.output);
  assert.ok(!existsSync(stamp) && !existsSync(stopped));
  result = build(app, { targets: "RunicViewsWwwRestored" });
  assert.equal(result.status, 0, result.output);
  assert.ok(existsSync(stamp));
  assert.ok(statSync(stopped).mtimeMs < statSync(stamp).mtimeMs - 3600_000, "dev-stopped is newer than the build.");
  // dev's stop is newer than the stamp until the next build copies the frontend again.
  writeFileSync(stopped, "");
  utimesSync(stamp, new Date(Date.now() - 60_000), new Date(Date.now() - 60_000));
  const stoppedAt = statSync(stopped).mtimeMs;
  assert.ok(stoppedAt > statSync(stamp).mtimeMs);
  result = build(app, { targets: "RunicViewsWwwRestored" });
  assert.equal(result.status, 0, result.output);
  assert.ok(statSync(stamp).mtimeMs >= stoppedAt, "The build did not record that www/ is restored.");
  assert.equal(statSync(stopped).mtimeMs, stoppedAt, "The build changed dev's stop stamp.");

  // #1121: runic-frontend-current.stamp follows a build after which the frontend is current: one whose frontend stamp
  // exists, or one during a dev session, which skips the frontend build and never has that stamp.
  const current = join(app.project, "obj/Debug/net10.0/runic-frontend-current.stamp");
  const frontendStamp = join(app.project, "obj/Debug/net10.0/runic-bridge-frontend.stamp");
  rmSync(join(app.project, "obj"), { recursive: true, force: true });
  result = build(app, { targets: "RunicViewsFrontendCurrent" });
  assert.equal(result.status, 0, result.output);
  assert.ok(!existsSync(current), "A build without a current frontend wrote the stamp.");
  result = build(app, { targets: "RunicViewsFrontendCurrent", properties: ["_RunicDevSessionLive=true"] });
  assert.equal(result.status, 0, result.output);
  assert.ok(existsSync(current), "A build during a dev session did not write the stamp.");
  utimesSync(current, new Date(Date.now() - 60_000), new Date(Date.now() - 60_000));
  const sessionAt = statSync(current).mtimeMs;
  writeFileSync(frontendStamp, "");
  result = build(app, { targets: "RunicViewsFrontendCurrent" });
  assert.equal(result.status, 0, result.output);
  assert.ok(statSync(current).mtimeMs > sessionAt, "A build with a current frontend did not renew the stamp.");
});

// #236 and #237: the host and the build environment change the page without changing a file.
test("RunicApplicationHost and the build environment are part of the build state", () => {
  const app = fixture("build-state", { name: "app", private: true },
    `  <ItemGroup><RunicApplicationFrontendBuildEnvironment Include="APP_FLAVOR" /></ItemGroup>`);
  const env = { APP_FLAVOR: "", NODE_ENV: "" };
  let result = build(app, { properties: ["RunicApplicationHost=desktop"], env });
  assert.equal(result.status, 0, result.output);
  assert.equal(builds(result), 1);
  assert.equal(lastHost(app), "desktop", "The frontend build receives the host as RUNIC_APPLICATION_HOST.");
  assert.equal(builds(build(app, { properties: ["RunicApplicationHost=desktop"], env })), 0);

  result = build(app, { properties: ["RunicApplicationHost=cswebui"], env });
  assert.equal(builds(result), 1, "Switching the host builds the page for the new host.");
  assert.equal(lastHost(app), "cswebui");
  assert.equal(builds(build(app, { properties: ["RunicApplicationHost=cswebui"], env })), 0);

  const host = ["RunicApplicationHost=cswebui"];
  for (const [name, value] of [["NODE_ENV", "development"], ["APP_FLAVOR", "store"]]) {
    env[name] = value;
    assert.equal(builds(build(app, { properties: host, env })), 1, `A changed ${name} builds the frontend again.`);
    assert.equal(builds(build(app, { properties: host, env })), 0, `An unchanged ${name} does not.`);
  }
  assert.equal(builds(build(app, { properties: host, env: { ...env, UNRELATED_SETTING: "1" } })), 0,
    "Other environment variables are not part of the build state.");
  assert.match(readFileSync(join(app.project, "obj/runic-bridge-frontend.state"), "utf8"), /^-?\d+\s*$/,
    "Only a hash of the build state is written.");

  // dist/ serves every configuration: a configuration whose state differs from the one that built it rebuilds.
  const flavor = value => ({ ...env, APP_FLAVOR: value });
  assert.equal(builds(build(app, { properties: host, env: flavor("debug") })), 1);
  assert.equal(builds(build(app, { properties: [...host, "Configuration=Release"], env: flavor("release") })), 1);
  assert.equal(builds(build(app, { properties: host, env: flavor("debug") })), 1,
    "Debug builds again after Release replaced dist/ with another build state.");
  assert.equal(builds(build(app, { properties: host, env: flavor("debug") })), 0);
});

// #975: Bun's bundler resolves the development build of @runic-artifex/views unless NODE_ENV is production, so an
// optimized build (Release, dotnet publish) runs the frontend build with NODE_ENV=production; Debug leaves it unset, and
// a NODE_ENV the environment sets wins.
test("an optimized build runs the frontend build with NODE_ENV=production unless the environment sets NODE_ENV", () => {
  const app = fixture("node-env", { name: "app", private: true });
  const nodeEnv = () => readFileSync(join(app.project, "node-env.log"), "utf8").trim().split("\n").at(-1);
  const host = "RunicApplicationHost=cswebui";
  const release = [host, "Configuration=Release", "Optimize=true"];
  let result = build(app, { properties: [host], env: { NODE_ENV: "" } });
  assert.equal(result.status, 0, result.output);
  assert.equal(builds(result), 1);
  assert.equal(nodeEnv(), "unset", "Debug leaves NODE_ENV unset: the development build");
  assert.equal(lastHost(app), "cswebui");
  result = build(app, { properties: release, env: { NODE_ENV: "" } });
  assert.equal(builds(result), 1, "Release builds again: the frontend build receives another NODE_ENV");
  assert.equal(nodeEnv(), "production");
  assert.equal(lastHost(app), "cswebui", "The host is still passed");
  assert.equal(builds(build(app, { properties: release, env: { NODE_ENV: "" } })), 0);
  assert.equal(builds(build(app, { properties: [host], env: { NODE_ENV: "" } })), 1, "Debug builds again after Release");
  assert.equal(nodeEnv(), "unset");
  assert.equal(builds(build(app, { properties: release, env: { NODE_ENV: "development" } })), 1);
  assert.equal(nodeEnv(), "development", "A NODE_ENV the environment sets wins");
});

// #236: www/ kept the assets of earlier builds, so a page built for another host kept being served.
test("www mirrors dist and keeps the files that the frontend copy did not write", () => {
  const app = fixture("mirror", { name: "app", private: true });
  writeFileSync(join(app.frontend, "emits-dist"), "");
  const www = join(app.project, "bin/www");
  const copy = { targets: "RunicViewsCollectFrontendInputs;RunicViewsCopyFrontend;RunicViewsMirrorFrontend",
    properties: [`TargetDir=${join(app.project, "bin")}/`] };
  let result = build(app, copy);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(files(www), ["assets/index-1.js", "index.html"]);

  // A host client script, an application's own file and a file from before the first mirrored copy stay.
  mkdirSync(join(www, "data"), { recursive: true });
  for (const file of ["runic-views.js", "data/user.json", "assets/older.js"]) writeFileSync(join(www, file), "kept");
  writeFileSync(join(app.frontend, "asset-name"), "index-2");
  result = build(app, copy);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(files(www), ["assets/index-2.js", "assets/older.js", "data/user.json", "index.html", "runic-views.js"],
    "The asset of the earlier build is deleted; other files stay.");
  assert.match(readFileSync(join(www, "index.html"), "utf8"), /index-2\.js/);

  // A recorded file that someone wrote since the last copy stays.
  const rewritten = join(www, "assets/index-2.js");
  writeFileSync(rewritten, "an operator's file");
  utimesSync(rewritten, new Date(Date.now() + 5000), new Date(Date.now() + 5000));

  // dotnet runic dev turns the copy off and writes its own document: nothing is deleted then.
  writeFileSync(join(app.frontend, "asset-name"), "index-3");
  result = build(app, { ...copy, properties: [...copy.properties, "RunicApplicationFrontendCopyEnabled=false"] });
  assert.equal(result.status, 0, result.output);
  assert.ok(existsSync(join(www, "assets/index-2.js")));
  result = build(app, copy);
  assert.deepEqual(files(www), ["assets/index-2.js", "assets/index-3.js", "assets/older.js", "data/user.json", "index.html", "runic-views.js"],
    "The rewritten asset of the earlier build stays.");
  assert.equal(readFileSync(rewritten, "utf8"), "an operator's file");
});

// I-063: a RunicApplicationFrontendPackageManager other than npm, pnpm or bun selects the npm commands; say so.
function packageManagerWarnings(name, properties, { packageJson = true } = {}) {
  const project = join(directory, `package-manager-${name}`);
  mkdirSync(join(project, "frontend"), { recursive: true });
  if (packageJson) writeFileSync(join(project, "frontend/package.json"), JSON.stringify({ name: "app", private: true }));
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/frontend</RunicApplicationFrontendDirectory>
    <Configuration>Debug</Configuration>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <Import Project="${targets}" />
</Project>
`);
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsCheckFrontendPackageManager",
    ...properties.map(property => `-p:${property}`),
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  const output = `${result.stdout}${result.stderr}`;
  assert.equal(result.status, 0, output);
  return [...new Set(output.split("\n").filter(line => / warning RUNICBRIDGE029:/.test(line)).map(line => line.trim()))];
}

test("a RunicApplicationFrontendPackageManager other than npm, pnpm or bun warns with RUNICBRIDGE029", () => {
  for (const value of ["yarn", "Yarn", "npm@10"]) {
    const warnings = packageManagerWarnings(`invalid-${value.replace(/[^A-Za-z0-9]/g, "_")}`, [`RunicApplicationFrontendPackageManager=${value}`]);
    assert.equal(warnings.length, 1, `${value}: ${warnings.join("\n")}`);
    assert.ok(warnings[0].includes(`RunicApplicationFrontendPackageManager is '${value}', which is not npm, pnpm or bun, so the default frontend install and build commands use npm.`), warnings[0]);
  }
  // One default command is enough for the value to matter.
  assert.equal(packageManagerWarnings("invalid-install-default",
    ["RunicApplicationFrontendPackageManager=yarn", "RunicApplicationFrontendBuildCommand=yarn build"]).length, 1);
  assert.equal(packageManagerWarnings("invalid-build-default",
    ["RunicApplicationFrontendPackageManager=yarn", "RunicApplicationFrontendInstallCommand=yarn install"]).length, 1);
});

// D066.35: a RUNICBRIDGE warning of these targets or the generator fails a build that treats warnings as errors, unless
// WarningsNotAsErrors or NoWarn names it; WarningsAsErrors keeps working beside it.
test("TreatWarningsAsErrors turns the targets' RUNICBRIDGE warnings into errors, except those it may not", () => {
  const run = (name, properties) => {
    const project = join(directory, `twae-${name}`);
    mkdirSync(join(project, "frontend"), { recursive: true });
    writeFileSync(join(project, "frontend/package.json"), JSON.stringify({ name: "app", private: true }));
    writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/frontend</RunicApplicationFrontendDirectory>
    <Configuration>Debug</Configuration>
    <TargetFramework>net10.0</TargetFramework>
    <RunicApplicationFrontendPackageManager>yarn</RunicApplicationFrontendPackageManager>
    ${properties}
  </PropertyGroup>
  <Import Project="${targets}" />
</Project>
`);
    const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsCheckFrontendPackageManager",
      "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
    const output = `${result.stdout}${result.stderr}`;
    return { status: result.status, output, error: / error RUNICBRIDGE029:/.test(output), warning: / warning RUNICBRIDGE029:/.test(output) };
  };
  const failed = run("on", "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>");
  assert.ok(failed.status !== 0 && failed.error && !failed.warning, failed.output);
  const kept = run("not-as-errors", "<TreatWarningsAsErrors>true</TreatWarningsAsErrors><WarningsNotAsErrors>CS0618, RUNICBRIDGE029</WarningsNotAsErrors>");
  assert.ok(kept.status === 0 && kept.warning && !kept.error, kept.output);
  // The SDK's targets make a NoWarn code a message; this project without them only shows that it is no error.
  const hidden = run("no-warn", "<TreatWarningsAsErrors>true</TreatWarningsAsErrors><NoWarn>$(NoWarn);RUNICBRIDGE029</NoWarn>");
  assert.ok(hidden.status === 0 && !hidden.error, hidden.output);
  // WarningsAsErrors is kept beside TreatWarningsAsErrors, also for a code the Bridge list leaves out. (Alone, the SDK's
  // targets map it.)
  const named = run("warnings-as-errors", "<TreatWarningsAsErrors>true</TreatWarningsAsErrors><WarningsAsErrors>RUNICBRIDGE029</WarningsAsErrors><NoWarn>RUNICBRIDGE029</NoWarn>");
  assert.ok(named.status !== 0 && named.error, named.output);
  const off = run("off", "");
  assert.ok(off.status === 0 && off.warning && !off.error, off.output);
});

test("supported, differently cased or empty package managers, and unused values, do not warn", () => {
  for (const value of ["", "npm", "pnpm", "bun", "NPM", "Pnpm", "BUN"])
    assert.deepEqual(packageManagerWarnings(`valid-${value || "empty"}`, [`RunicApplicationFrontendPackageManager=${value}`]), [], value);
  const yarn = "RunicApplicationFrontendPackageManager=yarn";
  assert.deepEqual(packageManagerWarnings("own-commands",
    [yarn, "RunicApplicationFrontendInstallCommand=yarn install", "RunicApplicationFrontendBuildCommand=yarn build"]), [],
    "A project that sets both commands does not use the package manager.");
  assert.deepEqual(packageManagerWarnings("no-install",
    [yarn, "RunicApplicationFrontendInstallEnabled=false", "RunicApplicationFrontendBuildCommand=yarn build"]), []);
  assert.deepEqual(packageManagerWarnings("no-build", [yarn, "RunicApplicationFrontendBuildEnabled=false"]), []);
  assert.deepEqual(packageManagerWarnings("no-frontend", [yarn], { packageJson: false }), []);
});

// RUNICBRIDGE033: the .NET SDK and Bun below the versions that `dotnet runic doctor` requires warn, and newer ones,
// prereleases of a newer SDK included, do not.
function toolchainWarnings(output) {
  return [...new Set(output.split("\n").filter(line => / warning RUNICBRIDGE033:/.test(line)).map(line => line.trim()))];
}

test("a .NET SDK older than the floor warns with RUNICBRIDGE033", () => {
  const app = fixture("toolchain-sdk", { name: "app", private: true });
  for (const [version, warns] of [["10.0.401", true], ["10.0.100-rc.2.25502.107", true], ["10.0.403", true], ["10.0.404", false],
    ["10.0.500", false], ["11.0.100-preview.1.26101.1", false]]) {
    const result = build(app, { targets: "RunicViewsCheckDotNetSdkVersion", properties: [`NETCoreSdkVersion=${version}`] });
    assert.equal(result.status, 0, result.output);
    const warnings = toolchainWarnings(result.output);
    assert.equal(warnings.length, warns ? 1 : 0, `${version}: ${result.output}`);
    if (warns) assert.ok(warnings[0].includes(`.NET SDK ${version} is older than 10.0.404`), warnings[0]);
  }
});

test("a Bun older than the floor warns with RUNICBRIDGE033 when the build runs the frontend build", () => {
  const env = packageManagers();
  for (const [version, warns] of [["1.4.2", true], ["1.4.4", false], ["1.5.0", false]]) {
    const app = fixture(`toolchain-bun-${version}`, { name: "app", private: true, packageManager: "bun@1.4.4" });
    const result = build(app, { env: { ...env, RUNIC_TEST_VERSION: version } });
    assert.equal(result.status, 0, result.output);
    assert.deepEqual(result.commands, ["install", "build"], version);
    const warnings = toolchainWarnings(result.output);
    assert.equal(warnings.length, warns ? 1 : 0, `${version}: ${result.output}`);
    if (warns) assert.ok(warnings[0].includes(`Bun ${version} is older than 1.4.4`), warnings[0]);
  }
  // npm and pnpm frontends do not run Bun.
  const npm = fixture("toolchain-npm", { name: "app", private: true });
  const result = build(npm, { env: { ...env, RUNIC_TEST_VERSION: "1.0.0" } });
  assert.deepEqual(toolchainWarnings(result.output), []);
});

// #237: a page that loads runic-views.js runs on every host; the referenced host supplies the client.
const hostTargets = name => resolve(root, `packages/dotnet/Runic.Application.Views.${name}/build/Runic.Application.Views.${name}.targets`);
const hostClient = name => readFileSync(resolve(root,
  `packages/dotnet/Runic.Application.Views.${name}/www/${{ Desktop: "runic-desktop-views.js", CsWebUi: "runic-cswebui.js" }[name]}`), "utf8");

// An application with a built page and the given host packages. Its page loads runic-views.js unless told otherwise.
function hostProject(name, hosts, script = "runic-views.js") {
  const project = join(directory, name);
  mkdirSync(join(project, "frontend/dist"), { recursive: true });
  writeFileSync(join(project, "frontend/dist/index.html"), `<script src="webui.js"></script><script src="./${script}"></script>`);
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>App</AssemblyName>
    <TargetDir>$(MSBuildProjectDirectory)/bin/</TargetDir>
    <PublishDir>$(MSBuildProjectDirectory)/publish/</PublishDir>
    <RunicApplicationFrontendGenerationEnabled>true</RunicApplicationFrontendGenerationEnabled>
    <RunicApplicationFrontendBuildEnabled>false</RunicApplicationFrontendBuildEnabled>
    <RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/frontend</RunicApplicationFrontendDirectory>
    <Configuration>Debug</Configuration>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <Import Project="${targets}" />
${hosts.map(host => `  <Import Project="${hostTargets(host)}" />`).join("\n")}
</Project>
`);
  return project;
}

// Copies the page and the host clients as a build (or publish) does, without deleting earlier output.
function copyHost(project, host, { publish = false } = {}) {
  const targets = publish
    ? "RunicViewsResolveHost;RunicViewsCopyFrontendForPublish;RunicViewsMirrorFrontendForPublish"
    : "RunicViewsResolveHost;RunicViewsCopyFrontend;RunicViewsMirrorFrontend";
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), `-t:${targets}`,
    ...(host ? [`-p:RunicApplicationHost=${host}`] : []),
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  const output = `${result.stdout}${result.stderr}`;
  const views = join(project, publish ? "publish/www/runic-views.js" : "bin/www/runic-views.js");
  return { status: result.status, output, views: existsSync(views) ? readFileSync(views, "utf8") : undefined,
    warnings: [...new Set(output.split("\n").filter(line => / warning RUNICBRIDGE019:/.test(line)).map(line => line.trim()))] };
}

test("the referenced host copies its Views client as runic-views.js", () => {
  for (const host of ["Desktop", "CsWebUi"]) {
    const result = copyHost(hostProject(`only-${host}`, [host]));
    assert.equal(result.status, 0, result.output);
    assert.equal(result.views, hostClient(host), `The only host, ${host}, needs no RunicApplicationHost.`);
    assert.deepEqual(result.warnings, []);
  }

  const both = hostProject("both-hosts", ["Desktop", "CsWebUi"]);
  let result = copyHost(both);
  assert.equal(result.status, 0, result.output);
  assert.equal(result.views, undefined, "With two hosts, neither is chosen silently.");
  assert.equal(result.warnings.length, 1, result.output);
  assert.match(result.warnings[0], /references more than one Runic host \(desktop, cswebui\), and its page index\.html loads runic-views\.js/);

  for (const [host, name] of [["desktop", "Desktop"], ["cswebui", "CsWebUi"]]) {
    result = copyHost(both, host);
    assert.equal(result.status, 0, result.output);
    assert.equal(result.views, hostClient(name), `RunicApplicationHost=${host} copies that host's client.`);
    assert.deepEqual(result.warnings, []);
  }
});

// Review of #256: a runic-views.js of an earlier build must not outlive the host choice.
test("a runic-views.js that no host copies any more is deleted, on build and publish", () => {
  const both = hostProject("stale-views", ["Desktop", "CsWebUi"]);
  for (const publish of [false, true]) {
    let result = copyHost(both, "desktop", { publish });
    assert.equal(result.views, hostClient("Desktop"), result.output);
    result = copyHost(both, undefined, { publish });
    assert.equal(result.status, 0, result.output);
    assert.equal(result.views, undefined, `Without a host, the Desktop client is gone (publish: ${publish}).`);

    copyHost(both, "cswebui", { publish });
    result = copyHost(both, "wpf", { publish });
    assert.equal(result.views, undefined, `An unknown host leaves no client behind (publish: ${publish}).`);
    if (!publish) {
      assert.equal(result.warnings.length, 1, result.output);
      assert.match(result.warnings[0], /RunicApplicationHost is 'wpf', but App references no such Runic host/);
    }
  }
});

// D072.11: 0.23 copies no host-named client, so a page that still loads one gets no Bridge and RUNICBRIDGE019 names the
// fix, with one host or several; a frontend that supplies its own copy under that name is left alone.
test("RUNICBRIDGE019: a page that loads a removed host-named client", () => {
  for (const [script, hosts] of [["runic-cswebui.js", ["CsWebUi"]], ["runic-desktop-views.js", ["Desktop"]],
    ["runic-desktop-views.js", ["Desktop", "CsWebUi"]]]) {
    const project = hostProject(`host-named-${hosts.length}-${script}`, hosts, script);
    let result = copyHost(project);
    assert.equal(result.status, 0, result.output);
    assert.equal(existsSync(join(project, `bin/www/${script}`)), false, result.output);
    assert.equal(result.warnings.length, 1, result.output);
    assert.match(result.warnings[0], /The page index\.html of App loads runic-desktop-views\.js or runic-cswebui\.js, which Runic 0\.23 no longer copies to www\/, so the page gets no Bridge\. Load runic-views\.js instead/);

    writeFileSync(join(project, `frontend/dist/${script}`), "// the application's own copy\n");
    result = copyHost(project);
    assert.equal(result.status, 0, result.output);
    assert.deepEqual(result.warnings, [], result.output);
  }
});

// Review of #256: a library or test project does not warn about a repository-wide RunicApplicationHost.
test("an unknown RunicApplicationHost does not warn in a library", () => {
  const project = hostProject("library-host", ["Desktop"]);
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsResolveHost",
    "-p:RunicApplicationHost=cswebui", "-p:OutputType=Library",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout);
  assert.doesNotMatch(result.stdout, /RUNICBRIDGE019/);
});

// #278: dotnet publish --no-build with NativeAOT schedules CoreCompile, so Bridge generation runs
// with NoBuild=true. Building the generator or the bootstrap pass then failed with NETSDK1085.
// The build before it generated the code: reuse it, and say so when it is missing.
function noBuildProject(name, { generated = [], noViews = false } = {}) {
  const project = join(directory, name);
  const output = join(project, "obj/Debug/net10.0/runic-bridge");
  mkdirSync(output, { recursive: true });
  for (const file of generated) writeFileSync(join(output, file), "// generated\n");
  if (noViews) writeFileSync(join(output, "RunicBridge.NoViews.marker"), "");
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <AssemblyName>App</AssemblyName>
    <RunicApplicationFrontendGenerationEnabled>true</RunicApplicationFrontendGenerationEnabled>
    <RunicApplicationFrontendBuildEnabled>false</RunicApplicationFrontendBuildEnabled>
    <RunicApplicationFrontendCompositionType>App.RunicViews</RunicApplicationFrontendCompositionType>
    <Configuration>Debug</Configuration>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <!-- The Microsoft.NET.Sdk targets that RunicViewsGenerateBridge depends on. -->
  <Target Name="ResolveProjectReferences" />
  <Target Name="InitializeSourceRootMappedPaths" />
  <Import Project="${targets}" />
</Project>
`);
  return { project, output };
}

function generateWithoutBuild(project, extra = []) {
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsGenerateBridge", "-p:NoBuild=true",
    ...extra, "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  return { status: result.status, output: `${result.stdout}${result.stderr}` };
}

test("NoBuild reuses the generated Bridge code instead of building the generator", () => {
  const generated = ["App.CounterViewModel.Bridge.g.cs", "App.CounterWindow.View.g.cs", "RunicBridgeComposition.g.cs"];
  const { project, output } = noBuildProject("no-build", { generated });
  const result = generateWithoutBuild(project, ["-getItem:Compile"]);
  assert.equal(result.status, 0, result.output);
  assert.doesNotMatch(result.output, /NETSDK1085|error/, result.output);
  const compile = JSON.parse(result.output.slice(result.output.indexOf("{"))).Items.Compile
    .map(item => item.FullPath.slice(output.length + 1)).sort();
  assert.deepEqual(compile, [...generated].sort());
});

test("NoBuild without generated Bridge code fails with RUNICBRIDGE022", () => {
  let { project } = noBuildProject("no-build-missing");
  let result = generateWithoutBuild(project);
  assert.notEqual(result.status, 0, result.output);
  assert.deepEqual(errors(result.output).map(line => line.match(/error (\w+):/)?.[1]), ["RUNICBRIDGE022"], result.output);
  assert.doesNotMatch(result.output, /NETSDK1085/);

  // A composition type needs its composition too.
  ({ project } = noBuildProject("no-build-no-composition", { generated: ["App.CounterWindow.View.g.cs"] }));
  result = generateWithoutBuild(project);
  assert.deepEqual(errors(result.output).map(line => line.match(/error (\w+):/)?.[1]), ["RUNICBRIDGE022"], result.output);

  // An application that declared no View left the marker and needs no code.
  ({ project } = noBuildProject("no-build-no-views", { noViews: true }));
  result = generateWithoutBuild(project);
  assert.equal(result.status, 0, result.output);
});

// S1 (D053.25): a publish passes its properties to the whole build. The generator's own build and the bootstrap pass
// clear the runtime identifier, so PublishReadyToRun reached them without one and failed with NETSDK1191, as
// PublishTrimmed did before #743 and PublishSingleFile did until 0.22 (D072.7, FD3). A stand-in dotnet on PATH answers
// the generator's run; the generator project itself is built for real (CONFIGURATION, as eng/run.mjs builds it). The
// source-tree ProjectReferences to the generator and the analyzers drop the same properties.
test("publish properties reach neither the generator's build nor the bootstrap pass", () => {
  const project = join(directory, "publish-properties");
  const fake = join(project, "fake-dotnet");
  mkdirSync(fake, { recursive: true });
  writeFileSync(join(fake, "dotnet"), `#!/bin/sh\necho "$@" >> "${join(project, "generator.log")}"\n`, { mode: 0o755 });
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <AssemblyName>App</AssemblyName>
    <RunicApplicationFrontendGenerationEnabled>true</RunicApplicationFrontendGenerationEnabled>
    <RunicApplicationFrontendBuildEnabled>false</RunicApplicationFrontendBuildEnabled>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <!-- The Microsoft.NET.Sdk targets that RunicViewsGenerateBridge depends on, and the bootstrap pass's Build. -->
  <Target Name="ResolveProjectReferences" />
  <Target Name="InitializeSourceRootMappedPaths" />
  <Target Name="Build">
    <WriteLinesToFile File="$(MSBuildProjectDirectory)/bootstrap.log" Overwrite="true"
                      Lines="RuntimeIdentifier=$(RuntimeIdentifier);PublishReadyToRun=$(PublishReadyToRun);PublishAot=$(PublishAot);PublishTrimmed=$(PublishTrimmed);PublishSingleFile=$(PublishSingleFile);IncludeNativeLibrariesForSelfExtract=$(IncludeNativeLibrariesForSelfExtract);SelfContained=$(SelfContained)" />
  </Target>
  <Import Project="${targets}" />
</Project>
`);
  const dotnet = spawnSync("sh", ["-c", "command -v dotnet"], { encoding: "utf8" }).stdout.trim();
  const result = spawnSync(dotnet, ["msbuild", join(project, "App.proj"), "-t:RunicViewsGenerateBridge", `-p:Configuration=${configuration}`,
    "-p:RuntimeIdentifier=linux-x64", "-p:PublishReadyToRun=true", "-p:PublishAot=true", "-p:PublishTrimmed=true", "-p:SelfContained=true",
    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"],
    { cwd: project, encoding: "utf8", env: { ...process.env, PATH: `${fake}:${process.env.PATH}` } });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.doesNotMatch(result.stdout, /NETSDK\d+/);
  assert.deepEqual(readFileSync(join(project, "bootstrap.log"), "utf8").trim().split("\n"),
    ["RuntimeIdentifier=", "PublishReadyToRun=false", "PublishAot=false", "PublishTrimmed=false",
      "PublishSingleFile=false", "IncludeNativeLibrariesForSelfExtract=false", "SelfContained=false"]);
  assert.match(readFileSync(join(project, "generator.log"), "utf8"), /--generate/);
  const references = [...readFileSync(targets, "utf8").matchAll(
    /<ProjectReference Include="\$\((_RunicBridgeCodegenProject|_RunicViewsAnalyzersProject)\)[^>]*?GlobalPropertiesToRemove="([^"]*)"/g)];
  assert.deepEqual(references.map(([, name]) => name), ["_RunicBridgeCodegenProject", "_RunicViewsAnalyzersProject"]);
  for (const [, name, removed] of references) {
    for (const property of ["RuntimeIdentifier", "PublishReadyToRun", "PublishAot", "PublishTrimmed", "PublishSingleFile",
      "IncludeNativeLibrariesForSelfExtract", "SelfContained"]) {
      assert.ok(removed.split(";").includes(property), `${name} keeps ${property}`);
    }
  }
});

// S1 (D053.25): every build started the generator twice, once for the bootstrap pass's composition stub and once for
// the Bridges. The stub is written again only when the composition type, the generator or a reference changed, or it
// is missing. The generator (an input) is the one eng/run.mjs built.
test("the bootstrap pass writes its composition stub only when an input changed", () => {
  const project = join(directory, "bootstrap-stub");
  const fake = join(project, "fake-dotnet");
  mkdirSync(fake, { recursive: true });
  writeFileSync(join(fake, "dotnet"), `#!/bin/sh
echo "$@" >> "${join(project, "generator.log")}"
[ "$2" = --composition-stub ] && echo "// stub" > "$4"
exit 0
`, { mode: 0o755 });
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <IntermediateOutputPath>obj/</IntermediateOutputPath>
    <RunicApplicationFrontendBootstrap>true</RunicApplicationFrontendBootstrap>
  </PropertyGroup>
  <Target Name="ResolveAssemblyReferences" />
  <Import Project="${targets}" />
</Project>
`);
  const dotnet = spawnSync("sh", ["-c", "command -v dotnet"], { encoding: "utf8" }).stdout.trim();
  const stubs = (composition = "App.RunicViews") => {
    const result = spawnSync(dotnet, ["msbuild", join(project, "App.proj"), "-t:RunicViewsBootstrapComposition",
      `-p:RunicApplicationFrontendCompositionType=${composition}`, `-p:Configuration=${configuration}`, "-getItem:Compile", "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m"],
      { cwd: project, encoding: "utf8", env: { ...process.env, PATH: `${fake}:${process.env.PATH}` } });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const compile = JSON.parse(result.stdout.slice(result.stdout.indexOf("{"))).Items.Compile.map(item => item.Filename + item.Extension);
    assert.deepEqual(compile, ["RunicBridgeComposition.Bootstrap.g.cs"]);
    return readFileSync(join(project, "generator.log"), "utf8").trim().split("\n").length;
  };
  assert.equal(stubs(), 1);
  assert.equal(stubs(), 1, "an unchanged build wrote the stub again");
  rmSync(join(project, "obj/RunicBridgeComposition.Bootstrap.g.cs"));
  assert.equal(stubs(), 2, "a missing stub was not written again");
  assert.equal(stubs("App.Other"), 3, "a new composition type did not write the stub again");
  assert.equal(stubs("App.Other"), 3);
});

// #1003: the bootstrap pass passes its own OutputPath and IntermediateOutputPath to the projects it references. The SDK
// adds a project's transitive references to ProjectReference during the build, after the targets' evaluation-time
// update, so each transitive reference was compiled again into the bootstrap's bin/ and obj/, all of them into one
// IntermediateOutputPath at once, and csc on Windows failed to open the shared AssemblyAttributes.cs (CS1504).
// App references Model, which references Core: Core is App's transitive reference, as Runic.Assets is a Views
// application's. The real SDK resolves the references.
test("the bootstrap pass builds transitive references with their own output paths", () => {
  const project = join(directory, "bootstrap-transitive");
  const library = (name, reference = "") => {
    mkdirSync(join(project, name), { recursive: true });
    writeFileSync(join(project, name, `${name}.csproj`), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  ${reference && `<ItemGroup><ProjectReference Include="../${reference}/${reference}.csproj" /></ItemGroup>`}
  ${name === "App" ? `<Import Project="${targets}" />` : ""}
</Project>
`);
  };
  library("Core");
  library("Model", "Core");
  library("App", "Model");
  // An empty Directory.Build.props stops the search for one above the temporary directory.
  writeFileSync(join(project, "Directory.Build.props"), "<Project />\n");
  const references = (bootstrap) => {
    const result = spawnSync("dotnet", ["msbuild", join(project, "App/App.csproj"), "-restore", "-t:AssignProjectConfiguration",
      ...(bootstrap ? ["-p:RunicApplicationFrontendBootstrap=true"] : []), "-getItem:ProjectReference",
      "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m"], { cwd: project, encoding: "utf8" });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    return Object.fromEntries(JSON.parse(result.stdout.slice(result.stdout.indexOf("{"))).Items.ProjectReference
      .map(item => [item.Filename, (item.GlobalPropertiesToRemove ?? "").split(";").filter(Boolean)]));
  };
  const removed = ["RunicApplicationFrontendBootstrap", "RunAnalyzers", "RunCodeAnalysis", "CopyLocalLockFileAssemblies", "OutputPath", "IntermediateOutputPath"];
  const bootstrap = references(true);
  assert.deepEqual(Object.keys(bootstrap).sort(), ["Core", "Model"], "the SDK added no transitive reference");
  for (const [name, properties] of Object.entries(bootstrap))
    assert.deepEqual(properties.filter(property => removed.includes(property)), removed, name);
  // The real build's references keep their metadata.
  for (const [name, properties] of Object.entries(references(false)))
    assert.deepEqual(properties.filter(property => removed.includes(property)), [], name);
});

// #1424: the bootstrap pass passed PublishAot=false and the other publish properties it clears on to its references,
// so a library that the solution also builds was built a second time with them, a second instance writing the
// library's bin/ and obj/, and its own bootstrap pass ran twice into one directory. With a test project that builds the
// application alongside, the instances ran at once and a build failed with an IOException on a file in bin/ or
// obj/runic-bridge/bootstrap/. The solution is the library template's: an application, its library and a test project
// that references the application. Each instance of a project runs its Build target once and leaves a file, so a
// project built with two property sets leaves two; with -m, the count is the same whichever instances overlap.
test("a solution builds an application and its library once each, with one bootstrap pass each", () => {
  const solution = join(directory, "solution-instances");
  const project = (name, { exe = false, reference = "", views = true } = {}) => {
    mkdirSync(join(solution, name), { recursive: true });
    writeFileSync(join(solution, name, `${name}.csproj`), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>${exe ? "<OutputType>Exe</OutputType>" : ""}<TargetFramework>net10.0</TargetFramework></PropertyGroup>
  ${reference && `<ItemGroup><ProjectReference Include="../${reference}/${reference}.csproj" /></ItemGroup>`}
  ${views ? `<Import Project="${targets}" />` : ""}
</Project>
`);
    writeFileSync(join(solution, name, exe ? "Program.cs" : "Library.cs"),
      exe ? "System.Console.WriteLine();\n" : `namespace ${name};\npublic static class Library { }\n`);
  };
  project("Lib");
  project("App", { exe: true, reference: "Lib" });
  project("App.Tests", { exe: true, reference: "App", views: false });
  writeFileSync(join(solution, "Directory.Build.props"), "<Project />\n");
  writeFileSync(join(solution, "Directory.Build.targets"), `<Project>
  <Target Name="RecordInstance" AfterTargets="Build">
    <WriteLinesToFile File="$(MSBuildThisFileDirectory)instances/$(MSBuildProjectName).$([MSBuild]::ValueOrDefault('$(RunicApplicationFrontendBootstrap)', 'false')).$([System.Guid]::NewGuid().ToString('N'))" Lines="$(MSBuildProjectName)" />
  </Target>
</Project>
`);
  writeFileSync(join(solution, "Solution.slnx"), `<Solution>
  <Project Path="App/App.csproj" />
  <Project Path="Lib/Lib.csproj" />
  <Project Path="App.Tests/App.Tests.csproj" />
</Solution>
`);
  const result = spawnSync("dotnet", ["build", join(solution, "Solution.slnx"), `-p:Configuration=${configuration}`, "-m",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: solution, encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const instances = {};
  for (const file of readdirSync(join(solution, "instances"))) {
    const key = file.split(".").slice(0, -1).join(".");
    instances[key] = (instances[key] ?? 0) + 1;
  }
  assert.deepEqual(instances, { "App.false": 1, "App.true": 1, "App.Tests.false": 1, "Lib.false": 1, "Lib.true": 1 });
});

// W319-003 msbuild backlog: the targets wrote to obj/ whatever UseArtifactsOutput, BaseIntermediateOutputPath or
// IntermediateOutputPath said. The generated code follows IntermediateOutputPath, which has the runtime identifier.
test("the generated Bridge code lives in IntermediateOutputPath, else below BaseIntermediateOutputPath", () => {
  const generated = ["App.CounterWindow.View.g.cs", "RunicBridgeComposition.g.cs"];
  for (const [name, properties, directory] of [
    ["intermediate-output", ["-p:IntermediateOutputPath=out/obj/debug_linux-x64/"], "out/obj/debug_linux-x64/runic-bridge"],
    ["base-intermediate-output", ["-p:BaseIntermediateOutputPath=out/obj/App/"], "out/obj/App/Debug/net10.0/runic-bridge"],
  ]) {
    const { project } = noBuildProject(`no-build-${name}`);
    const output = join(project, directory);
    mkdirSync(output, { recursive: true });
    for (const file of generated) writeFileSync(join(output, file), "// generated\n");
    const result = generateWithoutBuild(project, [...properties, "-getItem:Compile"]);
    assert.equal(result.status, 0, result.output);
    const compile = JSON.parse(result.output.slice(result.output.indexOf("{"))).Items.Compile.map(item => item.FullPath);
    assert.deepEqual(compile.sort(), generated.map(file => join(output, file)).sort(), name);
  }
});

// Off Windows, WPF passes its temporary markup project an IntermediateOutputPath that still ends with the target
// framework, and the copy appends it again (obj/Release/net10.0-windows/net10.0-windows/). The copy compiles the code
// that the real project recorded instead.
test("WPF's temporary markup project compiles the generated code the real project recorded", () => {
  const generated = ["App.CounterWindow.View.g.cs", "RunicBridgeComposition.g.cs"];
  const { project, output } = noBuildProject("markup-temporary", { generated });
  let result = generateWithoutBuild(project, ["-p:UseWPF=true"]);
  assert.equal(result.status, 0, result.output);
  const record = join(project, "obj/runic-bridge/App-Debug-net10.0-.generated-directory");
  assert.equal(readFileSync(record, "utf8").trim(), `${output}/`);

  result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsMarkupTemporaryCompile",
    "-p:_TargetAssemblyProjectName=App", "-p:UseWPF=true", "-p:IntermediateOutputPath=obj/Debug/net10.0/net10.0/", "-getItem:Compile",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m"], { cwd: project, encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const compile = JSON.parse(result.stdout.slice(result.stdout.indexOf("{"))).Items.Compile.map(item => item.FullPath);
  assert.deepEqual(compile.sort(), generated.map(file => join(output, file)).sort());
});

test("the frontend install and build state live below BaseIntermediateOutputPath", () => {
  const app = fixture("base-intermediate-frontend", { name: "app", private: true, devDependencies: { vite: "8.3.2" } });
  const properties = ["BaseIntermediateOutputPath=out/obj/App/"];
  let result = build(app, { properties });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["install", "build"]);
  for (const file of ["runic-bridge-frontend-install.stamp", "runic-bridge-frontend.state",
    "Debug/net10.0/runic-bridge-frontend.stamp", "Debug/net10.0/runic-bridge-frontend.outputs"])
    assert.ok(existsSync(join(app.project, "out/obj/App", file)), file);
  assert.ok(!existsSync(join(app.project, "obj")), "Nothing is written to obj/.");
  result = build(app, { properties });
  assert.deepEqual(result.commands, [], "The relocated stamps keep an unchanged frontend up to date.");
});

// ViewsCodegen-03: the default composition type follows RootNamespace, made a valid C# namespace, so a project
// named 2048Game or with a custom RootNamespace needs no RunicApplicationFrontendCompositionType.
function defaultComposition(name, rootNamespace, hostAdapter = true) {
  const project = join(directory, `composition-${name.replace(/[^A-Za-z0-9]/g, "_")}`);
  mkdirSync(project, { recursive: true });
  const projectFile = join(project, `${name}.proj`);
  writeFileSync(projectFile, `<Project>
  <PropertyGroup>
    ${hostAdapter ? "<_RunicApplicationHostAdapter>true</_RunicApplicationHostAdapter>" : ""}
    ${rootNamespace === undefined ? "" : `<RootNamespace>${rootNamespace}</RootNamespace>`}
  </PropertyGroup>
  <Import Project="${targets}" />
</Project>
`);
  const result = spawnSync("dotnet", ["msbuild", projectFile, "-getProperty:RunicApplicationFrontendCompositionType",
    "-nologo", "-noAutoResponse", "-nodeReuse:false"], { cwd: project, encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  return result.stdout.trim();
}

test("the default composition type is the sanitized RootNamespace's RunicViewsComposition", () => {
  assert.equal(defaultComposition("Notes", "Contoso.Notes"), "Contoso.Notes.RunicViewsComposition");
  assert.equal(defaultComposition("My-App", undefined), "My_App.RunicViewsComposition");
  assert.equal(defaultComposition("2048Game", undefined), "_2048Game.RunicViewsComposition");
  // I-062: a project name with a segment that starts with a digit builds (since 0.9); nothing to report.
  assert.equal(defaultComposition("9Lives", undefined), "_9Lives.RunicViewsComposition");
  assert.equal(defaultComposition("My.2App", undefined), "My._2App.RunicViewsComposition");
  assert.equal(defaultComposition("Keywords", "Contoso.class.9lives.Classic"), "Contoso._class._9lives.Classic.RunicViewsComposition");
  assert.equal(defaultComposition("Spaces", "My App (Beta)"), "My_App__Beta_.RunicViewsComposition");
  assert.equal(defaultComposition("Library", "Contoso.Library", false), "", "A project without a host adapter gets no composition.");
});

// A1: a test project that uses an application's generated Bridges keeps generation off, so it gets no RUNICBRIDGE006
// for declaring no Window: IsTestProject (Microsoft.NET.Test.Sdk, TUnit), or a direct Runic.Application.Testing
// reference, as in a console test runner that a host adapter reaches through the application's reference.
function testProjectGeneration(name, items, properties = "") {
  const project = join(directory, `test-project-${name}`);
  mkdirSync(project, { recursive: true });
  writeFileSync(join(project, "Checks.cs"), "public static class Checks { }\n");
  const projectFile = join(project, "Checks.proj");
  writeFileSync(projectFile, `<Project>
  <PropertyGroup>
    <_RunicApplicationHostAdapter>true</_RunicApplicationHostAdapter>
    <RunicApplicationFrontendCompositionType>Checks.RunicViewsComposition</RunicApplicationFrontendCompositionType>
${properties}
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Checks.cs" />
${items}
  </ItemGroup>
  <Import Project="${targets}" />
</Project>
`);
  const result = spawnSync("dotnet", ["msbuild", projectFile, "-t:RunicViewsDetectBridgeUse",
    "-getProperty:RunicApplicationFrontendGenerationEnabled", "-nologo", "-noAutoResponse", "-nodeReuse:false"],
    { cwd: project, encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  return result.stdout.trim();
}

test("a test project that declares no Window keeps Bridge generation off", () => {
  assert.equal(testProjectGeneration("console", ""), "true", "Another project with a host adapter generates.");
  assert.equal(testProjectGeneration("is-test-project", "", "<IsTestProject>true</IsTestProject>"), "false");
  assert.equal(testProjectGeneration("package", '<PackageReference Include="Runic.Application.Testing" Version="0.14.0" />'), "false");
  assert.equal(testProjectGeneration("project",
    '<ProjectReference Include="../../packages/dotnet/Runic.Application.Testing/Runic.Application.Testing.csproj" />'), "false");
  assert.equal(testProjectGeneration("explicit", '<PackageReference Include="Runic.Application.Testing" Version="0.14.0" />',
    "<RunicApplicationFrontendGenerationEnabled>true</RunicApplicationFrontendGenerationEnabled>"), "true",
    "A test project that asks for generation keeps it.");
});

// I-027: RunicApplicationFrontendDelivery=Embedded stages the www/ tree in obj/ and lets Runic.Assets pack it; the
// build output gets no www/. RUNICBRIDGE030 reports what cannot embed, and the build continues as Files.
const assetsBuild = resolve(root, "packages/dotnet/Runic.Assets/buildTransitive");
function deliveryProject(name, hosts, properties = "") {
  const project = hostProject(name, hosts);
  mkdirSync(join(project, "frontend/dist/assets"), { recursive: true });
  writeFileSync(join(project, "frontend/dist/assets/app.js"), "console.log(1)");
  writeFileSync(join(project, "packer.mjs"), `import { writeFileSync, mkdirSync } from "node:fs";
import { dirname } from "node:path";
const args = process.argv.slice(2);
mkdirSync(dirname(args[1]), { recursive: true });
writeFileSync(args[1], "archive");
writeFileSync(${JSON.stringify(join(project, "packer-arguments.json"))}, JSON.stringify(args));
`);
  const text = readFileSync(join(project, "App.proj"), "utf8")
    .replace("<Project>", `<Project>\n  <Import Project="${join(assetsBuild, "Runic.Assets.props")}" />`)
    .replace("</PropertyGroup>", `    <IntermediateOutputPath>obj/</IntermediateOutputPath>
    <RunicAssetsPackerPath>$(MSBuildProjectDirectory)/packer.mjs</RunicAssetsPackerPath>
    <DotNetHostPath>${process.execPath}</DotNetHostPath>
${properties}
  </PropertyGroup>`)
    .replace("</Project>", `  <Import Project="${join(assetsBuild, "Runic.Assets.targets")}" />
  <Target Name="ShowDelivery">
    <Message Importance="high" Text="RUNTIME_OPTION=@(RuntimeHostConfigurationOption->'%(Identity)=%(Value)')" />
    <Message Importance="high" Text="RESOURCE=@(_CoreCompileResourceInputs->'%(LogicalName)')" />
  </Target>
</Project>`);
  writeFileSync(join(project, "App.proj"), text);
  return project;
}

function buildDelivery(project, extra = []) {
  rmSync(join(project, "bin"), { recursive: true, force: true });
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"),
    "-t:RunicViewsResolveHost;RunicViewsResolveFrontendDelivery;RunicAssetsEmbedStagedFileSystem;RunicViewsCopyFrontend;RunicViewsMirrorFrontend;ShowDelivery",
    "-p:_RunicViewsEmbeddedFrontendBuildTargets=", ...extra,
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  const output = `${result.stdout}${result.stderr}`;
  const packer = join(project, "packer-arguments.json");
  const packed = existsSync(packer) ? JSON.parse(readFileSync(packer, "utf8")) : undefined;
  rmSync(packer, { force: true });
  return { project, status: result.status, output, packed,
    www: existsSync(join(project, "bin/www")) ? files(join(project, "bin/www")) : undefined,
    staged: existsSync(join(project, "obj/runic-www")) ? files(join(project, "obj/runic-www")) : undefined,
    warnings: [...new Set(output.split("\n").filter(line => / warning RUNICBRIDGE030:/.test(line)).map(line => line.trim()))] };
}

test("RunicApplicationFrontendDelivery=Embedded stages www/ for Runic.Assets and copies no www/", () => {
  const project = deliveryProject("embedded", ["Desktop"], "<RunicApplicationFrontendDelivery>embedded</RunicApplicationFrontendDelivery>");
  let result = buildDelivery(project);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.warnings, []);
  assert.equal(result.www, undefined, result.output);
  assert.deepEqual(result.staged, ["assets/app.js", "index.html", "runic-views.js"]);
  assert.equal(readFileSync(join(project, "obj/runic-www/runic-views.js"), "utf8"), hostClient("Desktop"));
  assert.ok(result.packed, result.output);
  assert.equal(resolve(result.packed[0]), resolve(project, "obj/runic-www"), result.packed.join(" "));
  assert.match(result.output, /RUNTIME_OPTION=Runic\.Application\.FrontendArchive=Runic\.Assets\.StaticFiles/);
  assert.match(result.output, /RESOURCE=Runic\.Assets\.StaticFiles/);

  // The staged tree mirrors the frontend: a file the frontend no longer has is gone.
  rmSync(join(project, "frontend/dist/assets/app.js"));
  result = buildDelivery(project);
  assert.deepEqual(result.staged, ["index.html", "runic-views.js"]);

  // Files, the default, copies www/ and packs nothing.
  result = buildDelivery(deliveryProject("files", ["Desktop"]));
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.www, ["assets/app.js", "index.html", "runic-views.js"]);
  assert.equal(result.packed, undefined);
  assert.doesNotMatch(result.output, /FrontendArchive/);
});

// #726: CS-WebUI serves the archive through its file handler, as Runic Desktop does.
test("RunicApplicationFrontendDelivery=Embedded stages the CS-WebUI client", () => {
  for (const [name, hosts, extra] of [["cswebui", ["CsWebUi"], []], ["both-cswebui", ["Desktop", "CsWebUi"], ["-p:RunicApplicationHost=cswebui"]]]) {
    const result = buildDelivery(deliveryProject(`embedded-${name}`, hosts,
      "<RunicApplicationFrontendDelivery>Embedded</RunicApplicationFrontendDelivery>"), extra);
    assert.equal(result.status, 0, result.output);
    assert.deepEqual(result.warnings, [], name);
    assert.equal(result.www, undefined, `${name}: ${result.output}`);
    assert.deepEqual(result.staged, ["assets/app.js", "index.html", "runic-views.js"], name);
    assert.equal(readFileSync(join(result.project, "obj/runic-www/runic-views.js"), "utf8"), hostClient("CsWebUi"), name);
    assert.ok(result.packed, result.output);
    assert.match(result.output, /RUNTIME_OPTION=Runic\.Application\.FrontendArchive=Runic\.Assets\.StaticFiles/, name);
  }
});

test("RUNICBRIDGE030: an unknown value, no single host and an own RunicAssetsDist build as Files", () => {
  const cases = [
    ["unknown", ["Desktop"], "<RunicApplicationFrontendDelivery>Inline</RunicApplicationFrontendDelivery>", [], /RunicApplicationFrontendDelivery is 'Inline', which is not Files or Embedded/],
    ["both-unchosen", ["Desktop", "CsWebUi"], "<RunicApplicationFrontendDelivery>Embedded</RunicApplicationFrontendDelivery>", [], /runs on neither the Runic Desktop host .* nor the CS-WebUI host/],
    ["own-dist", ["Desktop"], "<RunicApplicationFrontendDelivery>Embedded</RunicApplicationFrontendDelivery><RunicAssetsDist>$(MSBuildProjectDirectory)/frontend/dist</RunicAssetsDist>", [], /sets RunicAssetsDist or RunicAssetsEmbeddedArchive itself/],
  ];
  for (const [name, hosts, properties, extra, message] of cases) {
    const result = buildDelivery(deliveryProject(`delivery-${name}`, hosts, properties), extra);
    assert.equal(result.status, 0, result.output);
    assert.equal(result.warnings.length, 1, `${name}: ${result.output}`);
    assert.match(result.warnings[0], message, name);
    assert.match(result.warnings[0], /so the build copies the frontend to www\/ \(Files\)/, name);
    assert.equal(result.staged, undefined, name);
    assert.ok(result.www?.includes("index.html"), `${name} copies www/: ${result.output}`);
    assert.doesNotMatch(result.output, /RUNTIME_OPTION=Runic\.Application\.FrontendArchive/, name);
  }

  // dotnet runic dev's development server builds without copying the frontend: Files, silently.
  const dev = buildDelivery(deliveryProject("delivery-dev", ["Desktop"], "<RunicApplicationFrontendDelivery>Embedded</RunicApplicationFrontendDelivery>"),
    ["-p:RunicApplicationFrontendCopyEnabled=false"]);
  assert.equal(dev.status, 0, dev.output);
  assert.deepEqual(dev.warnings, []);
  assert.equal(dev.staged, undefined);
  assert.equal(dev.packed, undefined);
});

// #1318: a client library (RunicApplicationFrontendClientLibrary=true) generates its Views' TypeScript as a module that
// other frontends import. Its generation is required, it builds and copies no frontend unless it asks to, and an
// application that references it does not copy its frontend to www/.
test("a client library builds and copies no frontend unless it asks to", () => {
  const library = fixture("client-library", { name: "shared-ui", private: true });
  writeFileSync(join(library.frontend, "emits-dist"), "");
  const output = join(library.project, "out") + "/";
  const properties = ["RunicApplicationFrontendClientLibrary=true", `TargetDir=${output}`];
  let result = build(library, { targets: "RunicViewsCollectFrontendInputs;RunicViewsCopyFrontend", properties });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, [], "A client library neither installs nor builds its frontend by default.");
  assert.ok(!existsSync(join(output, "www")), "A client library copies no frontend to www/.");
  assert.doesNotMatch(result.output, /RUNICBRIDGE014/);

  // The project may still build its package from MSBuild; the output is not copied.
  result = build(library, { targets: "RunicViewsCollectFrontendInputs;RunicViewsCopyFrontend",
    properties: [...properties, "RunicApplicationFrontendBuildEnabled=true"] });
  assert.equal(result.status, 0, result.output);
  assert.equal(builds(result), 1);
  assert.ok(existsSync(join(library.frontend, "dist/index.html")) && !existsSync(join(output, "www")), result.output);

  const evaluate = extra => {
    const result = spawnSync("dotnet", ["msbuild", join(library.project, "App.proj"), "-nologo", "-noAutoResponse", "-nodeReuse:false",
      ...extra, "-getProperty:RunicApplicationFrontendBuildEnabled", "-getProperty:RunicApplicationFrontendCopyEnabled"],
      { cwd: library.project, encoding: "utf8" });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    return JSON.parse(result.stdout).Properties;
  };
  assert.deepEqual(evaluate(["-p:RunicApplicationFrontendClientLibrary=true"]),
    { RunicApplicationFrontendBuildEnabled: "false", RunicApplicationFrontendCopyEnabled: "false" });
  assert.deepEqual(evaluate([]), { RunicApplicationFrontendBuildEnabled: "", RunicApplicationFrontendCopyEnabled: "" },
    "Without the property, nothing changes.");
});

// The generator gets --client-library, and never --optional: a client library without a View is RUNICBRIDGE006.
test("a client library passes --client-library to the generator, which requires its Views", () => {
  const project = join(directory, "client-library-arguments");
  const fake = join(project, "fake-dotnet");
  mkdirSync(fake, { recursive: true });
  writeFileSync(join(fake, "dotnet"), `#!/bin/sh\necho "$@" >> "${join(project, "generator.log")}"\n`, { mode: 0o755 });
  // No frontend and no RunicApplicationFrontendGenerationEnabled: generation is on by default and optional.
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <AssemblyName>SharedUi</AssemblyName>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <Target Name="ResolveProjectReferences" />
  <Target Name="InitializeSourceRootMappedPaths" />
  <Target Name="Build" />
  <Import Project="${targets}" />
</Project>
`);
  const dotnet = spawnSync("sh", ["-c", "command -v dotnet"], { encoding: "utf8" }).stdout.trim();
  const generate = (extra = []) => {
    rmSync(join(project, "generator.log"), { force: true });
    // RunicViewsGenerateBridge's condition is evaluated before its dependencies, so detect the generation first, as
    // PrepareForBuild does.
    const result = spawnSync(dotnet, ["msbuild", join(project, "App.proj"), "-t:RunicViewsDetectBridgeUse;RunicViewsGenerateBridge",
      `-p:Configuration=${configuration}`, ...extra, "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"],
      { cwd: project, encoding: "utf8", env: { ...process.env, PATH: `${fake}:${process.env.PATH}` } });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    return readFileSync(join(project, "generator.log"), "utf8").split("\n").find(line => line.includes("--generate")).split(" ");
  };
  // The fake generator writes no NoViews marker, so an application without a frontend would be RUNICBRIDGE014.
  let generator = generate(["-p:RunicApplicationFrontendBuildEnabled=false"]);
  assert.ok(generator.includes("--optional") && !generator.includes("--client-library"), generator.join(" "));
  generator = generate(["-p:RunicApplicationFrontendClientLibrary=true"]);
  assert.ok(generator.includes("--client-library") && !generator.includes("--optional"), generator.join(" "));
});

test("an application does not copy a referenced client library's frontend to www/", () => {
  const project = join(directory, "client-library-reference");
  // The Microsoft.NET.Sdk targets that RunicViewsCollectReferencedFrontends depends on.
  const references = `  <Target Name="AssignProjectConfiguration" />
  <Target Name="_GetProjectReferenceTargetFrameworkProperties" />
  <Target Name="_SplitProjectReferencesByFileExistence">
    <ItemGroup><_MSBuildProjectReferenceExistent Include="@(ProjectReference)" /></ItemGroup>
  </Target>`;
  const reference = (name, page, properties = "") => {
    mkdirSync(join(project, name, "frontend/dist"), { recursive: true });
    writeFileSync(join(project, name, "frontend/dist", page), `<p>${name}</p>`);
    writeFileSync(join(project, name, `${name}.proj`), `<Project>
  <PropertyGroup>
    <RunicApplicationFrontendGenerationEnabled>true</RunicApplicationFrontendGenerationEnabled>
    <RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/frontend</RunicApplicationFrontendDirectory>
${properties}
  </PropertyGroup>
${references}
  <Import Project="${targets}" />
</Project>
`);
  };
  // A model project whose frontend is a page (#1318 keeps that), and a client library whose frontend is a package.
  reference("Model", "model.html");
  reference("SharedUi", "shared-ui.html", "    <RunicApplicationFrontendClientLibrary>true</RunicApplicationFrontendClientLibrary>");
  mkdirSync(join(project, "App"), { recursive: true });
  writeFileSync(join(project, "App/App.proj"), `<Project>
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetDir>$(MSBuildProjectDirectory)/bin/</TargetDir>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Model/Model.proj" />
    <ProjectReference Include="../SharedUi/SharedUi.proj" />
  </ItemGroup>
${references}
  <Import Project="${targets}" />
</Project>
`);
  const result = spawnSync("dotnet", ["msbuild", join(project, "App/App.proj"), "-t:RunicViewsCopyReferencedFrontends",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.deepEqual(readdirSync(join(project, "App/bin/www")), ["model.html"]);
});

test("a client library compiles the library-mode marker", () => {
  const generated = ["App.AboutDialogViewModel.Bridge.g.cs", "App.AboutDialogView.View.g.cs", "RunicBridgeComposition.g.cs", "RunicClientLibrary.g.cs"];
  const { project, output } = noBuildProject("no-build-client-library", { generated });
  const compile = extra => {
    const result = generateWithoutBuild(project, [...extra, "-getItem:Compile"]);
    assert.equal(result.status, 0, result.output);
    return JSON.parse(result.output.slice(result.output.indexOf("{"))).Items.Compile.map(item => item.FullPath.slice(output.length + 1)).sort();
  };
  assert.deepEqual(compile(["-p:RunicApplicationFrontendClientLibrary=true"]), [...generated].sort());
  assert.deepEqual(compile([]), generated.filter(file => file !== "RunicClientLibrary.g.cs").sort(),
    "Only a client library compiles the marker, which the generator deletes without library mode.");
});

// #1318: RunicApplicationFrontendClientModule maps a referenced client library's assembly to the specifier of its
// generated runic-clients.ts, which the application's generated modules import instead of generating copies.
test("each RunicApplicationFrontendClientModule becomes a --client-module argument", () => {
  const project = join(directory, "client-module-arguments");
  const fake = join(project, "fake-dotnet");
  mkdirSync(fake, { recursive: true });
  writeFileSync(join(fake, "dotnet"), `#!/bin/sh\nprintf '%s\\n' "$@" > "${join(project, "generator.log")}"\n`, { mode: 0o755 });
  const dotnet = spawnSync("sh", ["-c", "command -v dotnet"], { encoding: "utf8" }).stdout.trim();
  const generate = items => {
    writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <AssemblyName>App</AssemblyName>
    <TargetFramework>net10.0</TargetFramework>
    <RunicApplicationFrontendBuildEnabled>false</RunicApplicationFrontendBuildEnabled>
  </PropertyGroup>
  <ItemGroup>
${items}
  </ItemGroup>
  <Target Name="ResolveProjectReferences" />
  <Target Name="InitializeSourceRootMappedPaths" />
  <Target Name="Build" />
  <Import Project="${targets}" />
</Project>
`);
    rmSync(join(project, "generator.log"), { force: true });
    const result = spawnSync(dotnet, ["msbuild", join(project, "App.proj"), "-t:RunicViewsDetectBridgeUse;RunicViewsGenerateBridge",
      `-p:Configuration=${configuration}`, "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"],
      { cwd: project, encoding: "utf8", env: { ...process.env, PATH: `${fake}:${process.env.PATH}` } });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const lines = readFileSync(join(project, "generator.log"), "utf8").split("\n");
    return lines.flatMap((line, index) => line === "--client-module" ? [lines[index + 1]] : []);
  };
  assert.deepEqual(generate(`    <RunicApplicationFrontendClientModule Include="SharedUi" Specifier="@acme/shared-ui/generated" />
    <RunicApplicationFrontendClientModule Include="Acme.Forms" Specifier=" @acme/forms/generated " />`),
  ["SharedUi=@acme/shared-ui/generated", "Acme.Forms=@acme/forms/generated"]);
  assert.deepEqual(generate(""), [], "Without the item, the generator gets no --client-module.");
});

// #1318: the analyzers add a mapped client library's Views to the application's, as the generator does, so a slot that
// presents a library ViewModel is content (BridgeSymbols.FindViews). The targets give them the items' names.
test("the analyzers see the assemblies that RunicApplicationFrontendClientModule maps", () => {
  const project = join(directory, "client-module-analyzers");
  mkdirSync(project, { recursive: true });
  const evaluate = items => {
    writeFileSync(join(project, "App.proj"), `<Project>
  <ItemGroup>
${items}
  </ItemGroup>
  <Target Name="GenerateMSBuildEditorConfigFileCore" />
  <Import Project="${targets}" />
</Project>
`);
    const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:GenerateMSBuildEditorConfigFileCore",
      "-getProperty:_RunicApplicationFrontendClientModules", "-getItem:CompilerVisibleProperty", "-nologo", "-noAutoResponse",
      "-nodeReuse:false"], { cwd: project, encoding: "utf8" });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const json = JSON.parse(result.stdout.slice(result.stdout.indexOf("{")));
    assert.ok(json.Items.CompilerVisibleProperty.some(item => item.Identity === "_RunicApplicationFrontendClientModules"), result.stdout);
    return json.Properties._RunicApplicationFrontendClientModules;
  };
  assert.equal(evaluate(`    <RunicApplicationFrontendClientModule Include="SharedUi" Specifier="@acme/shared-ui/generated" />
    <RunicApplicationFrontendClientModule Include="Acme.Forms" Specifier="@acme/forms/generated" />`), "SharedUi;Acme.Forms");
  assert.equal(evaluate(""), "", "Without the item, the analyzers map nothing.");
});

test("RUNICBRIDGE082: a RunicApplicationFrontendClientModule without a Specifier, with a relative one, or twice", () => {
  const project = join(directory, "client-module-items");
  mkdirSync(project, { recursive: true });
  const check = items => {
    writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
${items}
  </ItemGroup>
  <Import Project="${targets}" />
</Project>
`);
    const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsCheckClientModules",
      "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
    return { status: result.status, errors: errors(`${result.stdout}${result.stderr}`) };
  };
  const failed = check(`    <RunicApplicationFrontendClientModule Include="NoSpecifier" />
    <RunicApplicationFrontendClientModule Include="Blank" Specifier="  " />
    <RunicApplicationFrontendClientModule Include="Relative" Specifier="../shared-ui/src/generated/runic-clients.js" />
    <RunicApplicationFrontendClientModule Include="Twice" Specifier="@acme/twice/generated" />
    <RunicApplicationFrontendClientModule Include="Twice" Specifier="@acme/other/generated" />
    <RunicApplicationFrontendClientModule Include="SharedUi" Specifier="@acme/shared-ui/generated" />`);
  assert.notEqual(failed.status, 0);
  const file = join(project, "App.proj");
  assert.deepEqual(failed.errors, [
    `${file} : error RUNICBRIDGE082: The RunicApplicationFrontendClientModule NoSpecifier has no Specifier. Set it to the module specifier of the library's generated runic-clients.ts, as the frontend imports it, such as Specifier="@acme/shared-ui/generated".`,
    `${file} : error RUNICBRIDGE082: The RunicApplicationFrontendClientModule Blank has no Specifier. Set it to the module specifier of the library's generated runic-clients.ts, as the frontend imports it, such as Specifier="@acme/shared-ui/generated".`,
    `${file} : error RUNICBRIDGE082: The RunicApplicationFrontendClientModule Relative has the relative Specifier '../shared-ui/src/generated/runic-clients.js', which every generated module would resolve from its own folder. Use a package specifier, such as @acme/shared-ui/generated, or a tsconfig paths alias.`,
    `${file} : error RUNICBRIDGE082: The assembly Twice has more than one RunicApplicationFrontendClientModule. Keep one item per assembly.`,
  ]);
  assert.deepEqual(check(`    <RunicApplicationFrontendClientModule Include="SharedUi" Specifier="@acme/shared-ui/generated" />`),
    { status: 0, errors: [] });
});

// The generator rewrites runic-client-modules.ts when a library's contracts change (a cache miss through the library's
// assembly), and the generated directory is a frontend input, so the frontend type-checks against them again.
test("a changed runic-client-modules.ts rebuilds the frontend", () => {
  const app = fixture("client-module-check", { name: "app", private: true });
  writeFileSync(join(app.frontend, "emits-dist"), "");
  mkdirSync(join(app.frontend, "generated"), { recursive: true });
  const check = join(app.frontend, "generated/runic-client-modules.ts");
  writeFileSync(check, "// <auto-generated />\nexport type RunicClientModuleCheck = [\"SharedUi.AboutDialogViewModel:1\"];\n");
  age(check, 3600);
  assert.equal(builds(build(app)), 1);
  assert.equal(builds(build(app)), 0);
  writeFileSync(check, "// <auto-generated />\nexport type RunicClientModuleCheck = [\"SharedUi.AboutDialogViewModel:2\"];\n");
  const result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.equal(builds(result), 1, "A changed library contract does not rebuild the frontend.");
});

// devtools-library-dx S2 (M2–M5, M7, D081.6): library packages and plain workspace packages are built, incrementally.
// A node first on PATH that logs each run, so that a test sees every JavaScript process a build starts.
function nodeSpy(log) {
  const bin = join(directory, "node-spy");
  mkdirSync(bin, { recursive: true });
  writeFileSync(join(bin, "node"), `#!/bin/sh\necho "node $*" >> "$RUNIC_TEST_NODE_LOG"\nexec "${process.execPath}" "$@"\n`, { mode: 0o755 });
  return { PATH: `${bin}:${process.env.PATH}`, RUNIC_TEST_NODE_LOG: log };
}

const evaluateProperty = (project, name, extra = []) => {
  const result = spawnSync("dotnet", ["msbuild", project, "-nologo", "-noAutoResponse", "-nodeReuse:false", ...extra, `-getProperty:${name}`], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  return result.stdout.trim();
};

test("a client library builds its package when it has a build script, copies its contracts and checks its exports", { skip: process.platform === "win32" }, () => {
  const library = fixture("library-package", { name: "@acme/shared-ui", private: true, scripts: { build: "node build.mjs" },
    exports: { ".": "./dist/index.js", "./generated": { types: "./dist/generated/runic-clients.d.ts", default: "./dist/generated/runic-clients.js" } } });
  writeFileSync(join(library.frontend, "emits-package"), "");
  mkdirSync(join(library.frontend, "generated"), { recursive: true });
  for (const file of ["about.contract.d.ts", "about.contract.d.ts.map", "runic-clients.ts"]) writeFileSync(join(library.frontend, "generated", file), "export {};\n");
  const properties = ["RunicApplicationFrontendClientLibrary=true"];
  const project = join(library.project, "App.proj");
  assert.equal(evaluateProperty(project, "RunicApplicationFrontendBuildEnabled", ["-p:RunicApplicationFrontendClientLibrary=true"]), "true", "auto: a build script builds.");
  assert.equal(evaluateProperty(project, "RunicApplicationFrontendBuildEnabled", ["-p:RunicApplicationFrontendClientLibrary=true", "-p:RunicApplicationFrontendBuildEnabled=false"]), "false");

  const log = join(library.project, "node.log");
  const env = nodeSpy(log);
  let result = build(library, { properties, env });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["install", "build"]);
  assert.deepEqual(readdirSync(join(library.frontend, "dist/generated")).sort(),
    ["about.contract.d.ts", "about.contract.d.ts.map", "runic-clients.d.ts", "runic-clients.js"], "tsc's output gets the contract declarations (F5).");
  rmSync(log, { force: true });
  result = build(library, { properties, env });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, []);
  assert.ok(!existsSync(log), `A no-change build starts no JavaScript process: ${existsSync(log) ? readFileSync(log, "utf8") : ""}`);

  // A changed contract rebuilds the package, which deletes dist/, and is copied again.
  writeFileSync(join(library.frontend, "generated/about.contract.d.ts"), "export type About = {};\n");
  result = build(library, { properties, env });
  assert.deepEqual(result.commands, ["build"]);
  assert.equal(readFileSync(join(library.frontend, "dist/generated/about.contract.d.ts"), "utf8"), "export type About = {};\n");

  // An export that the build does not write names the project and the package (F3).
  writeFileSync(join(library.frontend, "skips-index"), "");
  result = build(library, { properties, env });
  assert.notEqual(result.status, 0, result.output);
  const reported = errors(result.output);
  assert.equal(reported.length, 1, result.output);
  assert.match(reported[0], /error RUNICBRIDGE088: App's frontend package @acme\/shared-ui exports .*library-package\/frontend\/dist\/index\.js, which its build 'node build\.mjs' did not write, so applications that import it fail to resolve it/);

  result = build(library, { properties: [...properties, "RunicApplicationFrontendBuildEnabled=false"], env });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, [], "RunicApplicationFrontendBuildEnabled=false turns the package build off.");
});

test("a workspace package that no .NET project owns is built before the frontend, once, and again when it changes (D081.6)", () => {
  const { root, app } = workspace("ws-plain", { workspaces: ["app/frontend", "lib"], packageManager: "bun@1.4.4" }, { "bun.lock": "{}" });
  const lib = join(root, "lib");
  writeFileSync(join(lib, "package.json"), JSON.stringify({ name: "@acme/lib", private: true, scripts: { build: "node build-lib.mjs" }, exports: { ".": "./dist/index.js" } }));
  mkdirSync(join(lib, "src"), { recursive: true });
  writeFileSync(join(lib, "src/index.ts"), "export const answer = 42;\n");
  writeFileSync(join(lib, "build-lib.mjs"), `import { existsSync, mkdirSync, writeFileSync } from "node:fs";
if (!existsSync("no-output")) { mkdirSync("dist", { recursive: true }); writeFileSync("dist/index.js", "export {};"); }
`);
  const env = { ...packageManagers(), RUNIC_TEST_COMMANDS: join(app.project, "commands.log") };
  const install = `bun install --frozen-lockfile --ignore-scripts in ${realRoot(root)}`;
  const libraryBuild = `bun run --bun build in ${realRoot(lib)}`;
  let result = build(app, { env });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, [install, libraryBuild, "build"]);
  assert.match(result.output, /Building the frontend package @acme\/lib in .*ws-plain\/lib, which .*app\/frontend depends on and no \.NET project builds\./);
  const graph = JSON.parse(readFileSync(join(app.project, "obj/runic/frontend-graph.json"), "utf8"));
  assert.deepEqual(graph.packages.map(entry => [entry.package, entry.kind, entry.build, entry.exports, entry.outputs]),
    [["@acme/lib", "compiled", "bun run --bun build", ["./dist/index.js"], ["./dist"]]]);
  assert.deepEqual(readFileSync(join(app.project, "obj/runic/frontend-dependencies.txt"), "utf8").trim().split("\n"), [realRoot(lib)]);

  result = build(app, { env });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, [], "Nothing changed: neither the package nor the page builds.");
  // A source edit rebuilds the package and the page that bundles it; its output alone does not count as an edit.
  writeFileSync(join(lib, "src/index.ts"), "export const answer = 43;\n");
  assert.deepEqual(build(app, { env }).commands, [libraryBuild, "build"]);
  assert.deepEqual(build(app, { env }).commands, []);
  rmSync(join(lib, "dist"), { recursive: true });
  assert.deepEqual(build(app, { env }).commands, [libraryBuild, "build"], "A deleted export builds the package again.");

  writeFileSync(join(lib, "no-output"), "");
  rmSync(join(lib, "dist"), { recursive: true });
  result = build(app, { env });
  assert.notEqual(result.status, 0, result.output);
  assert.deepEqual(result.commands, [libraryBuild]);
  const reported = errors(result.output);
  assert.equal(reported.length, 1, result.output);
  assert.match(reported[0], /error RUNICBRIDGE088: .*ws-plain\/app\/frontend depends on @acme\/lib \(.*ws-plain\/lib\), a workspace package that no \.NET project owns, but its package\.json exports .*lib\/dist\/index\.js, which do not exist: its build 'bun run --bun build' did not write them/);
});

test("RunicViewsGetFrontendDependencies returns the packages of the referenced libraries and the workspace; a library builds its own", () => {
  const { root, app } = workspace("ws-owned", { workspaces: ["app/frontend", "lib", "tokens"], packageManager: "bun@1.4.4" }, { "bun.lock": "{}" },
    { appPackageJson: { name: "@acme/app", private: true, dependencies: { "@acme/lib": "workspace:*", "@acme/tokens": "workspace:*" } } });
  const lib = join(root, "lib");
  writeFileSync(join(lib, "package.json"), JSON.stringify({ name: "@acme/lib", private: true, scripts: { build: "tsc" }, exports: { ".": "./dist/index.js", "./generated": "./dist/generated/runic-clients.js" } }));
  mkdirSync(join(root, "tokens"), { recursive: true });
  writeFileSync(join(root, "tokens/package.json"), JSON.stringify({ name: "@acme/tokens", private: true }));
  mkdirSync(join(root, "SharedUi"), { recursive: true });
  // A client library whose package build is off: the application names it instead of failing the bundler's import.
  writeFileSync(join(root, "SharedUi/SharedUi.proj"), `<Project>
  <PropertyGroup>
    <AssemblyName>SharedUi</AssemblyName>
    <RunicApplicationFrontendClientLibrary>true</RunicApplicationFrontendClientLibrary>
    <RunicApplicationFrontendGenerationEnabled>true</RunicApplicationFrontendGenerationEnabled>
    <RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/../lib</RunicApplicationFrontendDirectory>
    <RunicApplicationFrontendBuildEnabled>false</RunicApplicationFrontendBuildEnabled>
  </PropertyGroup>
  <Import Project="${targets}" />
</Project>
`);
  const appProject = join(app.project, "App.proj");
  writeFileSync(appProject, readFileSync(appProject, "utf8").replace("  <Import Project=", `  <PropertyGroup><UsingMicrosoftNETSdk>true</UsingMicrosoftNETSdk></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../SharedUi/SharedUi.proj" />
    <RunicApplicationFrontendClientModule Include="SharedUi" Specifier="@acme/lib/generated" />
  </ItemGroup>
  <Target Name="AssignProjectConfiguration" />
  <Target Name="_GetProjectReferenceTargetFrameworkProperties" />
  <Target Name="_SplitProjectReferencesByFileExistence">
    <ItemGroup><_MSBuildProjectReferenceExistent Include="@(ProjectReference)" Condition="'%(Extension)' == '.proj'" /></ItemGroup>
  </Target>
  <Import Project=`));
  const env = { ...process.env, ...packageManagers(), RUNIC_TEST_COMMANDS: join(app.project, "commands.log") };
  const query = spawnSync("dotnet", ["msbuild", appProject, "-nologo", "-noAutoResponse", "-nodeReuse:false", "-getTargetResult:RunicViewsGetFrontendDependencies"],
    { cwd: app.project, encoding: "utf8", env });
  assert.equal(query.status, 0, query.stdout + query.stderr);
  const items = JSON.parse(query.stdout).TargetResults.RunicViewsGetFrontendDependencies.Items;
  assert.deepEqual(items.map(item => [item.PackageName, item.Kind, item.OwnerName ?? "", item.BuildEnabled ?? "", item.Specifier ?? ""]), [
    ["@acme/lib", "compiled", "SharedUi", "false", "@acme/lib/generated"],
    ["@acme/tokens", "source", "", "", ""],
    ["@acme/app", "application", "App", "", ""],
  ]);
  assert.ok(!existsSync(join(app.project, "commands.log")), "The query installs and builds nothing.");

  let result = build(app, { env });
  assert.notEqual(result.status, 0, result.output);
  assert.deepEqual(result.commands, [`bun install --frozen-lockfile --ignore-scripts in ${realRoot(root)}`], "The application does not build a package that a library owns.");
  let reported = errors(result.output);
  assert.equal(reported.length, 1, result.output);
  assert.match(reported[0], /error RUNICBRIDGE088: .*app\/frontend depends on @acme\/lib \(.*ws-owned\/lib\), the frontend package of SharedUi, but its package\.json exports .*lib\/dist\/index\.js, .*lib\/dist\/generated\/runic-clients\.js, which do not exist: its package build is off in SharedUi \(RunicApplicationFrontendBuildEnabled=false\)/);

  // M7: a bundler error that names a package of the graph says what that package is.
  mkdirSync(join(lib, "dist/generated"), { recursive: true });
  writeFileSync(join(lib, "dist/index.js"), "export {};");
  writeFileSync(join(lib, "dist/generated/runic-clients.js"), "export {};");
  writeFileSync(join(app.frontend, "unresolved-import"), "@acme/tokens");
  result = build(app, { env });
  assert.notEqual(result.status, 0, result.output);
  reported = errors(result.output);
  assert.equal(reported.length, 1, result.output);
  assert.match(reported[0], /error RUNICBRIDGE016: The frontend build 'node build\.mjs' in .* exited with code 1\. Its output is above\. @acme\/tokens \(.*ws-owned\/tokens\) is a workspace package that no \.NET project owns; check that it is built and that this frontend installs it\./);

  // M5: Visual Studio's up-to-date check gets the dependency packages that the last build recorded, without node_modules.
  mkdirSync(join(lib, "node_modules/x"), { recursive: true });
  writeFileSync(join(lib, "node_modules/x/index.js"), "");
  const evaluated = spawnSync("dotnet", ["msbuild", appProject, "-nologo", "-noAutoResponse", "-nodeReuse:false", "-p:BuildingInsideVisualStudio=true", "-getItem:UpToDateCheckInput"],
    { cwd: app.project, encoding: "utf8", env });
  assert.equal(evaluated.status, 0, evaluated.stdout + evaluated.stderr);
  const inputs = JSON.parse(evaluated.stdout).Items.UpToDateCheckInput.map(item => item.FullPath);
  assert.ok(inputs.some(input => input.endsWith("ws-owned/lib/dist/index.js")) && inputs.some(input => input.endsWith("ws-owned/tokens/package.json")), inputs.join("\n"));
  assert.ok(!inputs.some(input => input.includes("node_modules")), inputs.join("\n"));
});

// devtools-library-dx §3.4 (S3): dotnet runic dev names the project it serves; only that project skips its frontend
// build and copy, so a library that the page imports still builds its package (F6).
test("RunicApplicationFrontendDevSessionProject skips only the served project's frontend build", () => {
  const app = fixture("dev-served-app", { name: "app", private: true, devDependencies: { vite: "8.3.2" } });
  writeFileSync(join(app.frontend, "emits-dist"), "");
  const library = fixture("dev-served-library", { name: "@acme/shared-ui", private: true, scripts: { build: "node build.mjs" }, exports: { ".": "./dist/index.html" } });
  writeFileSync(join(library.frontend, "emits-dist"), "");
  const served = `RunicApplicationFrontendDevSessionProject=${join(app.project, "App.proj")}`;
  const output = join(app.project, "out") + "/";
  assert.equal(evaluateProperty(join(app.project, "App.proj"), "_RunicViewsDevSessionProtocol"), "1");
  assert.equal(evaluateProperty(join(app.project, "App.proj"), "RunicApplicationFrontendBuildEnabled", [`-p:${served}`]), "false");
  assert.equal(evaluateProperty(join(app.project, "App.proj"), "RunicApplicationFrontendCopyEnabled", [`-p:${served}`]), "false");

  let result = build(app, { targets: "RunicViewsCollectFrontendInputs;RunicViewsCopyFrontend", properties: [served, `TargetDir=${output}`] });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["install"], "The served project installs its packages but neither builds nor copies its frontend.");
  assert.ok(!existsSync(join(output, "www")));
  result = build(library, { properties: [served, "RunicApplicationFrontendClientLibrary=true"] });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(result.commands, ["install", "build"], "Another project, such as a client library, builds its package.");
  // What older dev passed to every project: the library's package stayed unbuilt (F6).
  rmSync(join(library.frontend, "dist"), { recursive: true });
  result = build(library, { properties: ["RunicApplicationFrontendBuildEnabled=false", "RunicApplicationFrontendInstallEnabled=false", "RunicApplicationFrontendClientLibrary=true"] });
  assert.deepEqual(result.commands, []);
});

// R7 G6: a source-tree build of a Views consumer has the TypeScript emitter without building the npm package first. The
// generator's build bundles it beside the generator (codegen/index.js), and the source-tree targets run that copy for a
// project without a frontend, as CI's consumer job builds the DynamicData fixture.
test("the generator's build bundles the TypeScript emitter that the source-tree targets run without a frontend", async () => {
  const dotnet = spawnSync("sh", ["-c", "command -v dotnet"], { encoding: "utf8" }).stdout.trim();
  const build = spawnSync(dotnet, ["build", resolve(root, "tools/Runic.Application.Views.Codegen"), "-c", configuration,
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: root, encoding: "utf8" });
  assert.equal(build.status, 0, build.stdout + build.stderr);
  const bundle = resolve(root, `tools/Runic.Application.Views.Codegen/bin/${configuration}/net10.0/codegen/index.js`);
  assert.ok(existsSync(bundle), `${bundle} is missing after the generator's build`);

  const project = join(directory, "bundled-emitter");
  mkdirSync(project, { recursive: true });
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup><Configuration>${configuration}</Configuration></PropertyGroup>
  <Import Project="${targets}" />
</Project>
`);
  const evaluated = spawnSync(dotnet, ["msbuild", join(project, "App.proj"), "-getProperty:_RunicViewsBundledEmitter", "-nologo", "-noAutoResponse"],
    { cwd: project, encoding: "utf8" });
  assert.equal(evaluated.status, 0, evaluated.stdout + evaluated.stderr);
  assert.equal(evaluated.stdout.trim(), bundle);

  const { generate } = await import(new URL(`file://${bundle}`).href);
  const files = generate({ format: "runic.views.contract-ir/1", producer: { language: "csharp", tool: "test", unit: "App" },
    views: [{ id: "App.CounterViewModel", name: "Counter", route: "counter", fingerprint: "F1", kinds: ["counter"], members: [], commands: [] }],
    types: {}, sources: {} }, { outputDirectory: "/generated" });
  assert.deepEqual([...files.keys()].sort(), ["counter.contract.d.ts", "counter.mock.ts", "counter.ts"]);
});

// R7 G6 (D080.5, refined): which emitter runic-views-codegen.mjs, the launcher RunicViewsGenerateClients runs, takes. The
// frontend's installed @runic-artifex/views; else the bundled copy, when the frontend declares none (or there is none, or
// it is not built); and RUNICBRIDGE084 when the frontend declares it without having it installed.
test("the TypeScript emitter is the frontend's installed one, else the bundled copy unless the frontend declares one", () => {
  const launcher = resolve(root, "packages/dotnet/Runic.Application.Views/build/runic-views-codegen.mjs");
  const base = join(directory, "emitter-selection");
  const emitter = (path, name) => {
    mkdirSync(path, { recursive: true });
    writeFileSync(join(path, "index.js"), `export function generate() { return new Map([["counter.ts", "// <auto-generated />\\n// ${name}\\n"]]); }\n`);
    return join(path, "index.js");
  };
  const bundled = emitter(join(base, "bundled"), "bundled");
  const ir = join(base, "contract.ir.json");
  writeFileSync(ir, JSON.stringify({ format: "runic.views.contract-ir/1", producer: { language: "csharp", tool: "test", unit: "App" },
    views: [], types: {}, sources: {} }));
  const run = (name, manifest, { installed = false, unbuilt = false } = {}) => {
    const project = join(base, name);
    const frontend = join(project, "Frontend");
    mkdirSync(frontend, { recursive: true });
    writeFileSync(join(frontend, "package.json"), JSON.stringify(manifest));
    if (installed) {
      const views = join(frontend, "node_modules", "@runic-artifex", "views");
      emitter(join(views, "dist", "codegen"), "frontend");
      writeFileSync(join(views, "package.json"), JSON.stringify({ name: "@runic-artifex/views", version: "0.24.0",
        exports: { "./codegen": { import: "./dist/codegen/index.js" } } }));
    }
    const out = join(frontend, "src", "generated");
    const result = spawnSync(process.execPath, [launcher, "--ir", ir, "--out", out, "--outputs", join(project, "outputs"),
      "--used-emitter", join(project, "emitter"), "--frontend", frontend, "--bundled", bundled, ...(unbuilt ? ["--frontend-unbuilt"] : [])],
      { encoding: "utf8" });
    const used = existsSync(join(project, "emitter")) ? readFileSync(join(project, "emitter"), "utf8").trim() : null;
    return { result, used, client: existsSync(join(out, "counter.ts")) ? readFileSync(join(out, "counter.ts"), "utf8") : null };
  };
  // 1. Installed: the frontend's, also when the package.json does not declare it (a workspace's hoisted install).
  for (const [name, manifest] of [["installed", { dependencies: { "@runic-artifex/views": "0.24.0" } }], ["hoisted", {}]]) {
    const { result, client } = run(name, manifest, { installed: true });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.equal(client, "// <auto-generated />\n// frontend\n", name);
  }
  // 2. Not declared (no dependencies, as the WPF bridge consumer's page), or not built: the bundled copy.
  for (const [name, manifest, unbuilt] of [["undeclared", { scripts: { build: "node build.mjs" } }, false],
    ["unbuilt", { devDependencies: { "@runic-artifex/views": "0.24.0" } }, true]]) {
    const { result, used, client } = run(name, manifest, { unbuilt });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.equal(used, bundled, name);
    assert.equal(client, "// <auto-generated />\n// bundled\n", name);
  }
  // 3. Declared in any dependency field but not installed: RUNICBRIDGE084, and no client.
  for (const field of ["dependencies", "devDependencies", "optionalDependencies", "peerDependencies"]) {
    const { result, client } = run(`missing-${field}`, { [field]: { "@runic-artifex/views": "0.24.0" } });
    assert.equal(result.status, 1, field);
    assert.match(result.stderr, /error RUNICBRIDGE084: the frontend .* declares @runic-artifex\/views, but it is not installed/);
    assert.equal(client, null);
  }
});

// R7 G6: a package's launcher reached through a symbolic link, as a Nix build restores packages from the store, runs: a
// main-script check by path alone saw the link and the resolved path differ, did nothing and exited 0.
test("the TypeScript emitter's launcher runs through a symbolic link", async () => {
  const { symlinkSync } = await import("node:fs");
  const launcher = resolve(root, "packages/dotnet/Runic.Application.Views/build/runic-views-codegen.mjs");
  const linked = join(directory, "linked-launcher");
  mkdirSync(linked, { recursive: true });
  symlinkSync(launcher, join(linked, "runic-views-codegen.mjs"));
  const result = spawnSync(process.execPath, [join(linked, "runic-views-codegen.mjs")], { encoding: "utf8" });
  assert.equal(result.status, 1, result.stdout + result.stderr);
  assert.match(result.stderr, /error RUNICBRIDGE001: --ir is required\./);
});
