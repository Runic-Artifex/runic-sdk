import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";
import { nugetPackArguments, root } from "./run.mjs";
import { bumpVersionFiles, checkVersions, packageVersion, readVersionFiles } from "./version.mjs";

const files = () => ({
  "eng/workspace.json": '{\n  "schemaVersion": 2,\n  "version": "0.6.0-preview.1",\n  "npm": [\n    { "name": "@runic-artifex/views", "path": "packages/web/views" },\n    { "name": "@runic-artifex/react", "path": "packages/web/react" }\n  ]\n}\n',
  "eng/Versions.props": "<Project>\n  <PropertyGroup>\n    <RunicSdkVersion>0.6.0-preview.1</RunicSdkVersion>\n    <RunicPackageValidationBaselineVersion>0.6.0-preview.1</RunicPackageValidationBaselineVersion>\n  </PropertyGroup>\n</Project>\n",
  "bun.lock": '{\n  "lockfileVersion": 1,\n  "workspaces": {\n    "packages/web/views": {\n      "name": "@runic-artifex/views",\n      "version": "0.6.0-preview.1",\n    },\n    "packages/web/react": {\n      "name": "@runic-artifex/react",\n      "version": "0.6.0-preview.1",\n    },\n  },\n}\n',
  "packages/web/views/package.json": '{\n  "name": "@runic-artifex/views",\n  "version": "0.6.0-preview.1",\n  "devDependencies": { "typescript": "6.0.3" }\n}\n',
  "packages/web/react/package.json": '{\n  "name": "@runic-artifex/react",\n  "version": "0.6.0-preview.1",\n  "peerDependencies": { "@runic-artifex/views": "0.6.0-preview.1", "react": "0.6.0-preview.1" }\n}\n',
  "examples/app/packages.lock.json": JSON.stringify({ version: 2, dependencies: { "net10.0": {
    "Runic.External": { type: "Direct", requested: "[0.6.0-preview.1, )", resolved: "0.6.0-preview.1" },
    "runic.desktop": { type: "Project", dependencies: { "Runic.External": "[0.6.0-preview.1, )", "Runic.Platform": "[0.6.0-preview.1, )" } },
    "runic.platform": { type: "Project" },
  } } }, null, 2) + "\n",
  "tests/fixture/packages.Fixture.Tests.lock.json": JSON.stringify({ version: 2, dependencies: { "net10.0": {
    "runic.navigation": { type: "Project", dependencies: { "Runic.Platform": "[0.6.0-preview.1, )" } },
    "runic.platform": { type: "Project" },
  } } }, null, 2) + "\n",
});

test("the committed release-train version copies agree with eng/workspace.json", () => {
  assert.equal(checkVersions(readVersionFiles()), JSON.parse(readFileSync(resolve(root, "eng/workspace.json"), "utf8")).version);
});

test("a mismatched version copy fails", () => {
  for (const [path, from, to] of [
    ["eng/Versions.props", "<RunicSdkVersion>0.6.0-preview.1", "<RunicSdkVersion>0.6.0-preview.2"],
    ["packages/web/views/package.json", '"version": "0.6.0-preview.1"', '"version": "0.6.0"'],
    ["packages/web/react/package.json", '"@runic-artifex/views": "0.6.0-preview.1"', '"@runic-artifex/views": "^0.6.0-preview.1"'],
    ["eng/Versions.props", "<RunicSdkVersion>0.6.0-preview.1</RunicSdkVersion>", ""],
    ["bun.lock", '"name": "@runic-artifex/react",\n      "version": "0.6.0-preview.1"', '"name": "@runic-artifex/react",\n      "version": "0.5.0"'],
    ["examples/app/packages.lock.json", '"Runic.Platform": "[0.6.0-preview.1, )"', '"Runic.Platform": "[0.5.0, )"'],
    ["tests/fixture/packages.Fixture.Tests.lock.json", '"Runic.Platform": "[0.6.0-preview.1, )"', '"Runic.Platform": "[0.5.0, )"'],
  ]) {
    const value = files();
    value[path] = value[path].replace(from, to);
    assert.throws(() => checkVersions(value), undefined, `${path}: ${to}`);
  }
});

