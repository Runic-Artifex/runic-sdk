import { spawn } from "node:child_process";
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url));
const sample = join(root, "examples/first-window-desktop");
const temporary = await mkdtemp(join(tmpdir(), "runic-desktop-views-package-"));
const feed = join(temporary, "feed");
const consumer = join(temporary, "consumer");
const projects = [
  ["Runic.Desktop", "Runic.Desktop"],
  ["Runic.Application", "Runic.Application.Views"],
  ["Runic.Application.ReactiveUI", "Runic.Application.Views.ReactiveUI"],
  ["Runic.Application.Desktop", "Runic.Application.Desktop"],
];
const version = JSON.parse(await readFile(join(root, "eng/workspace.json"), "utf8")).version;

function run(command, args, cwd = root, env = process.env) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, env, stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    child.stdout.on("data", chunk => { output += chunk; });
    child.stderr.on("data", chunk => { output += chunk; });
    child.on("error", reject);
    child.on("close", code => code === 0 ? resolve(output)
      : reject(new Error(`${command} ${args.join(" ")} exited ${code}:\n${output.slice(-5000)}`)));
  });
}

try {
  await run("dotnet", ["build", "examples/first-window-desktop/FirstWindowDesktop.csproj", "-c", "Release"]);
  await mkdir(feed);
  for (const [identity, project] of projects) {
    await run("dotnet", ["pack", `packages/dotnet/${project}/${project}.csproj`,
      "-c", "Release", "--no-build", "-o", feed, `-p:PackageVersion=${version}`]);
    if (identity === "Runic.Application.Desktop")
      await readFile(join(feed, `${identity}.${version}.nupkg`));
  }
  await mkdir(join(consumer, "Frontend/src"), { recursive: true });
  for (const file of ["Program.cs", "CounterWindow.cs", "CounterViewModel.cs"])
    await copyFile(join(sample, file), join(consumer, file));
  for (const file of ["package.json", "build.mjs", "index.html"])
    await copyFile(join(sample, "Frontend", file), join(consumer, "Frontend", file));
  await copyFile(join(sample, "Frontend/src/app.ts"), join(consumer, "Frontend/src/app.ts"));
  await writeFile(join(consumer, "FirstWindowDesktop.csproj"), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <RunicBridgeRegisterGlobally>false</RunicBridgeRegisterGlobally>
    <RunicBridgeCompositionType>FirstWindowDesktop.RunicBridgeComposition</RunicBridgeCompositionType>
    <RunicBridgeFrontendBuildCommand>bun run --bun build</RunicBridgeFrontendBuildCommand>
    <RestoreAdditionalProjectSources>${feed}</RestoreAdditionalProjectSources>
  </PropertyGroup>
  <PropertyGroup Condition="'$(RunicBridgeBootstrap)' == 'true'"><OutputType>Library</OutputType></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Runic.Application.Desktop" Version="${version}" />
    <PackageReference Include="Runic.Application.ReactiveUI" Version="${version}" />
  </ItemGroup>
  <ItemGroup Condition="'$(RunicBridgeBootstrap)' == 'true'"><Compile Remove="Program.cs" /></ItemGroup>
</Project>
`);
  await run("dotnet", ["build", "FirstWindowDesktop.csproj", "-c", "Release"], consumer);
  const dll = join(consumer, "bin/Release/net10.0/FirstWindowDesktop.dll");
  const owner = await run("xvfb-run", ["-a", "dotnet", dll, "--probe-owner"], consumer);
  if (!owner.includes("DESKTOP_VIEWS_OWNER_OK")) throw new Error(`Packaged Desktop owner failed:\n${owner}`);
  const browser = await run("node", [join(sample, "browser-smoke.mjs")], root,
    { ...process.env, RUNIC_DESKTOP_VIEWS_DLL: dll });
  if (!browser.includes("DESKTOP_VIEWS_BROWSER_OK")) throw new Error(`Packaged Desktop browser failed:\n${browser}`);
  console.log("DESKTOP_VIEWS_PACKAGE_OK|restore|generate|owner|browser");
} finally {
  await rm(temporary, { recursive: true, force: true });
}
