import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, readlinkSync, rmSync, statSync, utimesSync, writeFileSync } from "node:fs";
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
import { appendFileSync, mkdirSync } from "node:fs";
// The build asks Bun for its version (RUNICBRIDGE033).
if (process.argv[2] === "--version") { console.log(process.env.RUNIC_TEST_VERSION ?? "1.4.4"); process.exit(0); }
appendFileSync("../commands.log", ["${name}", ...process.argv.slice(2)].join(" ") + "\\n");
mkdirSync("node_modules", { recursive: true });
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
