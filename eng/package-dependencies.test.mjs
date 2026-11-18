import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, resolve, join } from "node:path";
import { spawnSync } from "node:child_process";
import { checkShippingProjects, shippingPolicyErrors, shippingProjects } from "./generate-shipping-projects.mjs";

test("shipping project inventory matches the workspace", checkShippingProjects);
test("shipping project generation rejects ambiguous identities and escaping paths", () => {
  const entry = { name: "Runic.Example", project: "packages/dotnet/Example/Example.csproj" };
  assert.throws(() => shippingProjects({ nuget: [entry, entry] }), /Duplicate/);
  assert.throws(() => shippingProjects({ nuget: [{ ...entry, project: "packages/../outside.csproj" }] }), /Invalid/);
});
test("shipping projects must set their component shipping flag", () => {
  const sources = {
    "packages/a.csproj": '<Import Project="../../eng/build/assets.props" /><RunicAssetsShippingProject>true</RunicAssetsShippingProject>',
    "packages/b.csproj": '<Import Project="../../eng/build/assets.props" />',
    "packages/b2.csproj": '<Import Project="../../eng/build/assets.props" /><!-- <RunicAssetsShippingProject>true</RunicAssetsShippingProject> -->',
    "packages/c.csproj": '<Import Project="../../eng/build/desktop.props" /><IsAotCompatible>true</IsAotCompatible>',
    "tools/d.csproj": '<Import Project="../../eng/build/application.props" /><PackageType>Template</PackageType><IncludeBuildOutput>false</IncludeBuildOutput>',
    "tools/e.csproj": "<Project />",
  };
  const workspace = { nuget: Object.keys(sources).map(project => ({ project })) };
  assert.deepEqual(shippingPolicyErrors(workspace, project => sources[project]), [
    "packages/b.csproj must set <RunicAssetsShippingProject>true</RunicAssetsShippingProject> for assets.props",
    "packages/b2.csproj must set <RunicAssetsShippingProject>true</RunicAssetsShippingProject> for assets.props",
    "packages/c.csproj must set <IsTrimmable>true</IsTrimmable> for desktop.props",
    "tools/e.csproj imports no shipping build policy from eng/build",
  ]);
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

// D072.9 (FD1): a RID-specific publish copies only its operating system's Runic.Platform providers and Linux-only
// libraries (Runic.Platform's buildTransitive targets), and its deps.json leaves the others out, so the host starts.
// A build, a portable publish, trimmed or NativeAOT publishes and RunicPublishAllPlatformAssemblies=true keep them all.
// Stand-in packages with the real IDs, from a local feed, exercise the SDK's actual package resolution.
test("a RID-specific publish leaves other operating systems' platform packages out", { timeout: 600000 }, () => {
  const directory = mkdtempSync(join(tmpdir(), "runic-platform-publish-"));
  const xml = value => value.replaceAll("&", "&amp;").replaceAll('"', "&quot;");
  const dotnet = (...args) => {
    const result = spawnSync("dotnet", [...args, "--disable-build-servers", "-p:UseSharedCompilation=false", "-nologo"],
      { cwd: directory, encoding: "utf8", timeout: 240000, env: { ...process.env, NUGET_PACKAGES: join(directory, "nuget") } });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    return result.stdout;
  };
  const packages = {
    "Runic.Platform.Windows": "windows", "Runic.Platform.MacOS": "macos", "Runic.Platform.Linux.Portal": "linux",
    "Tmds.DBus.Protocol": "linux", "GirCore.GLib-2.0": "linux", "Shared.Library": "any",
  };
  const type = id => id.replace(/[^A-Za-z0-9]/g, "_");
  try {
    writeFileSync(join(directory, "NuGet.Config"), `<configuration><packageSources><clear /><add key="local" value="${xml(join(directory, "feed"))}" /></packageSources></configuration>`);
    writeFileSync(join(directory, "Directory.Build.props"), '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><Version>1.0.0</Version><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>');
    for (const id of Object.keys(packages)) {
      mkdirSync(join(directory, "src", id), { recursive: true });
      writeFileSync(join(directory, "src", id, `${id}.csproj`), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><PackageId>${id}</PackageId><AssemblyName>${id}</AssemblyName></PropertyGroup></Project>`);
      writeFileSync(join(directory, "src", id, "Probe.cs"), `public sealed class ${type(id)} { public static string Name => "${id}"; }`);
      dotnet("pack", join("src", id), "-c", "Release", "-o", "feed");
    }
    mkdirSync(join(directory, "app"));
    // As NuGet imports Runic.Platform's buildTransitive targets into a package consumer.
    writeFileSync(join(directory, "app", "Directory.Build.targets"), `<Project><Import Project="${xml(resolve("packages/dotnet/Runic.Platform/buildTransitive/Runic.Platform.targets"))}" /></Project>`);
    writeFileSync(join(directory, "app", "App.csproj"), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><UseAppHost>false</UseAppHost></PropertyGroup><ItemGroup>${Object.keys(packages).map(id => `<PackageReference Include="${id}" Version="1.0.0" />`).join("")}</ItemGroup></Project>`);
    // Each provider is called only from a method of its own that runs on its operating system, as Runic does.
    const probe = { windows: "OperatingSystem.IsWindows()", macos: "OperatingSystem.IsMacOS()", linux: "OperatingSystem.IsLinux()" };
    const ids = Object.keys(packages).filter(id => packages[id] !== "any");
    writeFileSync(join(directory, "app", "Program.cs"), `using System.Runtime.CompilerServices;
Console.WriteLine(Declarations.All);
${ids.map(id => `if (${probe[packages[id]]}) Console.WriteLine(Probes.${type(id)}());`).join("\n")}
// As the generated PrivilegedActions class: static fields of every provider's types, initialized on first use.
static class Declarations
{
${ids.map(id => `    static global::${type(id)}? _${type(id)};`).join("\n")}
    public static readonly string All = Shared_Library.Name;
}
static class Probes
{
${ids.map(id => `    [MethodImpl(MethodImplOptions.NoInlining)] public static string ${type(id)}() => global::${type(id)}.Name;`).join("\n")}
}
`);
    // No apphost: the fixture's feed has no packages but its own, and only the host's apphost pack ships with the SDK.
    const shipped = (rid, ...args) => {
      const output = join(directory, "out", `${rid ?? "portable"}${args.length ? "-optout" : ""}`);
      dotnet("publish", "app", "-c", "Release", "-o", output, ...(rid ? ["-r", rid, "--self-contained", "false"] : []), ...args);
      const deps = JSON.parse(readFileSync(join(output, "App.deps.json"), "utf8"));
      const listed = Object.values(deps.targets).flatMap(Object.values).flatMap(entry =>
        [...Object.keys(entry.runtime ?? {}), ...Object.keys(entry.runtimeTargets ?? {})]).map(path => basename(path, ".dll"));
      const files = Object.keys(packages).filter(id => existsSync(join(output, `${id}.dll`)));
      // Every assembly deps.json lists is in the output, so the host starts.
      assert.deepEqual(listed.filter(id => id !== "App").sort(), files.slice().sort(), `${output}: deps.json`);
      return { output, files };
    };
    const only = os => Object.keys(packages).filter(id => packages[id] === "any" || packages[id] === os).sort();
    const all = Object.keys(packages).sort();
    const linux = shipped("linux-x64");
    assert.deepEqual(linux.files.sort(), only("linux"));
    assert.deepEqual(shipped("win-x64").files.sort(), only("windows"));
    assert.deepEqual(shipped("osx-arm64").files.sort(), only("macos"));
    assert.deepEqual(shipped(undefined).files.sort(), all);
    assert.deepEqual(shipped("linux-x64", "-p:RunicPublishAllPlatformAssemblies=true").files.sort(), all);
    // The build output keeps every assembly, also for a RuntimeIdentifier.
    dotnet("build", "app", "-c", "Release", "-r", "linux-x64", "--self-contained", "false");
    const built = join(directory, "app", "bin", "Release", "net10.0", "linux-x64");
    assert.deepEqual(all.filter(id => existsSync(join(built, `${id}.dll`))), all);
    if (process.platform === "linux" && process.arch === "x64") {
      const run = spawnSync("dotnet", [join(linux.output, "App.dll")], { encoding: "utf8", timeout: 60000 });
      assert.equal(run.status, 0, run.stdout + run.stderr);
      assert.deepEqual(run.stdout.trim().split(/\r?\n/), ["Shared.Library", "Runic.Platform.Linux.Portal", "Tmds.DBus.Protocol", "GirCore.GLib-2.0"]);
    }
  } finally { rmSync(directory, { recursive: true, force: true }); }
});