test("version bump rewrites every copy and keeps the validation baseline", () => {
  const next = bumpVersionFiles(files(), "0.7.0-preview.1");
  assert.equal(checkVersions(next), "0.7.0-preview.1");
  assert.match(next["eng/Versions.props"], /<RunicPackageValidationBaselineVersion>0\.6\.0-preview\.1</);
  assert.match(next["packages/web/react/package.json"], /"react": "0\.6\.0-preview\.1"/, "external dependencies are untouched");
  assert.match(next["examples/app/packages.lock.json"], /"Runic\.Platform": "\[0\.7\.0-preview\.1, \)"/);
  assert.equal(next["examples/app/packages.lock.json"].match(/0\.6\.0-preview\.1/g)?.length, 3, "external NuGet packages are untouched");
  assert.match(next["tests/fixture/packages.Fixture.Tests.lock.json"], /"Runic\.Platform": "\[0\.7\.0-preview\.1, \)"/, "project-named lock files are rewritten");
  for (const invalid of [undefined, "0.7", "v0.7.0", "0.7.0-preview..1"])
    assert.throws(() => bumpVersionFiles(files(), invalid));
});

test("Runic.CommandLine has its own exact pin, independent of the SDK version", () => {
  const props = readFileSync(resolve(root, "Directory.Packages.props"), "utf8");
  const pins = ["Runic.CommandLine", "Runic.CommandLine.Spectre"].map(name =>
    props.match(new RegExp(`<PackageVersion Include="${name.replaceAll(".", "\\.")}" Version="([^"]+)" />`))?.[1]);
  for (const pin of pins) assert.match(pin ?? "", /^\d+\.\d+\.\d+(?:-[\w.]+)?$/, "Runic.CommandLine pins must be exact released versions");
  assert.equal(pins[0], pins[1], "Runic.CommandLine and Runic.CommandLine.Spectre are released together");
});

// Runic.Application.Views.CsWebUi may ship on a version of its own (eng/version.mjs separateVersions).
const cswebui = "Runic.Application.Views.CsWebUi";
const separateFiles = (cswebuiVersion, { workspaceEntry = cswebuiVersion } = {}) => {
  const value = files();
  const entry = workspaceEntry === null ? "" : `, "version": "${workspaceEntry}"`;
  value["eng/workspace.json"] = value["eng/workspace.json"].replace('  "npm": [',
    `  "nuget": [\n    { "name": "${cswebui}"${entry}, "project": "packages/dotnet/${cswebui}/${cswebui}.csproj" },\n    { "name": "Runic.Platform", "project": "packages/dotnet/Runic.Platform/Runic.Platform.csproj" }\n  ],\n  "npm": [`);
  value["eng/Versions.props"] = value["eng/Versions.props"].replace("  </PropertyGroup>",
    `    <RunicCsWebUiPackageVersion Condition="'$(RunicCsWebUiPackageVersion)' == ''">${cswebuiVersion}</RunicCsWebUiPackageVersion>\n  </PropertyGroup>`);
  value["examples/web/packages.lock.json"] = JSON.stringify({ version: 2, dependencies: { "net10.0": {
    "consumer.tests": { type: "Project", dependencies: { [cswebui]: `[${cswebuiVersion}, )`, "Runic.Platform": "[0.6.0-preview.1, )" } },
    "runic.application.views.cswebui": { type: "Project", dependencies: { "Runic.Platform": "[0.6.0-preview.1, )" } },
    "runic.platform": { type: "Project" },
  } } }, null, 2) + "\n";
  return value;
};
const cswebuiCopies = value => [
  /<RunicCsWebUiPackageVersion [^>]*>([^<]*)</.exec(value["eng/Versions.props"])[1],
  JSON.parse(value["eng/workspace.json"]).nuget[0].version,
  JSON.parse(value["examples/web/packages.lock.json"]).dependencies["net10.0"]["consumer.tests"].dependencies[cswebui],
];

test("packageVersion gives a package's own version, else the release train", () => {
  const workspace = JSON.parse(separateFiles("1.0.0-preview.1")["eng/workspace.json"]);
  assert.equal(packageVersion(workspace, cswebui), "1.0.0-preview.1");
  assert.equal(packageVersion(workspace, cswebui.toLowerCase()), "1.0.0-preview.1", "NuGet ids are case-insensitive");
  assert.equal(packageVersion(workspace, "Runic.Platform"), "0.6.0-preview.1");
  assert.equal(packageVersion(workspace, "Runic.Unlisted"), "0.6.0-preview.1");
  assert.equal(packageVersion(JSON.parse(separateFiles("0.6.0-preview.1", { workspaceEntry: null })["eng/workspace.json"]), cswebui), "0.6.0-preview.1");
});

