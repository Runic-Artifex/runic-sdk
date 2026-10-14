import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, utimesSync, writeFileSync } from "node:fs";
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
  const env = { VITE_TITLE: "", APP_FLAVOR: "", NODE_ENV: "" };
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
  for (const [name, value] of [["VITE_TITLE", "Notes"], ["NODE_ENV", "development"], ["APP_FLAVOR", "store"]]) {
    env[name] = value;
    assert.equal(builds(build(app, { properties: host, env })), 1, `A changed ${name} builds the frontend again.`);
    assert.equal(builds(build(app, { properties: host, env })), 0, `An unchanged ${name} does not.`);
  }
  assert.equal(builds(build(app, { properties: host, env: { ...env, UNRELATED_SETTING: "1" } })), 0,
    "Other environment variables are not part of the build state.");
  assert.doesNotMatch(readFileSync(join(app.project, "obj/Debug/net10.0/runic-bridge-frontend.state"), "utf8"), /Notes|store/,
    "Only a hash of the environment is written.");
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

  // dotnet runic dev turns the copy off and writes its own document: nothing is deleted then.
  writeFileSync(join(app.frontend, "asset-name"), "index-3");
  result = build(app, { ...copy, properties: [...copy.properties, "RunicApplicationFrontendCopyEnabled=false"] });
  assert.equal(result.status, 0, result.output);
  assert.ok(existsSync(join(www, "assets/index-2.js")));
  result = build(app, copy);
  assert.deepEqual(files(www), ["assets/index-3.js", "assets/older.js", "data/user.json", "index.html", "runic-views.js"]);
});

// #237: a page that loads runic-views.js runs on every host; the referenced host supplies the client.
test("the referenced host copies its Views client as runic-views.js", () => {
  const hostTargets = name => resolve(root, `packages/dotnet/Runic.Application.Views.${name}/build/Runic.Application.Views.${name}.targets`);
  const client = name => readFileSync(resolve(root, `packages/dotnet/Runic.Application.Views.${name}/www/${{ Desktop: "runic-desktop-views.js", CsWebUi: "runic-cswebui.js" }[name]}`), "utf8");
  const hostProject = (name, hosts) => {
    const project = join(directory, name);
    mkdirSync(project, { recursive: true });
    writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>App</AssemblyName>
    <TargetDir>$(MSBuildProjectDirectory)/bin/</TargetDir>
    <Configuration>Debug</Configuration>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <Import Project="${targets}" />
${hosts.map(host => `  <Import Project="${hostTargets(host)}" />`).join("\n")}
</Project>
`);
    return { project, hosts };
  };
  const copy = ({ project, hosts }, host) => {
    rmSync(join(project, "bin"), { recursive: true, force: true });
    const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"),
      `-t:RunicViewsResolveHost;${hosts.map(name => `RunicViewsCopy${name}Client`).join(";")}`,
      ...(host ? [`-p:RunicApplicationHost=${host}`] : []),
      "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
    const output = `${result.stdout}${result.stderr}`;
    const views = join(project, "bin/www/runic-views.js");
    return { status: result.status, output, views: existsSync(views) ? readFileSync(views, "utf8") : undefined,
      warnings: [...new Set(output.split("\n").filter(line => / warning RUNICBRIDGE019:/.test(line)).map(line => line.trim()))] };
  };

  for (const host of ["Desktop", "CsWebUi"]) {
    const project = hostProject(`only-${host}`, [host]);
    const result = copy(project);
    assert.equal(result.status, 0, result.output);
    assert.equal(result.views, client(host), `The only host, ${host}, needs no RunicApplicationHost.`);
    assert.deepEqual(result.warnings, []);
  }

  const both = hostProject("both-hosts", ["Desktop", "CsWebUi"]);
  let result = copy(both);
  assert.equal(result.status, 0, result.output);
  assert.equal(result.views, undefined, "With two hosts, neither is chosen silently.");
  assert.equal(result.warnings.length, 1, result.output);
  assert.match(result.warnings[0], /references more than one Runic host \(desktop, cswebui\)/);

  for (const [host, name] of [["desktop", "Desktop"], ["cswebui", "CsWebUi"]]) {
    result = copy(both, host);
    assert.equal(result.status, 0, result.output);
    assert.equal(result.views, client(name), `RunicApplicationHost=${host} copies that host's client.`);
    assert.deepEqual(result.warnings, []);
  }

  result = copy(both, "wpf");
  assert.equal(result.views, undefined);
  assert.equal(result.warnings.length, 1, result.output);
  assert.match(result.warnings[0], /RunicApplicationHost is 'wpf', but App references no such Runic host/);
});
