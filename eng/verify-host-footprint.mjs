import assert from "node:assert/strict";
import { assertCsWebUiDependencies } from "./ci/nuget-graph.mjs";
import { isWithinDirectory } from "./path-boundary.mjs";
import { cpSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync, realpathSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve, join } from "node:path";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { root, workspace, run, configuration } from "./run.mjs";

const rid = process.env.RUNIC_SIZE_RID ?? ({linux: "linux", win32: "win", darwin: "osx"}[process.platform] + "-" + process.arch);
const temporary = realpathSync(mkdtempSync(join(tmpdir(), "runic-host-footprint-")));
const results = resolve(root, "artifacts/host-footprint", rid, `run-${Date.now()}`);
mkdirSync(results, { recursive: true });
try {
const feed = resolve(root, "artifacts/packages/nuget");
const source = resolve(root, "examples/customer-migration");
const frontend = join(temporary, "frontend");
cpSync(resolve(source, "Host/Frontend"), frontend, { recursive: true,
  filter: path => !/(^|[/\\])(node_modules|dist)([/\\]|$)/.test(path) });
const manifest = JSON.parse(readFileSync(join(frontend, "package.json"), "utf8"));
const candidates = {};
for (const group of [manifest.dependencies, manifest.devDependencies]) {
  for (const name of Object.keys(group)) {
    if (!name.startsWith("@runic-artifex/")) continue;
    const archive = resolve(root, "artifacts/packages/npm", `${name.replace("@", "").replace("/", "-")}-${workspace.version}.tgz`);
    group[name] = `file:${archive.replaceAll("\\", "/")}`;
    candidates[name] = createHash("sha256").update(readFileSync(archive)).digest("hex");
  }
}
// Bind transitive SDK references as well as direct dependencies to the candidates.
// Bun may otherwise resolve a dependency's release version through the registry.
manifest.overrides = { ...manifest.overrides, ...Object.fromEntries(Object.keys(candidates)
  .map(name => [name, manifest.dependencies?.[name] ?? manifest.devDependencies[name]])) };
writeFileSync(join(frontend, "package.json"), JSON.stringify(manifest, null, 2));
run("bun", ["install", "--ignore-scripts"], frontend, { BUN_INSTALL_CACHE_DIR: join(temporary, "bun-cache") });
for (const name of Object.keys(candidates))
  assert.ok(isWithinDirectory(frontend, join(frontend, "node_modules", name)), `${name} resolved to workspace source`);
// Build one package-only frontend and embed exactly those bytes in every candidate.
run("bun", ["run", "--bun", "build"], frontend, { VITE_RUNIC_HOST: "desktop" });
cpSync(join(frontend, "bun.lock"), join(results, "frontend-bun.lock"));
const xml = value => value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;");
writeFileSync(join(temporary, "NuGet.config"), `<configuration><packageSources><clear/><add key="candidate" value="${xml(feed)}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>`);
const env = { NUGET_PACKAGES: join(temporary, "packages"), DOTNET_CLI_HOME: join(temporary, "dotnet-home"),
  NUGET_HTTP_CACHE_PATH: join(temporary, "http-cache") };
writeFileSync(join(temporary, "Directory.Build.props"), `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><InvariantGlobalization>true</InvariantGlobalization><StripSymbols>true</StripSymbols><DebugType>none</DebugType><IlcTreatWarningsAsErrors>true</IlcTreatWarningsAsErrors></PropertyGroup></Project>`);
function copyApplication(name, nativeProvider) {
  const destination = join(temporary, name);
  cpSync(source, destination, { recursive: true, filter: path => {
    const relative = path.slice(source.length).replaceAll("\\", "/");
    if (/(^|\/)(bin|obj|node_modules|Wpf|Before|Tests)(\/|$)/.test(relative)) return false;
    if (relative.startsWith("/Host/Frontend/")) return false;
    return true;
  }});
  cpSync(join(frontend, "dist"), join(destination, "Host/Frontend/dist"), { recursive: true });
  // App-to-app references stay local. Every SDK reference comes from a package candidate.
  const after = join(destination, "After/CustomerApplication.csproj");
  writeFileSync(after, `<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Domain/CustomerDomain.csproj"/><PackageReference Include="Runic.Platform" Version="${workspace.version}"/><PackageReference Include="Runic.Application.Bridge" Version="${workspace.version}"/><PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11"/></ItemGroup></Project>`);
  const host = join(destination, "Host/CustomerDesktop.csproj");
  writeFileSync(host, `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><RunicHost Condition="'$(RunicHost)' == ''">desktop</RunicHost><DefineConstants Condition="'$(RunicHost)' == 'cswebui'">$(DefineConstants);RUNIC_CSWEBUI</DefineConstants><RunicAssetsDist>$(MSBuildProjectDirectory)/Frontend/dist</RunicAssetsDist><RunicApplicationBridgeGenerate>false</RunicApplicationBridgeGenerate><RunicNativeProvider>${nativeProvider}</RunicNativeProvider><DefineConstants Condition="'$(RunicHost)' == 'desktop' and '$(RunicNativeProvider)' != 'None'">$(DefineConstants);RUNIC_PLATFORM_$(RunicNativeProvider)</DefineConstants></PropertyGroup><ItemGroup><ProjectReference Include="../After/CustomerApplication.csproj"/><PackageReference Include="Runic.Platform" Version="${workspace.version}"/><PackageReference Include="Runic.Platform.Runtime" Version="${workspace.version}"/><PackageReference Include="Runic.Application.Platform" Version="${workspace.version}"/><PackageReference Include="Runic.Application.Platform.Desktop" Version="${workspace.version}" Condition="'$(RunicHost)' == 'desktop'"/><PackageReference Include="Runic.Platform.$(RunicNativeProvider)" Version="${workspace.version}" Condition="'$(RunicHost)' == 'desktop' and '$(RunicNativeProvider)' != 'None'"/><PackageReference Include="Runic.Application" Version="${workspace.version}"/><PackageReference Include="Runic.Assets" Version="${workspace.version}"/><PackageReference Include="Runic.Application.CsWebUi" Version="${workspace.version}" Condition="'$(RunicHost)' == 'cswebui'"/><PackageReference Include="Runic.Application.Desktop" Version="${workspace.version}" Condition="'$(RunicHost)' == 'desktop'"/><PackageReference Include="Runic.Assets.Desktop" Version="${workspace.version}" Condition="'$(RunicHost)' == 'desktop'"/></ItemGroup></Project>`);
  return host;
}
const metadata = { schema: "runic.host-footprint-matrix/1", rid,
  revision: execFileSync("git", ["rev-parse", "HEAD"], { cwd: root, encoding: "utf8" }).trim(),
  dirty: execFileSync("git", ["status", "--porcelain"], { cwd: root, encoding: "utf8" }).length !== 0,
  consumerDirectory: temporary, npmCandidates: candidates,
  nugetCandidates: Object.fromEntries(workspace.nuget.map(item => {
    const name = `${item.name}.${workspace.version}.nupkg`;
    return [name, createHash("sha256").update(readFileSync(join(feed, name))).digest("hex")];
  })), cases: [] };
writeFileSync(join(results, "matrix.json"), JSON.stringify(metadata, null, 2));
const osProvider = {linux: "Linux", win32: "Windows", darwin: "MacOS"}[process.platform];
assert.ok(osProvider, "Unsupported native provider platform");
for (const [host, profile, nativeProvider] of [["desktop", "default", "None"], ["desktop", "minimal", "None"], ["cswebui", "default", "None"], ["desktop", "default", osProvider]]) {
  const name = `${host}-${profile}${nativeProvider === "None" ? "" : "-provider"}`;
  const project = copyApplication(name, nativeProvider);
  const report = join(results, `${name}.json`);
  run("dotnet", [resolve(root, `tools/dotnet-runic-toolkit/bin/${configuration}/net10.0/dotnet-runic.dll`),
    "size", "--project", project, "--runtime", rid, "--host", host, "--profile", profile,
    "--report", report, "--verify", process.execPath, "--verify-argument", resolve(root, "eng/verify-published-customers.mjs")], root, env);
  const measured = JSON.parse(readFileSync(report, "utf8"));
  assert.equal(measured.publishExitCode, 0);
  assert.equal(measured.verification.status, "passed");
  const assetsPath = join(project, "../obj/project.assets.json");
  cpSync(assetsPath, join(results, `${name}.assets.json`));
  const assets = JSON.parse(readFileSync(assetsPath, "utf8"));
  for (const [name, item] of Object.entries(assets.libraries))
    if (name.startsWith("Runic.")) assert.equal(item.type, "package", `${name} is not a package consumer`);
  if (host === "cswebui") assertCsWebUiDependencies(assets);
  const providerPackages = Object.keys(assets.libraries).filter(name => /^Runic\.Platform\.(Windows|Linux|MacOS)\//.test(name));
  if (nativeProvider === "None") assert.deepEqual(providerPackages, [], "Omitted provider entered consumer dependency graph");
  else assert.deepEqual(providerPackages, [`Runic.Platform.${nativeProvider}/${workspace.version}`], "Provider graph must contain only the statically selected provider");
  metadata.cases.push({ host, profile, nativeProvider, report, totalBytes: measured.totalBytes, compressedBytes: measured.compressedBytes,
    mainExecutableBytes: measured.files.find(file => file.category === "main-executable")?.bytes,
    verification: measured.verification.status });
  writeFileSync(join(results, "matrix.json"), JSON.stringify(metadata, null, 2));
}
console.log(`Verified package-consumer matrix: ${results}`);

} finally {
  rmSync(temporary, { recursive: true, force: true });
}
