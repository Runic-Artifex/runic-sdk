import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readlinkSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { root } from "../../eng/run.mjs";

// G-8: on macOS, dotnet run (and dotnet watch and dotnet runic dev, which use it) starts the application from a
// development bundle that Runic.Platform.MacOS's targets make when dotnet run computes the command
// (ComputeRunArguments). These tests run the target on any OS, as on a Mac (_RunicMacDevelopmentBundleHost), with a
// script in place of codesign, and check the bundle, its Info.plist, the links to the build output and when it is
// signed again. The macOS packaging leg runs the probe through dotnet run on a Mac.
const targets = resolve(root, "packages/dotnet/Runic.Platform.MacOS/buildTransitive/Runic.Platform.MacOS.targets");
const windows = process.platform === "win32";
let directory;

before(() => {
  directory = mkdtempSync(join(tmpdir(), "runic-macos-bundle-"));
});

after(() => {
  rmSync(directory, { recursive: true, force: true });
});

function fixture(name, properties = "") {
  const project = join(directory, name);
  const output = join(project, "bin", "Debug", "net10.0");
  mkdirSync(join(output, "www"), { recursive: true });
  // The build output of an application with its host: the host, the managed assembly, a configuration file and the frontend.
  writeFileSync(join(output, "Notes"), "#!/bin/sh\n");
  chmodSync(join(output, "Notes"), 0o755);
  writeFileSync(join(output, "Notes.dll"), "dll");
  writeFileSync(join(output, "Notes.runtimeconfig.json"), "{}");
  writeFileSync(join(output, "www", "index.html"), "<p>notes</p>");
  const codesign = join(project, "codesign.sh");
  // Records each call and marks the bundle signed, as codesign does with _CodeSignature.
  writeFileSync(codesign, `#!/bin/sh\necho "$@" >> "${join(project, "codesign.log")}"\nfor last; do :; done\nmkdir -p "$last/Contents/_CodeSignature"\n`);
  chmodSync(codesign, 0o755);
  // The properties the .NET SDK sets for an executable with an application host, then the project's, then the package's
  // targets, as NuGet imports them. ComputeRunArguments is the SDK's placeholder target, which dotnet run runs.
  writeFileSync(join(project, "App.proj"), `<Project>
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UseAppHost>true</UseAppHost>
    <AssemblyName>Notes</AssemblyName>
    <TargetDir>${output}/</TargetDir>
    <IntermediateOutputPath>obj/Debug/net10.0/</IntermediateOutputPath>
    <RunCommand>${output}/Notes</RunCommand>
    <ApplicationId>org.example.Notes</ApplicationId>
    <_RunicMacDevelopmentBundleHost>true</_RunicMacDevelopmentBundleHost>
    <_RunicMacCodesign>${codesign}</_RunicMacCodesign>
${properties}
  </PropertyGroup>
  <Target Name="ComputeRunArguments" />
  <Import Project="${targets}" />
</Project>
`);
  return { project, output, bundle: join(project, "obj", "Debug", "net10.0", "runic-macos", "Notes.app") };
}

function runCommand({ project }, properties = []) {
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:ComputeRunArguments", "-getProperty:RunCommand",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", ...properties.map(property => `-p:${property}`)], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  return result.stdout.trim();
}

const signatures = ({ project }) =>
  existsSync(join(project, "codesign.log")) ? readFileSync(join(project, "codesign.log"), "utf8").trim().split("\n") : [];

