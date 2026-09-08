import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve, join } from "node:path";
import { spawnSync } from "node:child_process";
import { checkShippingProjects, shippingProjects } from "./generate-shipping-projects.mjs";

test("shipping project inventory matches the workspace", checkShippingProjects);
test("shipping project generation rejects ambiguous identities and escaping paths", () => {
  const entry = { name: "Runic.Example", project: "packages/dotnet/Example/Example.csproj" };
  assert.throws(() => shippingProjects({ nuget: [entry, entry] }), /Duplicate/);
  assert.throws(() => shippingProjects({ nuget: [{ ...entry, project: "packages/../outside.csproj" }] }), /Invalid/);
});

test("real NuGet pack pins shipping project dependencies and preserves external ranges", { timeout: 120000 }, () => {
  const directory = mkdtempSync(join(tmpdir(), "runic-package-dependencies-"));
  const xml = value => value.replaceAll("&", "&amp;").replaceAll('"', "&quot;");
  try {
    for (const name of ["Internal", "External", "Parent"]) mkdirSync(join(directory, name));
    writeFileSync(join(directory, "Directory.Build.props"), '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><Version>0.2.0-preview.1</Version></PropertyGroup></Project>');
    writeFileSync(join(directory, "Directory.Build.targets"), `<Project><Import Project="${xml(resolve("eng/build/package-dependencies.targets"))}" /><ItemGroup><RunicShippingProject Include="${xml(join(directory, "Internal/Internal.csproj"))}" /></ItemGroup></Project>`);
    for (const name of ["Internal", "External"]) writeFileSync(join(directory, name, `${name}.csproj`), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><PackageId>${name}.Package</PackageId></PropertyGroup></Project>`);
    writeFileSync(join(directory, "Parent/Parent.csproj"), '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Internal/Internal.csproj" /><ProjectReference Include="../External/External.csproj" /></ItemGroup></Project>');
    // Empty feeds make this deterministic: the fixture has no third-party packages.
    writeFileSync(join(directory, "NuGet.Config"), '<configuration><packageSources><clear /></packageSources></configuration>');
    const packed = spawnSync("dotnet", ["pack", join(directory, "Parent/Parent.csproj"), "-c", "Release", "-o", join(directory, "packages"), "--disable-build-servers", "-p:UseSharedCompilation=false"], { encoding: "utf8", timeout: 100000 });
    assert.equal(packed.status, 0, packed.stdout + packed.stderr);
    // Inspect the actual archive, not intermediate MSBuild item metadata.
    const inspected = spawnSync("python3", ["-c", "import sys,zipfile,xml.etree.ElementTree as E,json; z=zipfile.ZipFile(sys.argv[1]); x=E.fromstring(z.read('Parent.nuspec')); print(json.dumps({d.get('id'):d.get('version') for d in x.findall('.//{*}dependency')}))", join(directory, "packages/Parent.0.2.0-preview.1.nupkg")], { encoding: "utf8" });
    assert.equal(inspected.status, 0, inspected.stderr);
    assert.deepEqual(JSON.parse(inspected.stdout), { "Internal.Package": "[0.2.0-preview.1]", "External.Package": "0.2.0-preview.1" });
  } finally { rmSync(directory, { recursive: true, force: true }); }
});
