import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";
import { nugetPackArguments, root } from "./run.mjs";
import { bumpVersionFiles, checkVersions, nextSeparateVersion, packageVersion, readVersionFiles } from "./version.mjs";

const files = () => ({
  "eng/workspace.json": '{\n  "schemaVersion": 2,\n  "version": "0.6.0-preview.1",\n  "npm": [\n    { "name": "@runic-artifex/views", "path": "packages/web/views" },\n    { "name": "@runic-artifex/react", "path": "packages/web/react" }\n  ]\n}\n',
  "eng/Versions.props": "<Project>\n  <PropertyGroup>\n    <RunicSdkVersion>0.6.0-preview.1</RunicSdkVersion>\n    <RunicPackageValidationBaselineVersion>0.5.0</RunicPackageValidationBaselineVersion>\n  </PropertyGroup>\n</Project>\n",
  "bun.lock": '{\n  "lockfileVersion": 1,\n  "workspaces": {\n    "packages/web/views": {\n      "name": "@runic-artifex/views",\n      "version": "0.6.0-preview.1",\n    },\n    "packages/web/react": {\n      "name": "@runic-artifex/react",\n      "version": "0.6.0-preview.1",\n    },\n  },\n}\n',
  "packages/web/views/package.json": '{\n  "name": "@runic-artifex/views",\n  "version": "0.6.0-preview.1",\n  "devDependencies": { "typescript": "6.0.3" }\n}\n',
  "packages/web/views/src/version.ts": '// The release this package belongs to.\nexport const viewsVersion = "0.6.0-preview.1";\n',
  "packages/web/react/package.json": '{\n  "name": "@runic-artifex/react",\n  "version": "0.6.0-preview.1",\n  "peerDependencies": { "@runic-artifex/views": "0.6.0-preview.1", "react": "0.6.0-preview.1" }\n}\n',
  "native/Cargo.toml": '[workspace]\nmembers = ["runic-server"]\n\n[workspace.package]\nversion = "0.6.0-preview.1"\nedition = "2024"\n',
  "native/Cargo.lock": 'version = 4\n\n[[package]]\nname = "axum"\nversion = "0.8.4"\n\n[[package]]\nname = "runic-server"\nversion = "0.6.0-preview.1"\n',
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
    ["packages/web/views/src/version.ts", 'viewsVersion = "0.6.0-preview.1"', 'viewsVersion = "0.6.0"'],
    ["packages/web/react/package.json", '"@runic-artifex/views": "0.6.0-preview.1"', '"@runic-artifex/views": "^0.6.0-preview.1"'],
    ["eng/Versions.props", "<RunicSdkVersion>0.6.0-preview.1</RunicSdkVersion>", ""],
    ["native/Cargo.toml", 'version = "0.6.0-preview.1"', 'version = "0.5.0"'],
    ["native/Cargo.lock", 'name = "runic-server"\nversion = "0.6.0-preview.1"', 'name = "runic-server"\nversion = "0.5.0"'],
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
  assert.match(next["eng/Versions.props"], /<RunicPackageValidationBaselineVersion>0\.5\.0</);
  assert.match(next["packages/web/views/src/version.ts"], /viewsVersion = "0\.7\.0-preview\.1"/);
  assert.match(next["native/Cargo.toml"], /^\[workspace\.package\]\nversion = "0\.7\.0-preview\.1"$/m);
  assert.match(next["native/Cargo.lock"], /name = "runic-server"\nversion = "0\.7\.0-preview\.1"\n/);
  assert.match(next["native/Cargo.lock"], /name = "axum"\nversion = "0\.8\.4"\n/, "crate dependencies are untouched");
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
  assert.equal(checkVersions(separateFiles("0.6.0-preview.2")), "0.6.0-preview.1");
  assert.equal(checkVersions(separateFiles("0.6.0-preview.1", { workspaceEntry: null })), "0.6.0-preview.1");
  for (const [path, from, to] of [
    ["eng/Versions.props", ">0.6.0-preview.2</RunicCsWebUiPackageVersion>", ">0.6.0-preview.1</RunicCsWebUiPackageVersion>"],
    ["eng/Versions.props", ">0.6.0-preview.2</RunicCsWebUiPackageVersion>", ">$(RunicSdkVersion)</RunicCsWebUiPackageVersion>"],
    ["examples/web/packages.lock.json", `"${cswebui}": "[0.6.0-preview.2, )"`, `"${cswebui}": "[0.6.0-preview.1, )"`],
    ["eng/workspace.json", '"version": "0.6.0-preview.2"', '"version": "1.0"'],
    ["eng/workspace.json", `"name": "${cswebui}", "version": "0.6.0-preview.2", "project": "packages/dotnet/${cswebui}/${cswebui}.csproj"`,
      `"name": "${cswebui}", "project": "packages/dotnet/${cswebui}/${cswebui}.csproj", "version": "0.6.0-preview.2"`],
    ["eng/workspace.json", '"name": "Runic.Platform",', '"name": "Runic.Platform", "version": "0.6.0-preview.1",'],
  ]) {
    const value = separateFiles("0.6.0-preview.2");
    assert.notEqual(value[path].replace(from, to), value[path], `${path}: ${from}`);
    value[path] = value[path].replace(from, to);
    assert.throws(() => checkVersions(value), undefined, `${path}: ${to}`);
  }
});

