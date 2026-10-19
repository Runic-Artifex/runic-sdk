import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";
import { root } from "./run.mjs";

const dotnet = (args, cwd) => spawnSync("dotnet", args, { cwd, encoding: "utf8" });

// W319-003 L3: verification builds treat warnings as errors and audit NuGet packages, so a locked restore without
// network access (Nix, an air-gapped mirror) failed on NU1900, "vulnerability data could not be read". It reports no
// vulnerability and stays a warning; dependency-audit.yml audits with network access.
test("an offline verification restore warns about missing audit data instead of failing", { timeout: 120000 }, () => {
  const packages = dotnet(["nuget", "locals", "global-packages", "--list"], root).stdout.replace(/^global-packages:\s*/, "").trim();
  const version = /Include="Microsoft\.Extensions\.DependencyInjection\.Abstractions" Version="([^"]+)"/
    .exec(readFileSync(resolve(root, "Directory.Packages.props"), "utf8"))[1];
  const directory = mkdtempSync(join(tmpdir(), "runic-offline-audit-"));
  try {
    // The package comes from the repository's package folder; the only source is unreachable.
    writeFileSync(join(directory, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="offline" value="https://127.0.0.1:9/v3/index.json" /></packageSources>
  <config><add key="globalPackagesFolder" value="${packages}" /></config>
</configuration>
`);
    writeFileSync(join(directory, "Offline.csproj"), `<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="${resolve(root, "eng/build/common.props")}" />
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="${version}" /></ItemGroup>
</Project>
`);
    const result = dotnet(["restore", "Offline.csproj", "-p:CI=true", "-nologo"], directory);
    const output = result.stdout + result.stderr;
    assert.match(output, /warning NU1900/, output);
    assert.doesNotMatch(output, /error NU1900/, output);
    assert.equal(result.status, 0, output);

    const properties = JSON.parse(dotnet(["msbuild", "Offline.csproj", "-p:CI=true", "-nologo",
      "-getProperty:RunicVerificationBuild,TreatWarningsAsErrors,NuGetAudit"], directory).stdout).Properties;
    assert.deepEqual(properties, { RunicVerificationBuild: "true", TreatWarningsAsErrors: "true", NuGetAudit: "true" },
      "The verification build still audits with warnings as errors.");
  } finally { rmSync(directory, { recursive: true, force: true }); }
});
