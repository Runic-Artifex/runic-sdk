import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, utimesSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { root } from "../../eng/run.mjs";

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

function fixture(name, packageJson) {
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
import { appendFileSync, existsSync } from "node:fs";
appendFileSync("../commands.log", "build\\n");
if (existsSync("build-fails")) { console.error("error during build:"); console.error("[vite]: Rollup failed to resolve import"); process.exit(1); }
if (existsSync("build-reports")) { console.log("src/App.ts(3,7): error TS2322: Type 'string' is not assignable to type 'number'."); process.exit(2); }
if (existsSync("build-warns")) console.log("src/App.ts(1,1): warning TS6133: 'unused' is declared but never read.");
`);
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <RunicApplicationFrontendGenerationEnabled>true</RunicApplicationFrontendGenerationEnabled>
    <RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/frontend</RunicApplicationFrontendDirectory>
    <RunicApplicationFrontendGeneratedDirectory>$(RunicApplicationFrontendDirectory)/generated</RunicApplicationFrontendGeneratedDirectory>
    <RunicApplicationFrontendInstallCommand>node install.mjs</RunicApplicationFrontendInstallCommand>
    <RunicApplicationFrontendBuildCommand>node build.mjs</RunicApplicationFrontendBuildCommand>
    <Configuration>Debug</Configuration>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <Import Project="${targets}" />
</Project>
`);
  return { project, frontend };
}

function build({ project, frontend }) {
  rmSync(join(project, "commands.log"), { force: true });
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsCollectFrontendInputs",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  let commands = [];
  try {
    commands = readFileSync(join(project, "commands.log"), "utf8").trim().split("\n").filter(Boolean);
  } catch {}
  return { status: result.status, output: `${result.stdout}${result.stderr}`, commands };
}

// At minimal verbosity MSBuild indents the command's own output lines, not the errors it logs.
const errors = output => [...new Set(output.split("\n").filter(line => /^\S.* error [A-Z]+\d+:/.test(line))
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

test("an old property name still applies and reports RUNICBRIDGE017 once", () => {
  const project = join(directory, "renamed");
  mkdirSync(project, { recursive: true });
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <RunicBridgeFrontendDir>$(MSBuildProjectDirectory)/client</RunicBridgeFrontendDir>
    <RunicBridgeBuildFrontend>false</RunicBridgeBuildFrontend>
    <RunicApplicationFrontendBuildEnabled>true</RunicApplicationFrontendBuildEnabled>
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
  assert.equal(properties.RunicApplicationFrontendDirectory, join(project, "client"));
  assert.equal(properties.RunicApplicationFrontendBuildEnabled, "false", "The old name wins over the new one.");
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicViewsReportRenamedProperties",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  const output = `${result.stdout}${result.stderr}`;
  assert.equal(result.status, 0, output);
  const warnings = [...new Set(output.split("\n").filter(line => / warning RUNICBRIDGE017:/.test(line)).map(line => line.trim()))];
  assert.equal(warnings.length, 2, output);
  assert.ok(warnings.some(line => /RunicBridgeFrontendDir is renamed to RunicApplicationFrontendDirectory/.test(line)), output);
  assert.ok(warnings.some(line => /RunicBridgeBuildFrontend is renamed to RunicApplicationFrontendBuildEnabled/.test(line)), output);
});
