#!/usr/bin/env node
// Package-consumer test of the experimental native Desktop server (X2): a consumer that knows nothing of this
// repository restores Runic.Desktop and Runic.Desktop.NativeServer from a local feed, serves a surface and reads it
// back (tests/fixtures/desktop/Runic.Desktop.PackageConsumer), first JIT, then as a NativeAOT executable. It checks
// that the closure has no ASP.NET Core, that a JIT build loads the package's shared library, and that NativeAOT
// restores Runic.Desktop.NativeServer.Static.<rid> through the package's runtime.json and links it: one executable,
// no librunic_server beside it, and no static library in any output.
//
//   node eng/native/verify-native-server-package.mjs <feed> [--rid <rid>] [--no-aot] [--keep]
//
// <feed> holds Runic.Desktop.NativeServer and Runic.Desktop.NativeServer.Static.<rid> packages; the script packs
// Runic.Desktop and its Runic dependencies from this checkout into it with the same version. Writes
// <feed>/../native-server-consumer.json with the sizes it saw.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const args = process.argv.slice(2);
const feed = args[0] && !args[0].startsWith("--") ? resolve(args[0]) : null;
if (!feed) {
  console.error("usage: node eng/native/verify-native-server-package.mjs <feed> [--rid <rid>] [--no-aot] [--keep]");
  process.exit(2);
}
const option = name => { const index = args.indexOf(name); return index < 0 ? undefined : args[index + 1]; };
const hostRid = () => {
  const arch = process.arch === "arm64" ? "arm64" : "x64";
  if (process.platform === "win32") return `win-${arch}`;
  if (process.platform === "darwin") return `osx-${arch}`;
  const musl = existsSync("/etc/alpine-release");
  return `linux-${musl ? "musl-" : ""}${arch}`;
};
const rid = option("--rid") ?? hostRid();
const aot = !args.includes("--no-aot");
const keep = args.includes("--keep");
const version = JSON.parse(readFileSync(join(root, "eng/workspace.json"), "utf8")).version;
const exe = process.platform === "win32" ? ".exe" : "";

function run(command, commandArgs, cwd, env = {}) {
  console.log(`> ${command} ${commandArgs.join(" ")}`);
  return execFileSync(command, commandArgs, { cwd, env: { ...process.env, ...env }, encoding: "utf8", stdio: ["ignore", "pipe", "inherit"] });
}

const files = directory => readdirSync(directory, { recursive: true, withFileTypes: true })
  .filter(entry => entry.isFile()).map(entry => join(entry.parentPath, entry.name));
const bytes = list => list.reduce((sum, file) => sum + statSync(file).size, 0);

// The managed closure, packed from this checkout with the feed's version.
for (const project of ["Runic.Platform", "Runic.Platform.Linux.DBus", "Runic.Desktop"]) {
  if (readdirSync(feed).some(file => file === `${project}.${version}.nupkg`)) continue;
  run("dotnet", ["pack", join(root, "packages/dotnet", project), "-c", "Release", "-o", feed, `-p:PackageVersion=${version}`], root);
}
assert.ok(readdirSync(feed).includes(`Runic.Desktop.NativeServer.${version}.nupkg`), `${feed} has no Runic.Desktop.NativeServer ${version}`);
if (aot)
  assert.ok(readdirSync(feed).includes(`Runic.Desktop.NativeServer.Static.${rid}.${version}.nupkg`),
    `${feed} has no Runic.Desktop.NativeServer.Static.${rid} ${version}`);