test("dotnet run starts the application from an ad-hoc signed development bundle", { skip: windows && "symbolic links need privileges on Windows" }, () => {
  const app = fixture("bundle", `
    <ApplicationTitle>Notes &amp; Co</ApplicationTitle>
    <Version>2.1.0-preview.3</Version>
    <RunicApplicationUrlSchemes>Notes; notes-x</RunicApplicationUrlSchemes>
    <RunicApplicationFileTypes>.notes=application/x-example-notes;.MD</RunicApplicationFileTypes>
    <RunicMacCameraUsageDescription>Notes shows your camera in &lt;video&gt; notes.</RunicMacCameraUsageDescription>
    <RunicMacMicrophoneUsageDescription>Notes records voice notes.</RunicMacMicrophoneUsageDescription>`);
  const macOS = join(app.bundle, "Contents", "MacOS");
  assert.equal(runCommand(app), join(macOS, "Notes"));
  // The host is a copy, which the signature covers; everything else links to the build output.
  assert.equal(lstatSync(join(macOS, "Notes")).isSymbolicLink(), false);
  assert.equal(readFileSync(join(macOS, "Notes"), "utf8"), "#!/bin/sh\n");
  assert.equal(lstatSync(join(macOS, "Notes")).mode & 0o111, 0o111);
  for (const name of ["Notes.dll", "Notes.runtimeconfig.json", "www"])
    assert.equal(readlinkSync(join(macOS, name)), join(app.output, name), name);
  const info = readFileSync(join(app.bundle, "Contents", "Info.plist"), "utf8");
  for (const entry of [
    "<key>CFBundleIdentifier</key><string>org.example.Notes</string>",
    "<key>CFBundleName</key><string>Notes &amp; Co</string>",
    "<key>CFBundleExecutable</key><string>Notes</string>",
    "<key>CFBundlePackageType</key><string>APPL</string>",
    "<key>CFBundleShortVersionString</key><string>2.1.0</string>",
    "<key>CFBundleURLSchemes</key><array><string>notes</string><string>notes-x</string></array>",
    "<key>CFBundleTypeExtensions</key><array><string>notes</string></array>",
    "<key>CFBundleTypeExtensions</key><array><string>md</string></array>",
    "<key>NSCameraUsageDescription</key><string>Notes shows your camera in &lt;video&gt; notes.</string>",
    "<key>NSMicrophoneUsageDescription</key><string>Notes records voice notes.</string>",
  ]) assert.ok(info.includes(entry), `${entry}\n${info}`);
  // The development bundle opens its file types but never claims them, and exports no type.
  assert.equal(info.match(/<key>LSHandlerRank<\/key><string>Alternate<\/string>/g)?.length, 2, info);
  assert.ok(!info.includes("Owner") && !info.includes("UTExportedTypeDeclarations"), info);
  assert.deepEqual(signatures(app), [`--force --sign - --timestamp=none ${app.bundle}`]);

  // An unchanged build is not signed again.
  runCommand(app);
  assert.equal(signatures(app).length, 1);

  // A file the build adds is linked and the bundle signed again; a file it removes loses its link.
  writeFileSync(join(app.output, "appsettings.json"), "{}");
  runCommand(app);
  assert.equal(readlinkSync(join(macOS, "appsettings.json")), join(app.output, "appsettings.json"));
  assert.equal(signatures(app).length, 2);
  rmSync(join(app.output, "appsettings.json"));
  runCommand(app);
  assert.equal(existsSync(join(macOS, "appsettings.json")), false);
  assert.equal(signatures(app).length, 3);

  // A rebuilt host is copied again.
  writeFileSync(join(app.output, "Notes"), "#!/bin/sh\necho rebuilt\n");
  runCommand(app);
  assert.equal(readFileSync(join(macOS, "Notes"), "utf8"), "#!/bin/sh\necho rebuilt\n");
  assert.equal(signatures(app).length, 4);

  // RunicMacBundleIdentifier names the bundle, as for the privileged daemon.
  runCommand(app, ["RunicMacBundleIdentifier=org.example.NotesApp"]);
  assert.ok(readFileSync(join(app.bundle, "Contents", "Info.plist"), "utf8").includes("<string>org.example.NotesApp</string>"));
  assert.equal(signatures(app).length, 5);
});

test("dotnet run starts the build output without the development bundle when it does not apply", { skip: windows && "symbolic links need privileges on Windows" }, () => {
  const app = fixture("skipped");
  const output = join(app.output, "Notes");
  for (const properties of [
    ["RunicMacDevelopmentBundle=false"],
    ["_RunicMacDevelopmentBundleHost=false"],
    ["ApplicationId="],
    ["IsTestProject=true"],
    ["RunicMacPrivilegedDaemon=true"],
    ["UseAppHost=false"],
    ["OutputType=Library"],
    ["RuntimeIdentifier=linux-x64"],
  ]) {
    assert.equal(runCommand(app, properties), output, properties.join(" "));
    assert.equal(existsSync(app.bundle), false, properties.join(" "));
  }
  assert.deepEqual(signatures(app), []);
  // Without a built host, dotnet run starts what it would have.
  rmSync(output);
  assert.equal(runCommand(app), output);
  assert.equal(existsSync(join(app.bundle, "Contents", "MacOS", "Notes")), false);
});
