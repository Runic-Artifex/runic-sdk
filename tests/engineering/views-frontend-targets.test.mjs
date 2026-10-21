import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, readlinkSync, rmSync, utimesSync, writeFileSync } from "node:fs";
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

function fixture(name, packageJson, projectItems = "") {
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
    <RunicApplicationFrontendInstallCommand>node install.mjs</RunicApplicationFrontendInstallCommand>
    <RunicApplicationFrontendBuildCommand>node build.mjs</RunicApplicationFrontendBuildCommand>
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

// Review of #256: an application that loads a host's own client chooses its host at run time.
test("several hosts without RunicApplicationHost are silent when the page loads a host's own client", () => {
  for (const script of ["runic-cswebui.js", "runic-desktop-views.js"]) {
    const project = hostProject(`runtime-host-${script}`, ["Desktop", "CsWebUi"], script);
    const result = copyHost(project);
    assert.equal(result.status, 0, result.output);
    assert.deepEqual(result.warnings, [], result.output);
    assert.ok(existsSync(join(project, `bin/www/${script}`)));
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
  assert.equal(defaultComposition("Keywords", "Contoso.class.9lives.Classic"), "Contoso._class._9lives.Classic.RunicViewsComposition");
  assert.equal(defaultComposition("Spaces", "My App (Beta)"), "My_App__Beta_.RunicViewsComposition");
  assert.equal(defaultComposition("Library", "Contoso.Library", false), "", "A project without a host adapter gets no composition.");
});