test("check compares the CsWebUi copies with its own version", () => {
  assert.equal(checkVersions(separateFiles("1.0.0-preview.1")), "0.6.0-preview.1");
  assert.equal(checkVersions(separateFiles("0.6.0-preview.1", { workspaceEntry: null })), "0.6.0-preview.1");
  for (const [path, from, to] of [
    ["eng/Versions.props", ">1.0.0-preview.1</RunicCsWebUiPackageVersion>", ">0.6.0-preview.1</RunicCsWebUiPackageVersion>"],
    ["eng/Versions.props", ">1.0.0-preview.1</RunicCsWebUiPackageVersion>", ">$(RunicSdkVersion)</RunicCsWebUiPackageVersion>"],
    ["examples/web/packages.lock.json", `"${cswebui}": "[1.0.0-preview.1, )"`, `"${cswebui}": "[0.6.0-preview.1, )"`],
    ["eng/workspace.json", '"version": "1.0.0-preview.1"', '"version": "1.0"'],
    ["eng/workspace.json", `"name": "${cswebui}", "version": "1.0.0-preview.1", "project": "packages/dotnet/${cswebui}/${cswebui}.csproj"`,
      `"name": "${cswebui}", "project": "packages/dotnet/${cswebui}/${cswebui}.csproj", "version": "1.0.0-preview.1"`],
    ["eng/workspace.json", '"name": "Runic.Platform",', '"name": "Runic.Platform", "version": "0.6.0-preview.1",'],
  ]) {
    const value = separateFiles("1.0.0-preview.1");
    assert.notEqual(value[path].replace(from, to), value[path], `${path}: ${from}`);
    value[path] = value[path].replace(from, to);
    assert.throws(() => checkVersions(value), undefined, `${path}: ${to}`);
  }
});

test("bump moves an equal CsWebUi version with the train, keeps a distinct one, and --cswebui sets it", () => {
  const equal = bumpVersionFiles(separateFiles("0.6.0-preview.1"), "0.7.0-preview.1");
  assert.deepEqual(cswebuiCopies(equal), ["0.7.0-preview.1", "0.7.0-preview.1", "[0.7.0-preview.1, )"]);
  const implicit = bumpVersionFiles(separateFiles("0.6.0-preview.1", { workspaceEntry: null }), "0.7.0-preview.1");
  assert.match(implicit["eng/Versions.props"], />0\.7\.0-preview\.1<\/RunicCsWebUiPackageVersion>/);
  const distinct = bumpVersionFiles(separateFiles("1.0.0-preview.1"), "1.0.0");
  assert.equal(checkVersions(distinct), "1.0.0");
  assert.deepEqual(cswebuiCopies(distinct), ["1.0.0-preview.1", "1.0.0-preview.1", "[1.0.0-preview.1, )"]);
  assert.match(distinct["examples/web/packages.lock.json"], /"Runic\.Platform": "\[1\.0\.0, \)"/);
  const set = bumpVersionFiles(separateFiles("0.6.0-preview.1"), "1.0.0", { [cswebui]: "1.0.0-preview.1" });
  assert.equal(checkVersions(set), "1.0.0");
  assert.deepEqual(cswebuiCopies(set), ["1.0.0-preview.1", "1.0.0-preview.1", "[1.0.0-preview.1, )"]);
  assert.match(set["eng/Versions.props"], /<RunicSdkVersion>1\.0\.0</);
  assert.throws(() => bumpVersionFiles(separateFiles("0.6.0-preview.1"), "1.0.0", { [cswebui]: "1.0" }));
  assert.throws(() => bumpVersionFiles(separateFiles("0.6.0-preview.1"), "1.0.0", { "Runic.Platform": "1.0.0-preview.1" }));
  assert.throws(() => bumpVersionFiles(separateFiles("0.6.0-preview.1", { workspaceEntry: null }), "1.0.0", { [cswebui]: "1.0.0-preview.1" }));
});

test("each NuGet package packs at its own version", () => {
  const workspace = JSON.parse(separateFiles("1.0.0-preview.1")["eng/workspace.json"]);
  const version = p => nugetPackArguments(workspace, p, { built: true, nuget: "/feed", npm: "/npm" }).filter(arg => arg.startsWith("-p:PackageVersion="));
  assert.deepEqual(version(workspace.nuget[0]), ["-p:PackageVersion=1.0.0-preview.1"]);
  assert.deepEqual(version(workspace.nuget[1]), ["-p:PackageVersion=0.6.0-preview.1"]);
  assert.deepEqual(nugetPackArguments(workspace, workspace.nuget[1], { built: false, nuget: "/feed", npm: "/npm", selectedConfiguration: "Release" }),
    ["pack", "packages/dotnet/Runic.Platform/Runic.Platform.csproj", "-c", "Release", "--no-restore", "-o", "/feed", "-p:PackageVersion=0.6.0-preview.1"]);
});
