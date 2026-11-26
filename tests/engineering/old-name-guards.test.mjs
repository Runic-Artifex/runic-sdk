import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { crc32 } from "node:zlib";
import { root } from "../../eng/run.mjs";
import { generate, load, render } from "../../packages/dotnet/Runic.Application.Views/rename/old-names.mjs";

// The old-name guards (D079.11): Runic.Sdk.RenamedNames.targets, which Runic.Application.Views and Runic.Desktop ship,
// is generated from rename/old-names.json. 0.24 lists only the RUNICBRIDGE017 names of 0.7.0-preview.6 and 0.8; the
// rename release adds old package IDs (RUNICBRIDGE085) and environment variables (RUNICBRIDGE086). These tests run the
// generator with a test-only list as well as the shipped one.
const inputFile = resolve(root, "packages/dotnet/Runic.Application.Views/rename/old-names.json");
const shipped = load(inputFile);
let directory;

before(() => {
  directory = mkdtempSync(join(tmpdir(), "runic-old-name-guards-"));
});

after(() => {
  rmSync(directory, { recursive: true, force: true });
});

const withLists = lists => ({ ...shipped.input, packages: [], msbuild: [], environment: [], ...lists });

// A list of every kind, with names that no Runic package uses.
const testLists = {
  packages: [
    { old: "Runic.Example.Direct", new: "RunicArtifex.Example.Direct" },
    { old: "Runic.Example.Transitive", new: "RunicArtifex.Example.Transitive" },
    { old: "Runic.Example.Absent", new: "RunicArtifex.Example.Absent" },
  ],
  msbuild: [
    { old: "RunicExampleProperty", new: "RaxExampleProperty", kind: "property" },
    { old: "RunicExampleItem", new: "RaxExampleItem", kind: "item" },
    { old: "RunicExampleKept", new: "RaxExampleKept", kind: "property", whenNewUnset: true },
    { old: "RunicExampleUnset", new: "RaxExampleUnset", kind: "property" },
  ],
  environment: [
    { old: "RUNIC_EXAMPLE_SET", new: "RAX_EXAMPLE_SET" },
    { old: "RUNIC_EXAMPLE_UNSET", new: "RAX_EXAMPLE_UNSET" },
  ],
};

// Writes the guard file of a list twice, as the two packages ship it, and a project that imports both copies.
function fixture(name, input, body) {
  const project = join(directory, name);
  for (const copy of ["views", "desktop"]) {
    mkdirSync(join(project, copy), { recursive: true });
    writeFileSync(join(project, copy, "Runic.Sdk.RenamedNames.targets"), render(input));
  }
  writeFileSync(join(project, "App.proj"), `<Project>
${body}
  <Import Project="views/Runic.Sdk.RenamedNames.targets" Condition="'$(_RunicSdkRenamedNamesImported)' != 'true'" />
  <Import Project="desktop/Runic.Sdk.RenamedNames.targets" Condition="'$(_RunicSdkRenamedNamesImported)' != 'true'" />
  <Target Name="PrepareForBuild" />
  <Target Name="ResolvePackageAssets" />
</Project>
`);
  return join(project, "App.proj");
}

// Old names of every kind: a property, an item, a direct and (as NuGet resolves it) a transitive package, and an
// environment variable. RunicExampleKept is set beside its new name, RUNIC_EXAMPLE_UNSET is not set.
const oldNames = `  <PropertyGroup>
    <RunicExampleProperty>old</RunicExampleProperty>
    <RunicExampleKept>old</RunicExampleKept>
    <RaxExampleKept>new</RaxExampleKept>
  </PropertyGroup>
  <ItemGroup>
    <RunicExampleItem Include="one;two" />
    <PackageReference Include="runic.example.direct" Version="1.0.0" />
    <PackageDependencies Include="Runic.Example.Transitive" />
  </ItemGroup>`;