const directory = mkdtempSync(join(tmpdir(), "runic-native-server-consumer-"));
const report = { rid, version };
try {
  const consumer = join(directory, "consumer");
  cpSync(join(root, "tests/fixtures/desktop/Runic.Desktop.PackageConsumer"), consumer, { recursive: true,
    filter: source => !/[\\/](bin|obj)$/.test(source) });
  // No parent Directory.Build.* or NuGet.config of this repository; Runic packages only from the feed.
  writeFileSync(join(directory, "Directory.Build.props"), "<Project />\n");
  writeFileSync(join(directory, "Directory.Build.targets"), "<Project />\n");
  writeFileSync(join(directory, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>\n");
  writeFileSync(join(directory, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${feed}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
`);
  const env = { NUGET_PACKAGES: join(directory, "nuget-cache"), DOTNET_CLI_HOME: join(directory, "dotnet-home") };
  const properties = [`-p:RunicDesktopPackageVersion=${version}`, "-p:RunicPackageConsumerServer=Native"];
  const project = join(consumer, "Runic.Desktop.PackageConsumer.csproj");

  // JIT, framework-dependent: the shared library comes from runtimes/<rid>/native.
  const jit = join(directory, "jit");
  run("dotnet", ["publish", project, "-c", "Release", "-r", rid, "--self-contained", "false", "-o", jit, ...properties], consumer, env);
  const assets = JSON.parse(readFileSync(join(consumer, "obj/project.assets.json"), "utf8"));
  const frameworkReferences = [
    ...Object.values(assets.project.frameworks).flatMap(framework => Object.keys(framework.frameworkReferences ?? {})),
    ...Object.values(assets.targets).flatMap(target => Object.values(target)).flatMap(library => library.frameworkReferences ?? []),
  ];
  assert.ok(!frameworkReferences.includes("Microsoft.AspNetCore.App"), "the native server consumer restores ASP.NET Core");
  assert.ok(!Object.keys(assets.libraries).some(name => /^Runic\.Desktop\.Kestrel\//i.test(name)), "the consumer restores Kestrel");
  const runtimeConfig = JSON.parse(readFileSync(join(jit, "Runic.Desktop.PackageConsumer.runtimeconfig.json"), "utf8")).runtimeOptions;
  const frameworks = [runtimeConfig.framework, ...(runtimeConfig.frameworks ?? [])].filter(Boolean).map(framework => framework.name);
  assert.deepEqual(frameworks, ["Microsoft.NETCore.App"], "the consumer needs a shared framework besides .NET");
  assert.equal(runtimeConfig.configProperties?.["Runic.Desktop.NativeServer"], true, "the native server switch is not set");
  const jitFiles = files(jit);
  assert.ok(jitFiles.some(file => /(lib)?runic_server\.(so|dylib|dll)$/.test(file)), "the JIT output has no native server library");
  assert.ok(!jitFiles.some(file => /(lib)?runic_server\.(a|lib)$/.test(file)), "the JIT output carries the static library");
  assert.ok(!jitFiles.some(file => /Microsoft\.AspNetCore\.|Runic\.Desktop\.Kestrel/.test(file)), "the JIT output carries ASP.NET Core");
  const jitOutput = run(join(jit, `Runic.Desktop.PackageConsumer${exe}`), [], jit);
  assert.match(jitOutput, /Runic\.Desktop package consumer passed/);
  report.jit = { files: jitFiles.length, bytes: bytes(jitFiles) };

  // Self-contained JIT: the runtime-specific restore brings the static package, whose library stays out.
  const selfContained = join(directory, "self-contained");
  run("dotnet", ["publish", project, "-c", "Release", "-r", rid, "--self-contained", "true", "-o", selfContained, ...properties], consumer, env);
  const scFiles = files(selfContained);
  assert.ok(!scFiles.some(file => /(lib)?runic_server\.(a|lib)$/.test(file)), "the self-contained output carries the static library");
  assert.ok(!scFiles.some(file => /Microsoft\.AspNetCore\./.test(file)), "the self-contained output carries ASP.NET Core");
  report.selfContained = { files: scFiles.length, bytes: bytes(scFiles) };

  if (aot) {
    const native = join(directory, "aot");
    run("dotnet", ["publish", project, "-c", "Release", "-r", rid, "-p:PublishAot=true", "-o", native, ...properties], consumer, env);
    const aotAssets = JSON.parse(readFileSync(join(consumer, "obj/project.assets.json"), "utf8"));
    assert.ok(Object.keys(aotAssets.libraries).some(name => name.toLowerCase() === `runic.desktop.nativeserver.static.${rid}/${version}`),
      `the NativeAOT restore did not bring Runic.Desktop.NativeServer.Static.${rid} (runtime.json)`);
    const aotFiles = files(native).filter(file => !/\.(pdb|dbg|dSYM)$/.test(file) && !file.includes(".dSYM"));
    assert.ok(!aotFiles.some(file => /(lib)?runic_server\.(so|dylib|dll|a|lib)$/.test(file)),
      `the NativeAOT output has a native server library beside the executable: ${aotFiles.join(", ")}`);
    const executable = join(native, `Runic.Desktop.PackageConsumer${exe}`);
    assert.match(run(executable, [], native), /Runic\.Desktop package consumer passed/);
    report.aot = { files: aotFiles.length, bytes: bytes(aotFiles), executableBytes: statSync(executable).size };
  }
  const reportFile = join(dirname(feed), "native-server-consumer.json");
  writeFileSync(reportFile, `${JSON.stringify(report, null, 2)}\n`);
  console.log(JSON.stringify(report));
  console.log(`Native server package consumer passed for ${rid}.`);
} finally {
  if (keep) console.log(`Kept ${directory}`);
  else rmSync(directory, { recursive: true, force: true, maxRetries: 5 });
}
