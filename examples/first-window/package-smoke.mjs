import { spawn } from "node:child_process";
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { homedir, tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url));
const example = join(root, "examples/first-window");
const temporary = await mkdtemp(join(tmpdir(), "runic-views-package-"));
const feed = join(temporary, "feed");
const consumer = join(temporary, "consumer");
const packageProjects = [
  ["Runic.Application", "Runic.Application.Views"],
  ["Runic.Application.CsWebUi", "Runic.Application.Views.CsWebUi"]
];
let testVersion;

function run(command, args, cwd = root, env = process.env) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, env, stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    child.stdout.on("data", chunk => { output += chunk; });
    child.stderr.on("data", chunk => { output += chunk; });
    child.on("error", reject);
    child.on("close", code => code === 0
      ? resolve(output)
      : reject(new Error(`${command} ${args.join(" ")} exited ${code}:\n${output.slice(-5000)}`)));
  });
}

try {
  const versions = await readFile(join(root, "eng/Versions.props"), "utf8");
  const packages = await readFile(join(root, "Directory.Packages.props"), "utf8");
  const baseVersion = versions.match(/<RunicSdkVersion>([^<]+)<\/RunicSdkVersion>/)?.[1];
  const toolkit = packages.match(/<PackageVersion Include="CommunityToolkit.Mvvm" Version="([^"]+)"/)?.[1];
  if (!baseVersion || !toolkit) throw new Error("SDK or CommunityToolkit version was not found.");
  testVersion = `${baseVersion}.packagetest${process.pid}${Date.now()}`;

  await run("dotnet", ["build", "examples/first-window/FirstWindow.csproj", "-c", "Release"]);
  await mkdir(feed);
  for (const [, project] of packageProjects) {
    await run("dotnet", ["pack", `packages/dotnet/${project}/${project}.csproj`,
      "-c", "Release", "--no-build", "-o", feed,
      `-p:Version=${testVersion}`, `-p:PackageVersion=${testVersion}`]);
  }

  await mkdir(join(consumer, "Frontend/src"), { recursive: true });
  for (const file of ["Program.cs", "CounterWindow.cs", "CounterViewModel.cs"])
    await copyFile(join(example, file), join(consumer, file));
  for (const file of ["package.json", "bun.lock", "tsconfig.json", "index.html", "copy-static.mjs"])
    await copyFile(join(example, "Frontend", file), join(consumer, "Frontend", file));
  await copyFile(join(example, "Frontend/src/app.ts"), join(consumer, "Frontend/src/app.ts"));
  await writeFile(join(consumer, "ConsumerFirstWindow.csproj"), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RunicBridgeRegisterGlobally>false</RunicBridgeRegisterGlobally>
    <RunicBridgeCompositionType>FirstWindow.RunicBridgeComposition</RunicBridgeCompositionType>
    <RunicBridgeFrontendBuildCommand>bun run --bun build</RunicBridgeFrontendBuildCommand>
    <RestoreAdditionalProjectSources>${feed}</RestoreAdditionalProjectSources>
  </PropertyGroup>
  <PropertyGroup Condition="'$(RunicBridgeBootstrap)' == 'true'"><OutputType>Library</OutputType></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="${toolkit}" />
    <PackageReference Include="Runic.Application.CsWebUi" Version="${testVersion}" />
  </ItemGroup>
  <ItemGroup Condition="'$(RunicBridgeBootstrap)' == 'true'"><Compile Remove="Program.cs" /></ItemGroup>
</Project>
`);
  await run("bun", ["install", "--frozen-lockfile"], join(consumer, "Frontend"));
  const build = await run("dotnet", ["build", "ConsumerFirstWindow.csproj", "-c", "Release"], consumer);
  if (!build.includes("Generated Counter bridge") || !build.includes("0 Error(s)"))
    throw new Error(`The package consumer did not generate its contract cleanly:\n${build}`);
  const failedConstruction = await run("dotnet", [
    join(consumer, "bin/Release/net10.0/ConsumerFirstWindow.dll"), "--probe-factory-failure"
  ], consumer);
  if (!failedConstruction.includes("FIRST_WINDOW_FACTORY_FAILURE_OK"))
    throw new Error(`The packaged Window factory did not release its failed construction scope:\n${failedConstruction}`);
  const closedWindow = await run("dotnet", [
    join(consumer, "bin/Release/net10.0/ConsumerFirstWindow.dll"), "--probe-window-close"
  ], consumer);
  if (!closedWindow.includes("FIRST_WINDOW_CLOSE_OK"))
    throw new Error(`The packaged Window did not close its host cleanly:\n${closedWindow}`);
  const browser = await run("node", [join(example, "browser-smoke.mjs")], root, {
    ...process.env,
    RUNIC_FIRST_WINDOW_DLL: join(consumer, "bin/Release/net10.0/ConsumerFirstWindow.dll")
  });
  if (!browser.includes("FIRST_WINDOW_OK")) throw new Error(`Browser journey failed:\n${browser}`);
  console.log("FIRST_WINDOW_PACKAGE_OK|pack|restore|generate|failed-construction|close|browser");
} finally {
  await rm(temporary, { recursive: true, force: true });
  if (testVersion) {
    const cache = process.env.NUGET_PACKAGES ?? join(homedir(), ".nuget/packages");
    for (const [packageId] of packageProjects)
      await rm(join(cache, packageId.toLowerCase(), testVersion.toLowerCase()), { recursive: true, force: true });
  }
}
