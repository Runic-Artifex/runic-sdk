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
  <Target Name="PrepareForILLink" />
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

// The CS-WebUI Window's Development DevTools Protocol endpoint (WebUiRemoteDebugging, D072.2 S2): a trimmed or NativeAOT
// publish turns its feature switch off, so the trimmer removes it and its HTTP client. Only the publish does, in
// PrepareForILLink (before ILLink and ILC): a build of a project that sets PublishAot or PublishTrimmed keeps the endpoint,
// and its runtimeconfig.json has no switch. The fixture's PrepareForILLink is the SDK target's stand-in.
const publishProperties = ["", "<PublishAot>true</PublishAot>", "<PublishTrimmed>true</PublishTrimmed>"];

function remoteDebuggingSwitch(name, properties, { publish = false } = {}) {
  const result = spawnSync("dotnet", ["msbuild", project(name, properties, []), "-getItem:RuntimeHostConfigurationOption",
    ...(publish ? ["-t:PrepareForILLink"] : []), "-nologo", "-noAutoResponse", "-nodeReuse:false"], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const items = JSON.parse(result.stdout).Items.RuntimeHostConfigurationOption ?? [];
  return items.filter(item => item.Identity === "Runic.Desktop.WebUiRemoteDebugging").map(item => ({ value: item.Value, trim: item.Trim }));
}

test("a build keeps the WebUI remote-debugging endpoint, also with PublishAot or PublishTrimmed in the project", () => {
  publishProperties.forEach((properties, index) =>
    assert.deepEqual(remoteDebuggingSwitch(`debugging-build-${index}`, properties), [], properties));
});

test("a trimmed or NativeAOT publish turns the WebUI remote-debugging switch off", () => {
  publishProperties.forEach((properties, index) =>
    assert.deepEqual(remoteDebuggingSwitch(`debugging-publish-${index}`, properties, { publish: true }),
      [{ value: "false", trim: "true" }], properties));
});

// dotnet runic dev's live frontend directory (DevelopmentAssets in Runic.Application.Views.Desktop, D072.2 S4): a trimmed
// or NativeAOT publish turns its feature switch off, so the trimmer removes it and its file watcher; a build keeps it, as
// for WebUiRemoteDebugging above.
const viewsDesktopTargets = resolve(root, "packages/dotnet/Runic.Application.Views.Desktop/build/Runic.Application.Views.Desktop.targets");

function viewsDesktopSwitches(name, properties, { publish = false } = {}) {
  const folder = join(directory, name);
  mkdirSync(folder, { recursive: true });
  writeFileSync(join(folder, "App.proj"), `<Project>
  <PropertyGroup>
${properties}
  </PropertyGroup>
  <Target Name="PrepareForILLink" />
  <Import Project="${viewsDesktopTargets}" />
</Project>
`);
  const result = spawnSync("dotnet", ["msbuild", join(folder, "App.proj"), "-getItem:RuntimeHostConfigurationOption",
    ...(publish ? ["-t:PrepareForILLink"] : []), "-nologo", "-noAutoResponse", "-nodeReuse:false"], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  return JSON.parse(result.stdout).Items.RuntimeHostConfigurationOption ?? [];
}

function developmentAssetsSwitch(name, properties, options) {
  return viewsDesktopSwitches(name, properties, options).filter(item => item.Identity === "Runic.Application.Views.Desktop.DevelopmentAssets")
    .map(item => ({ value: item.Value, trim: item.Trim }));
}

// RunicDesktopRegionHost=false (runic-sdk#1109): the application's pages declare no hosted regions; its choice, so the
// switch applies to every build. The default keeps the region host.
test("RunicDesktopRegionHost=false turns the region host off in every build", () => {
  const regionHost = (name, properties, options) => viewsDesktopSwitches(name, properties, options)
    .filter(item => item.Identity === "Runic.Application.Views.Desktop.RegionHost").map(item => ({ value: item.Value, trim: item.Trim }));
  assert.deepEqual(regionHost("region-default", ""), []);
  assert.deepEqual(regionHost("region-true", "<RunicDesktopRegionHost>true</RunicDesktopRegionHost>"), []);
  assert.deepEqual(regionHost("region-off", "<RunicDesktopRegionHost>false</RunicDesktopRegionHost>"), [{ value: "false", trim: "true" }]);
  assert.deepEqual(regionHost("region-off-publish", "<RunicDesktopRegionHost>false</RunicDesktopRegionHost>", { publish: true }),
    [{ value: "false", trim: "true" }]);
});

test("a build keeps the development assets, also with PublishAot or PublishTrimmed in the project", () => {
  publishProperties.forEach((properties, index) =>
    assert.deepEqual(developmentAssetsSwitch(`assets-build-${index}`, properties), [], properties));
});

test("a trimmed or NativeAOT publish turns the development assets switch off", () => {
  publishProperties.forEach((properties, index) =>
    assert.deepEqual(developmentAssetsSwitch(`assets-publish-${index}`, properties, { publish: true }),
      [{ value: "false", trim: "true" }], properties));
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

// RunicDesktopServer=None declares an executable that opens no window (runic-sdk#1109): Runic.Desktop.PresentationServer
// is off in every build, so a trimmed or NativeAOT one keeps neither server, also with a server package referenced.
function presentationServerSwitch(name, properties, servers) {
  const result = spawnSync("dotnet", ["msbuild", project(name, properties, servers), "-getItem:RuntimeHostConfigurationOption",
    "-nologo", "-noAutoResponse", "-nodeReuse:false"], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const items = JSON.parse(result.stdout).Items.RuntimeHostConfigurationOption ?? [];
  return items.filter(item => item.Identity === "Runic.Desktop.PresentationServer").map(item => ({ value: item.Value, trim: item.Trim }));
}

test("RunicDesktopServer=None turns the presentation server off, also with a server package", () => {
  assert.deepEqual(presentationServerSwitch("server-none", "<RunicDesktopServer>None</RunicDesktopServer>", []), [{ value: "false", trim: "true" }]);
  assert.deepEqual(presentationServerSwitch("server-none-kestrel", "<RunicDesktopServer>None</RunicDesktopServer>", ["kestrel"]),
    [{ value: "false", trim: "true" }]);
  assert.deepEqual(presentationServerSwitch("server-kestrel", "", ["kestrel"]), []);
  assert.deepEqual(presentationServerSwitch("server-native", "", ["native"]), []);
});

function validateServer(name, properties, servers) {
  return spawnSync("dotnet", ["msbuild", project(name, properties, servers), "-t:_RunicDesktopValidateServer", "-nologo",
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

// RunicDesktopServer=None keeps neither server (runic-sdk#1109), so a referenced server package is unused: RUNICDESK007
// warns, and NoWarn suppresses it. Without a server package, None is quiet.
test("RunicDesktopServer=None with a server package warns with RUNICDESK007", () => {
  const none = "<OutputType>Exe</OutputType><RunicDesktopServer>None</RunicDesktopServer>";
  for (const servers of [["kestrel"], ["native"], ["kestrel", "native"]]) {
    const result = validateServer(`none-with-${servers.join("-")}`, none, servers);
    assert.equal(result.status, 0, result.stdout);
    assert.match(result.stdout, /warning RUNICDESK007: RunicDesktopServer is None, but the project references a presentation server package/);
  }
  const quiet = validateServer("none-without-server", none, []);
  assert.equal(quiet.status, 0, quiet.stdout);
  assert.doesNotMatch(quiet.stdout, /RUNICDESK007/);
  // The fixture is not an SDK project: it maps NoWarn to MSBuildWarningsAsMessages as Microsoft.Common.CurrentVersion.targets does.
  const suppressed = validateServer("none-suppressed",
    `${none}<NoWarn>$(NoWarn);RUNICDESK007</NoWarn><MSBuildWarningsAsMessages>$(MSBuildWarningsAsMessages);$(NoWarn)</MSBuildWarningsAsMessages>`,
    ["kestrel"]);
  assert.equal(suppressed.status, 0, suppressed.stdout);
  assert.doesNotMatch(suppressed.stdout, /warning RUNICDESK007/);
});

test("a server whose package is missing fails with RUNICDESK004, an unknown one with RUNICDESK003", () => {
  const kestrel = validateServer("missing-kestrel", "<OutputType>Exe</OutputType><RunicDesktopServer>Kestrel</RunicDesktopServer>", ["native"]);
  assert.match(kestrel.stdout, /error RUNICDESK004: RunicDesktopServer is Kestrel, but the project does not reference Runic\.Desktop\.Kestrel\./);
  const native = validateServer("missing-native", "<OutputType>Exe</OutputType><RunicDesktopServer>Native</RunicDesktopServer>");
  assert.match(native.stdout, /error RUNICDESK004: RunicDesktopServer is Native, but the project does not reference Runic\.Desktop\.NativeServer\./);
  const unknown = validateServer("unknown", "<RunicDesktopServer>Axum</RunicDesktopServer>");
  assert.match(unknown.stdout, /error RUNICDESK003: RunicDesktopServer is 'Axum'; it must be Native, Kestrel or None\./);
});

// RunicDesktopNativeServerLinkage (#859): how a NativeAOT publish gets the native server. Static links the package's (or
// RunicDesktopNativeServerLibraryDirectory's) static library, Shared leaves the shared library beside the executable,
// Application binds the C functions from the application's own library with the runic-server crate in it.
// eng/native/verify-native-server-package.mjs publishes and runs each.
function nativeLinkage(name, properties, { rid = "linux-x64", libraries = ["librunic_server.a", "librunic_server.so"] } = {}) {
  const library = join(directory, `${name}-library`);
  mkdirSync(library, { recursive: true });
  for (const file of libraries) writeFileSync(join(library, file), "");
  const result = spawnSync("dotnet", ["msbuild", project(name, `<OutputType>Exe</OutputType><PublishAot>true</PublishAot>
<RuntimeIdentifier>${rid}</RuntimeIdentifier><RunicDesktopNativeServerLibraryDirectory>${library}</RunicDesktopNativeServerLibraryDirectory>
${properties}`, ["native"]), "-t:_RunicDesktopValidateNativeServer;_RunicDesktopLinkNativeServer", "-getItem:NativeLibrary",
  "-getItem:DirectPInvoke", "-getItem:None", "-getProperty:_RunicDesktopNativeServerLinked", "-nologo", "-noAutoResponse",
  "-nodeReuse:false"], { encoding: "utf8" });
  if (result.status !== 0) return { error: result.stdout + result.stderr };
  const output = JSON.parse(result.stdout);
  const names = items => (items ?? []).map(item => item.Identity.startsWith(library) ? item.Identity.slice(library.length + 1) : item.Identity);
  return {
    linked: output.Properties._RunicDesktopNativeServerLinked === "true",
    directPInvoke: names(output.Items.DirectPInvoke),
    nativeLibrary: names(output.Items.NativeLibrary),
    none: names(output.Items.None),
  };
}

test("RunicDesktopNativeServerLinkage Static, the default, links the static library", () => {
  const expected = { linked: true, directPInvoke: ["runic_server"], nativeLibrary: ["librunic_server.a"], none: ["librunic_server.so"] };
  assert.deepEqual(nativeLinkage("linkage-default", ""), expected);
  assert.deepEqual(nativeLinkage("linkage-static", "<RunicDesktopNativeServerLinkage>Static</RunicDesktopNativeServerLinkage>"), expected);
  const missing = nativeLinkage("linkage-static-missing", "", { libraries: ["librunic_server.so"] });
  assert.match(missing.error, /error RUNICDESK005: A NativeAOT publish for linux-x64 links the native Desktop server statically/);
});

test("RunicDesktopNativeServerLinkage Shared links nothing and keeps the shared library", () => {
  assert.deepEqual(nativeLinkage("linkage-shared", "<RunicDesktopNativeServerLinkage>Shared</RunicDesktopNativeServerLinkage>",
    { libraries: ["librunic_server.so"] }),
  { linked: false, directPInvoke: [], nativeLibrary: [], none: ["librunic_server.so"] });
});

test("RunicDesktopNativeServerLinkage Application binds the C functions without a library of Runic's", () => {
  assert.deepEqual(nativeLinkage("linkage-application", "<RunicDesktopNativeServerLinkage>Application</RunicDesktopNativeServerLinkage>",
    { libraries: ["librunic_server.so"] }),
  { linked: true, directPInvoke: ["runic_server"], nativeLibrary: [], none: ["librunic_server.so"] });
  // Rust's standard library needs the same system libraries on Windows wherever the crate is linked.
  assert.deepEqual(nativeLinkage("linkage-application-win", "<RunicDesktopNativeServerLinkage>application</RunicDesktopNativeServerLinkage>",
    { rid: "win-x64", libraries: ["runic_server.dll"] }),
  { linked: true, directPInvoke: ["runic_server"], nativeLibrary: ["ntdll.lib", "ws2_32.lib", "userenv.lib", "bcrypt.lib"], none: ["runic_server.dll"] });
});

test("an unknown RunicDesktopNativeServerLinkage fails with RUNICDESK006", () => {
  const result = nativeLinkage("linkage-unknown", "<RunicDesktopNativeServerLinkage>Dynamic</RunicDesktopNativeServerLinkage>");
  assert.match(result.error, /error RUNICDESK006: RunicDesktopNativeServerLinkage is 'Dynamic'; it must be Static, Shared or Application\./);
});