function run(project, env = {}) {
  const result = spawnSync("dotnet", ["msbuild", project, "-t:PrepareForBuild;ResolvePackageAssets", "-nologo", "-noAutoResponse",
    "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: directory, encoding: "utf8", env: { ...process.env, ...env } });
  const output = `${result.stdout}${result.stderr}`;
  return { status: result.status, output, errors: output.split(/\r?\n/).filter(line => / error [A-Z]+\d+:/.test(line)) };
}

test("the shipped guard files match old-names.json", () => {
  assert.deepEqual(shipped.outputs.map(path => path.slice(root.length + 1).replaceAll("\\", "/")), [
    "packages/dotnet/Runic.Application.Views/build/Runic.Sdk.RenamedNames.targets",
    "packages/dotnet/Runic.Desktop/buildTransitive/Runic.Sdk.RenamedNames.targets",
  ]);
  assert.deepEqual(generate(inputFile, { check: true }), [],
    "Regenerate with node packages/dotnet/Runic.Application.Views/rename/old-names.mjs");
  const views = readFileSync(resolve(root, "packages/dotnet/Runic.Application.Views/build/Runic.Application.Views.targets"), "utf8");
  const desktop = readFileSync(resolve(root, "packages/dotnet/Runic.Desktop/buildTransitive/Runic.Desktop.targets"), "utf8");
  for (const targets of [views, desktop])
    assert.match(targets, /<Import Project="\$\(MSBuildThisFileDirectory\)Runic\.Sdk\.RenamedNames\.targets" Condition="'\$\(_RunicSdkRenamedNamesImported\)' != 'true'" \/>/);
  // 0.24 ships the package and environment lists empty; the rename release fills them.
  assert.deepEqual([shipped.input.packages, shipped.input.environment], [[], []]);
  assert.deepEqual(shipped.input.family.diagnostics, { packages: "RUNICBRIDGE085", msbuild: "RUNICBRIDGE017", environment: "RUNICBRIDGE086" });
});

test("the generator rejects an invalid list", () => {
  assert.throws(() => render(withLists({ msbuild: [{ old: "RunicA", new: "RaxA", kind: "metadata" }] })), /kind "metadata"/);
  assert.throws(() => render(withLists({ packages: [{ old: "Runic.A", new: "Runic.A" }] })), /renamed to itself/);
  assert.throws(() => render(withLists({ environment: [{ old: "RUNIC_A", new: "RAX_A" }, { old: "runic_a", new: "RAX_B" }] })), /listed twice/);
  assert.throws(() => render(withLists({ packages: [{ old: "Runic.A\" Condition=\"true", new: "RunicArtifex.A" }] })), /valid package ID/);
  assert.throws(() => render({ ...shipped.input, environment: undefined }), /no "environment" list/);
});

test("empty lists report nothing", () => {
  const result = run(fixture("empty", withLists({}), oldNames), { RUNIC_EXAMPLE_SET: "1" });
  assert.equal(result.status, 0, result.output);
  assert.doesNotMatch(result.output, /RUNICBRIDGE/, result.output);
});

test("the shipped list reports nothing for a project without old names", () => {
  const result = run(fixture("shipped", shipped.input, `${oldNames}
  <PropertyGroup>
    <RunicApplicationId>org.example.app</RunicApplicationId>
    <ApplicationId>org.example.app</ApplicationId>
  </PropertyGroup>`), { RUNIC_EXAMPLE_SET: "1" });
  assert.equal(result.status, 0, result.output);
  assert.doesNotMatch(result.output, /RUNICBRIDGE/, result.output);
});

test("a test-only list reports each old name once and names the new one", () => {
  const result = run(fixture("listed", withLists(testLists), oldNames), { RUNIC_EXAMPLE_SET: "1" });
  assert.notEqual(result.status, 0, result.output);
  const of = code => result.errors.filter(line => line.includes(` error ${code}:`));
  // Both copies are imported, so each name would be reported twice without the import guard.
  assert.equal(result.errors.length, 5, result.output);
  assert.deepEqual(of("RUNICBRIDGE017").map(line => line.replace(/^.*?: error RUNICBRIDGE017: /, "").replace(/ The old name.*$/, "")), [
    "The Runic build property RunicExampleProperty is renamed to RaxExampleProperty.",
    "The Runic build item RunicExampleItem is renamed to RaxExampleItem.",
  ]);
  assert.match(of("RUNICBRIDGE017")[0], /rename it in App\.proj, its imports or the command line\./);
  assert.deepEqual(of("RUNICBRIDGE085").map(line => line.match(/references the package (\S+), directly or through another package\. It is renamed to (\S+), and/)?.slice(1)), [
    ["Runic.Example.Direct", "RunicArtifex.Example.Direct"],
    ["Runic.Example.Transitive", "RunicArtifex.Example.Transitive"],
  ]);
  assert.match(of("RUNICBRIDGE085")[0], /Run rax migrate \(dnx RunicArtifex\.Tool migrate without a tool manifest\)/);
  assert.equal(of("RUNICBRIDGE086").length, 1, result.output);
  assert.match(of("RUNICBRIDGE086")[0], /The environment variable RUNIC_EXAMPLE_SET is renamed to RAX_EXAMPLE_SET\. The old name no longer applies; set RAX_EXAMPLE_SET instead/);
});

test("a design-time build reports nothing", () => {
  const result = spawnSync("dotnet", ["msbuild", fixture("design-time", withLists(testLists), oldNames), "-t:PrepareForBuild;ResolvePackageAssets",
    "-p:DesignTimeBuild=true", "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m"], { cwd: directory, encoding: "utf8", env: { ...process.env, RUNIC_EXAMPLE_SET: "1" } });
  assert.equal(result.status, 0, result.stdout + result.stderr);
});

// A package archive with only its .nuspec, stored without compression.
function nupkg(file, id, dependencies = []) {
  const nuspec = Buffer.from(`<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>${id}</id><version>1.0.0</version><authors>test</authors><description>${id}</description>
    <dependencies><group targetFramework="net10.0">${dependencies.map(dependency => `<dependency id="${dependency}" version="1.0.0" />`).join("")}</group></dependencies>
  </metadata>
</package>
`);
  const name = Buffer.from(`${id}.nuspec`);
  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt32LE(crc32(nuspec), 14);
  local.writeUInt32LE(nuspec.length, 18); local.writeUInt32LE(nuspec.length, 22); local.writeUInt16LE(name.length, 26);
  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt32LE(crc32(nuspec), 16);
  central.writeUInt32LE(nuspec.length, 20); central.writeUInt32LE(nuspec.length, 24); central.writeUInt16LE(name.length, 28);
  const centralOffset = local.length + name.length + nuspec.length;
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(1, 8); end.writeUInt16LE(1, 10);
  end.writeUInt32LE(central.length + name.length, 12); end.writeUInt32LE(centralOffset, 16);
  writeFileSync(file, Buffer.concat([local, name, nuspec, central, name, end]));
}

// The real SDK: after restore, ResolvePackageAssets lists a package that another package brings in.
test("a restored package graph reports an old ID that another package brings in", () => {
  const project = join(directory, "restored");
  const feed = join(project, "feed");
  mkdirSync(feed, { recursive: true });
  nupkg(join(feed, "Example.Direct.1.0.0.nupkg"), "Example.Direct", ["Runic.Example.Transitive"]);
  nupkg(join(feed, "Runic.Example.Transitive.1.0.0.nupkg"), "Runic.Example.Transitive");
  writeFileSync(join(project, "Runic.Sdk.RenamedNames.targets"), render(withLists({ packages: testLists.packages })));
  writeFileSync(join(project, "NuGet.config"), `<configuration><packageSources><clear /><add key="feed" value="feed" /></packageSources></configuration>\n`);
  writeFileSync(join(project, "App.csproj"), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RestorePackagesPath>$(MSBuildProjectDirectory)/packages</RestorePackagesPath>
    <NuGetAudit>false</NuGetAudit>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Example.Direct" Version="1.0.0" />
  </ItemGroup>
  <Import Project="Runic.Sdk.RenamedNames.targets" />
</Project>
`);
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.csproj"), "-restore", "-t:ResolvePackageAssets", "-nologo",
    "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  const output = `${result.stdout}${result.stderr}`;
  assert.notEqual(result.status, 0, output);
  const reported = output.split(/\r?\n/).filter(line => / error [A-Z]+\d+:/.test(line));
  assert.equal(reported.length, 1, output);
  assert.match(reported[0], /error RUNICBRIDGE085: App references the package Runic\.Example\.Transitive, directly or through another package\. It is renamed to RunicArtifex\.Example\.Transitive/);
});