test("CsWebUi ships as <train major.minor.patch>-preview.N, also through 1.0.0-rc.N and 1.0.0 (D043.6)", () => {
  // 1.0.0-preview.N sorts below 1.0.0-rc.N, so CsWebUi keeps one prerelease label of its own.
  for (const invalid of ["0.7.0-preview.1", "0.6.0-rc.1", "0.6.0", "0.6.0-preview.0", "0.6.0-preview.1.1"])
    assert.throws(() => checkVersions(separateFiles(invalid)), /ships as 0\.6\.0-preview\.N/, invalid);
  assert.equal(nextSeparateVersion("0.11.0-preview.1", "0.11.0", "1.0.0-rc.1"), "1.0.0-preview.1");
  assert.equal(nextSeparateVersion("1.0.0-preview.1", "1.0.0-rc.1", "1.0.0-rc.2"), "1.0.0-preview.2");
  assert.equal(nextSeparateVersion("1.0.0-preview.2", "1.0.0-rc.2", "1.0.0"), "1.0.0-preview.3");
  assert.equal(nextSeparateVersion("1.0.0-preview.3", "1.0.0", "1.0.1"), "1.0.1-preview.1");
  assert.equal(nextSeparateVersion("1.0.0-preview.3", "1.0.0", "1.0.0"), "1.0.0-preview.3", "the same train keeps the version");
});

test("bump advances CsWebUi to the next -preview.N of the train, and --cswebui sets it", () => {
  const equal = bumpVersionFiles(separateFiles("0.6.0-preview.1"), "0.7.0-preview.1");
  assert.deepEqual(cswebuiCopies(equal), ["0.7.0-preview.1", "0.7.0-preview.1", "[0.7.0-preview.1, )"]);
  const implicit = bumpVersionFiles(separateFiles("0.6.0-preview.1", { workspaceEntry: null }), "0.7.0-preview.1");
  assert.match(implicit["eng/Versions.props"], />0\.7\.0-preview\.1<\/RunicCsWebUiPackageVersion>/);
  // The 1.0 train: every rc and 1.0.0 itself take the next CsWebUi preview.
  let value = separateFiles("0.6.0-preview.2");
  for (const [train, own] of [["1.0.0-rc.1", "1.0.0-preview.1"], ["1.0.0-rc.2", "1.0.0-preview.2"], ["1.0.0", "1.0.0-preview.3"], ["1.0.1", "1.0.1-preview.1"]]) {
    value = bumpVersionFiles(value, train);
    assert.equal(checkVersions(value), train);
    assert.deepEqual(cswebuiCopies(value), [own, own, `[${own}, )`], train);
    assert.match(value["examples/web/packages.lock.json"], new RegExp(`"Runic\\.Platform": "\\[${train.replaceAll(".", "\\.")}, \\)"`));
  }
  const set = bumpVersionFiles(separateFiles("0.6.0-preview.1"), "1.0.0", { [cswebui]: "1.0.0-preview.4" });
  assert.equal(checkVersions(set), "1.0.0");
  assert.deepEqual(cswebuiCopies(set), ["1.0.0-preview.4", "1.0.0-preview.4", "[1.0.0-preview.4, )"]);
  assert.match(set["eng/Versions.props"], /<RunicSdkVersion>1\.0\.0</);
  for (const invalid of ["1.0", "1.0.0-rc.1", "1.0.0", "0.6.0-preview.3"])
    assert.throws(() => bumpVersionFiles(separateFiles("0.6.0-preview.1"), "1.0.0", { [cswebui]: invalid }), undefined, invalid);
  assert.throws(() => bumpVersionFiles(separateFiles("0.6.0-preview.1"), "1.0.0", { "Runic.Platform": "1.0.0-preview.1" }));
  assert.throws(() => bumpVersionFiles(separateFiles("0.6.0-preview.1", { workspaceEntry: null }), "1.0.0", { [cswebui]: "1.0.0-preview.1" }));
});

test("from 1.0 the package validation baseline is a stable release, never a prerelease", () => {
  const prerelease = value => ({ ...value, "eng/Versions.props": value["eng/Versions.props"].replace(">0.5.0<", ">1.0.0-rc.1<") });
  assert.doesNotThrow(() => bumpVersionFiles(files(), "1.0.0-rc.1"), "a stable baseline stays through the rcs");
  assert.throws(() => checkVersions(prerelease(bumpVersionFiles(files(), "1.0.0-rc.2"))), /RunicPackageValidationBaselineVersion 1\.0\.0-rc\.1 is a prerelease/);
  assert.throws(() => checkVersions(prerelease(bumpVersionFiles(files(), "1.0.0"))), /is a prerelease/);
  assert.equal(checkVersions(prerelease(files())), "0.6.0-preview.1", "before 1.0 any baseline is accepted");
});

test("each NuGet package packs at its own version", () => {
  const workspace = JSON.parse(separateFiles("1.0.0-preview.1")["eng/workspace.json"]);
  const version = p => nugetPackArguments(workspace, p, { built: true, nuget: "/feed", npm: "/npm" }).filter(arg => /^-p:\w*PackageVersion=/.test(arg));
  // Not a global PackageVersion, which would pin its project references to the same version.
  assert.deepEqual(version(workspace.nuget[0]), ["-p:RunicCsWebUiPackageVersion=1.0.0-preview.1"]);
  assert.deepEqual(version(workspace.nuget[1]), ["-p:PackageVersion=0.6.0-preview.1"]);
  assert.deepEqual(nugetPackArguments(workspace, workspace.nuget[1], { built: false, nuget: "/feed", npm: "/npm", selectedConfiguration: "Release" }),
    ["pack", "packages/dotnet/Runic.Platform/Runic.Platform.csproj", "-c", "Release", "--no-restore", "-o", "/feed", "-p:PackageVersion=0.6.0-preview.1"]);
});
