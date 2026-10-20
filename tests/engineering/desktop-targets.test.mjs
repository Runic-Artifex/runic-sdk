import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { root } from "../../eng/run.mjs";

// RunicDesktopMinimalHost is a documented MSBuild property (packages/dotnet/Runic.Desktop/pack/README.md): it becomes
// the Runic.Desktop.MinimalHost runtime switch, which NativeAOT trims on, and a value other than true or false fails
// the build with RUNICDESK001.
const targets = resolve(root, "packages/dotnet/Runic.Desktop/buildTransitive/Runic.Desktop.targets");
let directory;

before(() => {
  directory = mkdtempSync(join(tmpdir(), "runic-desktop-targets-"));
});

after(() => {
  rmSync(directory, { recursive: true, force: true });
});

function project(name, properties) {
  const folder = join(directory, name);
  mkdirSync(folder, { recursive: true });
  // As NuGet does: the package's targets after the project's properties.
  writeFileSync(join(folder, "App.proj"), `<Project>
  <PropertyGroup>
${properties}
  </PropertyGroup>
  <Target Name="PrepareForBuild" />
  <Import Project="${targets}" />
</Project>
`);
  return join(folder, "App.proj");
}

function switchOf(name, properties) {
  const result = spawnSync("dotnet", ["msbuild", project(name, properties), "-getItem:RuntimeHostConfigurationOption",
    "-nologo", "-noAutoResponse", "-nodeReuse:false"], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const items = JSON.parse(result.stdout).Items.RuntimeHostConfigurationOption ?? [];
  return items.filter(item => item.Identity === "Runic.Desktop.MinimalHost").map(item => ({ value: item.Value, trim: item.Trim }));
}

test("RunicDesktopMinimalHost writes the trimmable runtime switch, false by default", () => {
  assert.deepEqual(switchOf("default", ""), [{ value: "false", trim: "true" }]);
  assert.deepEqual(switchOf("enabled", "<RunicDesktopMinimalHost>true</RunicDesktopMinimalHost>"), [{ value: "true", trim: "true" }]);
  assert.deepEqual(switchOf("disabled", "<RunicDesktopMinimalHost>false</RunicDesktopMinimalHost>"), [{ value: "false", trim: "true" }]);
});

function validate(name, value) {
  return spawnSync("dotnet", ["msbuild", project(name, `<RunicDesktopMinimalHost>${value}</RunicDesktopMinimalHost>`),
    "-t:RunicDesktopValidateHostProfile", "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-tl:off"], { encoding: "utf8" });
}

// MSBuild compares yes, on, no and off as Booleans; the runtime switch reads only true and false, so they are errors.
for (const value of ["maybe", "yes", "on", "no", "off", "1"]) {
  test(`RunicDesktopMinimalHost=${value} fails with RUNICDESK001`, () => {
    const result = validate(`invalid-${value}`, value);
    assert.notEqual(result.status, 0, result.stdout);
    assert.match(result.stdout, new RegExp(`error RUNICDESK001: RunicDesktopMinimalHost is '${value}'; it must be true or false\\.`));
  });
}

for (const value of ["true", "false", "True", "FALSE"]) {
  test(`RunicDesktopMinimalHost=${value} is accepted`, () => {
    const result = validate(`valid-${value}`, value);
    assert.equal(result.status, 0, result.stdout + result.stderr);
  });
}
