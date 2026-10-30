import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { root } from "../../eng/run.mjs";

// RunicDesktopMinimalHost is a documented MSBuild property of Runic.Desktop.Kestrel (packages/dotnet/Runic.Desktop/pack/README.md):
// it becomes the Runic.Desktop.MinimalHost runtime switch, which NativeAOT trims on, and a value other than true or
// false fails the build with RUNICDESK001. RunicDesktopServer selects the presentation server from the server packages
// a project references (X2): RUNICDESK003 for an unknown value, RUNICDESK004 for an executable without a server.
const targets = resolve(root, "packages/dotnet/Runic.Desktop/buildTransitive/Runic.Desktop.targets");
const kestrelProps = resolve(root, "packages/dotnet/Runic.Desktop.Kestrel/buildTransitive/Runic.Desktop.Kestrel.props");
const kestrelTargets = resolve(root, "packages/dotnet/Runic.Desktop.Kestrel/buildTransitive/Runic.Desktop.Kestrel.targets");
const nativeProps = resolve(root, "packages/dotnet/Runic.Desktop.NativeServer/buildTransitive/Runic.Desktop.NativeServer.props");
const nativeTargets = resolve(root, "packages/dotnet/Runic.Desktop.NativeServer/buildTransitive/Runic.Desktop.NativeServer.targets");
let directory;

before(() => {
  directory = mkdtempSync(join(tmpdir(), "runic-desktop-targets-"));
});

after(() => {
  rmSync(directory, { recursive: true, force: true });
});

// servers: the server packages the project references, "kestrel" (the default) and "native".
function project(name, properties, servers = ["kestrel"]) {
  const folder = join(directory, name);
  mkdirSync(folder, { recursive: true });
  const props = [servers.includes("kestrel") && kestrelProps, servers.includes("native") && nativeProps].filter(Boolean);
  const after = [targets, servers.includes("kestrel") && kestrelTargets, servers.includes("native") && nativeTargets].filter(Boolean);
  // As NuGet does: the packages' props before the project's properties, their targets after them.
  writeFileSync(join(folder, "App.proj"), `<Project>
${props.map(file => `  <Import Project="${file}" />`).join("\n")}
  <PropertyGroup>
${properties}
  </PropertyGroup>
  <Target Name="PrepareForBuild" />
${after.map(file => `  <Import Project="${file}" />`).join("\n")}
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

function switches(name, properties, servers) {
  const result = spawnSync("dotnet", ["msbuild", project(name, properties, servers), "-getItem:RuntimeHostConfigurationOption",
    "-getProperty:RunicDesktopServer", "-nologo", "-noAutoResponse", "-nodeReuse:false"], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const output = JSON.parse(result.stdout);
  const items = output.Items.RuntimeHostConfigurationOption ?? [];
  return {
    server: output.Properties.RunicDesktopServer,
    native: items.filter(item => item.Identity === "Runic.Desktop.NativeServer").map(item => `${item.Value}/${item.Trim}`),
    minimal: items.some(item => item.Identity === "Runic.Desktop.MinimalHost"),
  };
}

test("the referenced server package selects the server and writes the trimmable switch", () => {
  assert.deepEqual(switches("kestrel", ""), { server: "Kestrel", native: ["false/true"], minimal: true });
  assert.deepEqual(switches("native", "", ["native"]), { server: "Native", native: ["true/true"], minimal: false });
  assert.deepEqual(switches("both", "", ["kestrel", "native"]), { server: "Native", native: ["true/true"], minimal: false });
  assert.deepEqual(switches("both-kestrel", "<RunicDesktopServer>Kestrel</RunicDesktopServer>", ["kestrel", "native"]),
    { server: "Kestrel", native: ["false/true"], minimal: true });
  assert.deepEqual(switches("none", "", []), { server: "", native: [], minimal: false });
});

function validateServer(name, properties, servers) {
  return spawnSync("dotnet", ["msbuild", project(name, properties, servers), "-t:RunicDesktopValidateServer", "-nologo",
    "-noAutoResponse", "-nodeReuse:false", "-v:m", "-tl:off"], { encoding: "utf8" });
}

test("an executable without a server package fails with RUNICDESK004", () => {
  const result = validateServer("no-server", "<OutputType>Exe</OutputType>", []);
  assert.notEqual(result.status, 0, result.stdout);
  assert.match(result.stdout, /error RUNICDESK004: Runic Desktop has no presentation server since 0\.15\./);
  for (const [name, properties] of [["library", "<OutputType>Library</OutputType>"],
    ["test", "<OutputType>Exe</OutputType><IsTestProject>true</IsTestProject>"],
    ["opted-out", "<OutputType>Exe</OutputType><RunicDesktopServer>None</RunicDesktopServer>"]]) {
    const accepted = validateServer(name, properties, []);
    assert.equal(accepted.status, 0, `${name}: ${accepted.stdout}`);
  }
});

test("a server whose package is missing fails with RUNICDESK004, an unknown one with RUNICDESK003", () => {
  const kestrel = validateServer("missing-kestrel", "<OutputType>Exe</OutputType><RunicDesktopServer>Kestrel</RunicDesktopServer>", ["native"]);
  assert.match(kestrel.stdout, /error RUNICDESK004: RunicDesktopServer is Kestrel, but the project does not reference Runic\.Desktop\.Kestrel\./);
  const native = validateServer("missing-native", "<OutputType>Exe</OutputType><RunicDesktopServer>Native</RunicDesktopServer>");
  assert.match(native.stdout, /error RUNICDESK004: RunicDesktopServer is Native, but the project does not reference Runic\.Desktop\.NativeServer\./);
  const unknown = validateServer("unknown", "<RunicDesktopServer>Axum</RunicDesktopServer>");
  assert.match(unknown.stdout, /error RUNICDESK003: RunicDesktopServer is 'Axum'; it must be Native, Kestrel or None\./);
});
