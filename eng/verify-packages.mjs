import assert from "node:assert/strict";
import {
  mkdtempSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  writeFileSync,
  realpathSync,
  rmSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { execFileSync } from "node:child_process";
import { basename, extname, resolve, join } from "node:path";
import { root, workspace, run, configuration } from "./run.mjs";

const nativeProviders = new Set([
  "runic.platform.windows",
  "runic.platform.linux",
  "runic.platform.linux.gtk4",
  "runic.platform.macos",
]);

export function dotnetBuildArguments(projectFile, selectedConfiguration = configuration, additional = []) {
  return ["build", projectFile, "--configuration", selectedConfiguration, ...additional];
}

export function resolveMsbuildPathValue(projectDirectory, value) {
  return resolve(projectDirectory, value.replaceAll("\\", "/"));
}

function msbuildProjectPath(
  projectDirectory,
  projectFile,
  property,
  selectedConfiguration = configuration,
  additional = [],
) {
  const value = execFileSync("dotnet", [
    "msbuild",
    projectFile,
    "--nologo",
    `-property:Configuration=${selectedConfiguration}`,
    ...additional,
    `-getProperty:${property}`,
  ], { cwd: projectDirectory, encoding: "utf8" }).trim();
  assert.ok(value, `${projectFile} did not report ${property} for ${selectedConfiguration}`);
  return resolveMsbuildPathValue(projectDirectory, value);
}

export function packageConsumerStrategy(packageEntry) {
  const project = readFileSync(resolve(root, packageEntry.project), "utf8");
  const frameworks = [
    ...[...project.matchAll(/<TargetFramework>([^<]+)<\/TargetFramework>/g)].flatMap(([, value]) => value.split(";")),
    ...[...project.matchAll(/<TargetFrameworks>([^<]+)<\/TargetFrameworks>/g)].flatMap(([, value]) => value.split(";")),
  ].map(value => value.trim()).filter(Boolean);
  const windowsOnly = frameworks.length > 0
    && frameworks.every(framework => /-windows(?:[0-9.]+)?$/i.test(framework));
  assert.ok(!windowsOnly, `${packageEntry.name} must declare a cross-platform consumer strategy`);
  return {
    targetFramework: "net10.0",
    execute: true,
    enableWindowsTargeting: false,
    useWpf: false,
    canaryType: undefined,
  };
}

function verifyConsumerGraph(consumer, label, { platformOnly = false, selectedProvider } = {}) {
  const assets = JSON.parse(readFileSync(join(consumer, "obj/project.assets.json"), "utf8"));
  const libraries = Object.keys(assets.libraries);
  assert.ok(Object.values(assets.libraries).every(library => library.type !== "project"),
    `${label} leaked a project reference`);
  const ownedPackages = new Set(workspace.nuget.map(packageEntry => packageEntry.name.toLowerCase()));
  for (const library of libraries.filter(name => ownedPackages.has(name.split("/")[0].toLowerCase()))) {
    assert.equal(library.split("/")[1], workspace.version, `stale internal dependency ${library}`);
  }
  // Host adapters use the DI abstractions; the application chooses and references a container.
  if (["Runic.Application.Desktop", "Runic.Application.CsWebUi"].includes(label)) {
    assert.ok(!libraries.some(library => library.split("/")[0].toLowerCase() === "microsoft.extensions.dependencyinjection"),
      `${label} unexpectedly depends on the Microsoft.Extensions.DependencyInjection container`);
  }
  const isolated = platformOnly;
  if (!isolated) return;

  // Inspect the complete restored graph, including dependencies that are not loaded by the canary.
  const allowedPlatform = new Set(["runic.platform", label.toLowerCase()]);
  if (label !== "Runic.Platform") allowedPlatform.add("runic.platform.runtime");
  if (label === "Runic.Platform.Linux") allowedPlatform.add("runic.platform.linux.portal");
  for (const library of libraries) {
    const name = library.split("/")[0].toLowerCase();
    assert.ok(!nativeProviders.has(name) || name === selectedProvider,
      `${label} unexpectedly depends on native provider ${library}`);
    assert.ok(!name.startsWith("microsoft.aspnetcore"),
      `${label} unexpectedly depends on ASP.NET Core package ${library}`);
    assert.ok(!/^runic\.(desktop|assets\.desktop)(\.|$)/.test(name),
      `${label} unexpectedly depends on Desktop package ${library}`);
    if (platformOnly) {
      assert.ok(!name.startsWith("runic.") || allowedPlatform.has(name),
        `${label} unexpectedly depends on host/application package ${library}`);
      assert.ok(name !== "cswebui", `${label} unexpectedly depends on CS-WebUI`);
    }
  }
  const targetPath = msbuildProjectPath(consumer, "Consumer.csproj", "TargetPath");
  const runtimePath = targetPath.slice(0, -extname(targetPath).length) + ".runtimeconfig.json";
  const runtime = JSON.parse(readFileSync(runtimePath, "utf8"));
  const frameworks = [runtime.runtimeOptions.framework,
    ...(runtime.runtimeOptions.frameworks ?? []), ...(runtime.runtimeOptions.includedFrameworks ?? [])];
  assert.ok(!frameworks.some(framework => framework?.name === "Microsoft.AspNetCore.App"),
    `${label} unexpectedly requires the ASP.NET Core shared framework`);
  for (const framework of Object.values(assets.project.frameworks ?? {})) {
    assert.ok(!Object.keys(framework.frameworkReferences ?? {}).includes("Microsoft.AspNetCore.App"),
      `${label} unexpectedly restores the ASP.NET Core shared framework`);
  }
}

// The consumer tree includes its own NuGet cache and package restores.
// It is removed after the run unless --keep asks to retain it for diagnosis.
export async function verifyPackages(packageName, { keep = false } = {}) {
  const directory = mkdtempSync(join(tmpdir(), "runic-sdk-consumers-"));
  console.log(`Package-only consumers: ${directory}`);
  try {
    await verifyConsumers(directory, packageName);
  } finally {
    if (keep) console.log(`Kept package-only consumers: ${directory}`);
    else rmSync(directory, { recursive: true, force: true, maxRetries: 5 });
  }
}

async function verifyConsumers(directory, packageName) {
  const nuget = resolve(root, "artifacts/packages/nuget");
  const npm = resolve(root, "artifacts/packages/npm");
  // Separate minimal consumers prevent dependencies from concealing missing
  // dependencies in standalone Application, Assets, Desktop, or Platform packages.
  const allLibraries = workspace.nuget.filter(
    (p) => !isToolPackage(p) && !p.name.endsWith(".Templates"),
  );
  const libraries = packageName
    ? allLibraries.filter(packageEntry => packageEntry.name === packageName)
    : allLibraries;
  assert.ok(!packageName || libraries.length === 1, `Unknown NuGet library consumer ${packageName}`);
  for (const p of packageName ? libraries : workspace.nuget) {
    assert.ok(
      readdirSync(nuget).includes(`${p.name}.${workspace.version}.nupkg`),
      `Pack ${p.name} first`,
    );
  }
  const archives = (packageName ? [] : workspace.npm).map((p) => {
    const file = `${p.name.replace("@", "").replace("/", "-")}-${workspace.version}.tgz`;
    assert.ok(readdirSync(npm).includes(file), `Pack ${p.name} first`);
    return [p.name, resolve(npm, file)];
  });
  // The temporary tree has no parent workspace, source references, or reusable Runic package cache.
  writeFileSync(
    join(directory, "NuGet.config"),
    `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${nuget}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate">${workspace.nuget.map(packageEntry => `<package pattern="${packageEntry.name}"/>`).join("")}</packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`,
  );
  const env = {
    NUGET_PACKAGES: join(directory, "nuget-cache"),
    DOTNET_CLI_HOME: join(directory, "dotnet-home"),
  };
  for (const p of libraries) {
    const strategy = packageConsumerStrategy(p);
    const projectText = readFileSync(resolve(root, p.project), "utf8");
    const assemblyName = projectText.match(/<AssemblyName>([^<]+)<\/AssemblyName>/)?.[1]
      ?? basename(p.project, ".csproj");
    const consumer = join(directory, p.name);
    const viewTestConsumer = p.name === "Runic.Application.Testing";
    const bridgeProperties = viewTestConsumer
      ? `<RunicBridgeBuildEnabled>true</RunicBridgeBuildEnabled><RunicBridgeRegisterGlobally>false</RunicBridgeRegisterGlobally><RunicBridgeFrontendBuildCommand>dotnet --version</RunicBridgeFrontendBuildCommand><RunicBridgeFrontendDir>$(MSBuildProjectDirectory)/Frontend</RunicBridgeFrontendDir><RunicBridgeTypescriptDir>$(RunicBridgeFrontendDir)/src/generated</RunicBridgeTypescriptDir><OutputType Condition="'$(RunicBridgeBootstrap)' == 'true'">Library</OutputType>`
      : ["Runic.Application", "Runic.Application.CsWebUi", "Runic.Application.ReactiveUI", "Runic.Application.ReactiveUI.Reactive", "Runic.Application.Desktop"].includes(p.name)
        ? "<RunicBridgeBuildEnabled>false</RunicBridgeBuildEnabled>" : "";
    mkdirSync(consumer);
    writeFileSync(
      join(consumer, "Consumer.csproj"),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>${strategy.targetFramework}</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors>${bridgeProperties}${strategy.enableWindowsTargeting ? "<EnableWindowsTargeting>true</EnableWindowsTargeting>" : ""}${strategy.useWpf ? "<UseWPF>true</UseWPF>" : ""}</PropertyGroup><ItemGroup><PackageReference Include="${p.name}" Version="${workspace.version}"/>${viewTestConsumer ? `<PackageReference Include="Runic.Application" Version="${workspace.version}"/>` : ""}</ItemGroup>${viewTestConsumer ? `<ItemGroup Condition="'$(RunicBridgeBootstrap)' == 'true'"><Compile Remove="Program.cs"/></ItemGroup>` : ""}</Project>`,
    );
    writeFileSync(
      join(consumer, "Program.cs"),
      p.name === "Runic.Application.Testing"
        ? `using Runic.Application.Testing;
using Runic.Application.Views;
using PackageConsumer;
var model = new ConsumerViewModel();
var window = new ConsumerWindow(model);
if (!ReferenceEquals(window.DataContext, model)) throw new Exception("Window DataContext was lost.");
using var host = new RunicWindowTestHost<ConsumerViewModel>(model, "consumer",
    (transport, content, vm) => new ConsumerBridge(transport, vm, content: content));
using var snapshot = host.Snapshot();
if (snapshot.RootElement.GetProperty("state").GetProperty("value").GetInt32() != 7)
    throw new Exception("The packaged headless Window snapshot failed.");
_ = host.Transport.Call("consumerSetValue", new(Int64Value: 8));
if (model.Value != 8) throw new Exception("The packaged generated setter failed.");
var published = false;
for (var attempt = 0; attempt < 100 && !published; attempt++)
{
    published = host.Transport.DrainPublications().Count > 0;
    if (!published) await Task.Delay(10);
}
if (!published) throw new Exception("The packaged generated publication failed.");
Console.WriteLine("Packaged Window/View test host passed.");`
      : strategy.canaryType
        ? `Console.WriteLine(typeof(${strategy.canaryType}).Assembly.GetName().Name);`
        : `Console.WriteLine(System.Reflection.Assembly.Load("${assemblyName}").GetName().Name);`,
    );
    if (viewTestConsumer)
      writeFileSync(join(consumer, "TestModel.cs"), `#nullable enable
using System.ComponentModel;
using Runic.Application.Views;
namespace PackageConsumer;
public sealed partial class ConsumerWindow(ConsumerViewModel model) : RunicWindow<ConsumerViewModel>(model);
public sealed class ConsumerViewModel : INotifyPropertyChanged
{
    private int _value = 7;
    public int Value { get => _value; set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}`);
    run("dotnet", strategy.execute
      ? ["run", "--project", "Consumer.csproj", "--configuration", configuration]
      : ["build", "Consumer.csproj", "--configuration", configuration], consumer, env);
    verifyConsumerGraph(consumer, p.name, {
      platformOnly: p.name === "Runic.Platform" || p.name.startsWith("Runic.Platform."),
      selectedProvider: nativeProviders.has(p.name.toLowerCase()) ? p.name.toLowerCase() : undefined,
    });
  }
  if (packageName) {
    console.log(`Packed ${packageName} consumer passed.`);
    return;
  }
  await verifyToolAndTemplatePackages(directory, nuget, env);
  const frontend = join(directory, "frontend");
  mkdirSync(frontend);
  writeFileSync(
    join(frontend, "package.json"),
    JSON.stringify(
      {
        private: true,
        type: "module",
        dependencies: {
          ...Object.fromEntries(
            archives.map(([name, file]) => [name, `file:${file}`]),
          ),
          typescript: "6.0.3",
          "@types/node": "24.19.1",
          effect: "4.0.1",
          svelte: "5.57.1",
          vite: "8.3.2",
          "@angular/core": "22.2.1",
          react: "19.3.0",
          "@types/react": "19.3.0",
          vue: "3.5.43",
          "@sveltejs/kit": "3.0.0",
          "@sveltejs/adapter-static": "4.0.0",
          "@sveltejs/vite-plugin-svelte": "7.3.1",
        },
      },
      null,
      2,
    ),
  );
  run(
    "npm",
    [
      "install",
      "--ignore-scripts",
      "--no-audit",
      "--no-fund",
      "--package-lock=false",
    ],
    frontend,
  );
  for (const [name] of archives) {
    const path = realpathSync(join(frontend, "node_modules", name));
    assert.ok(
      path.startsWith(frontend),
      `${name} is linked to the source workspace`,
    );
    const manifest = JSON.parse(
      readFileSync(join(path, "package.json"), "utf8"),
    );
    for (const version of Object.values({
      ...manifest.dependencies,
      ...manifest.peerDependencies,
    })) {
      assert.ok(
        !/^(workspace:|file:|link:)/.test(version),
        `${name} has an unpublished dependency ${version}`,
      );
    }
  }
  writeFileSync(
    join(frontend, "consumer.mjs"),
    `
import assert from 'node:assert/strict';
import * as vite from '@runic-artifex/vite-plugin-runic';
import { runicSpaPageOptions } from '@runic-artifex/sveltekit/page-options';
import { BridgeError } from '@runic-artifex/views';
import { connectView } from '@runic-artifex/views/generated';
import * as bridgeWire from '@runic-artifex/views/generated/wire';
import { installMockBridge } from '@runic-artifex/views/mock';
import { useView as useReactView } from '@runic-artifex/react';
import { useView as useVueView } from '@runic-artifex/vue';
import { command } from '@runic-artifex/views-effect';
import { Effect } from 'effect';
assert.equal(runicSpaPageOptions.ssr, false);
assert.ok(Object.keys(vite).length);
installMockBridge().view('consumer', { state: { value: 1 } });
const client = await connectView({ contract: 'Consumer:fingerprint', route: 'consumer', mount: false,
  hydrate: wire => ({ value: bridgeWire.integer(wire.value, 0, 9) }) });
assert.equal(client.snapshot.value, 1);
client.dispose();
await assert.rejects(client.invoke('consumerSetValue', 2), error => error instanceof BridgeError && error.kind === 'disconnected');
assert.equal((await Effect.runPromise(Effect.flip(command(() => client.invoke('consumerSetValue', 2)))))._tag, 'ViewDisconnected');
for (const hook of [useReactView, useVueView]) assert.equal(typeof hook, 'function');
console.log('Packed npm consumers passed.');
`,
  );
  run("node", ["consumer.mjs"], frontend);
  writeFileSync(
    join(frontend, "consumer.ts"),
    archives
      .map(([name], index) => `import * as package${index} from '${name}';`)
      .join("\n") +
      '\n',
  );
  writeFileSync(
    join(frontend, "tsconfig.json"),
    JSON.stringify({
      compilerOptions: {
        target: "ES2022",
        module: "NodeNext",
        moduleResolution: "NodeNext",
        strict: true,
        noEmit: true,
        skipLibCheck: true,
      },
      include: ["consumer.ts"],
    }),
  );
  run(
    "node",
    ["node_modules/typescript/bin/tsc", "-p", "tsconfig.json"],
    frontend,
  );
  console.log(
    `All ${libraries.length} NuGet library consumers, CS-WebUI/Platform composition, 2 tools, 1 template package, and ${archives.length} npm artifacts passed.`,
  );
}


// .NET tools install with dotnet tool, not as package references.
function isToolPackage(packageEntry) {
  return /<PackAsTool>true<\/PackAsTool>/.test(readFileSync(resolve(root, packageEntry.project), "utf8"));
}

export async function verifyToolAndTemplatePackages(directory, nuget, env) {
  const toolPath = join(directory, "tools");
  const config = join(directory, "NuGet.config");
  for (const [id, command] of [
    ["dotnet-runic", "dotnet-runic"],
    ["Runic.Create", "runic-create"],
  ]) {
    run(
      "dotnet",
      [
        "tool",
        "install",
        id,
        "--version",
        workspace.version,
        "--tool-path",
        toolPath,
        "--configfile",
        config,
      ],
      directory,
      env,
    );
    run(
      join(toolPath, command + (process.platform === "win32" ? ".exe" : "")),
      ["--help"],
      directory,
      env,
    );
  }
  for (const name of ["Runic.Application.Templates"]) {
    run(
      "dotnet",
      [
        "new",
        "install",
        join(nuget, `${name}.${workspace.version}.nupkg`),
        "--force",
      ],
      directory,
      env,
    );
  }
  // Confirm the installed package generates an application and its candidate
  // tool manifest restores. CI's template matrix covers application builds.
  const application = join(directory, "application-template");
  run("dotnet", ["new", "runic-app", "--name", "ApplicationConsumer", "--output", application], directory, env);
  assert.ok(readFileSync(join(application, "ApplicationConsumer.csproj"), "utf8").includes("Runic.Application"),
    "Installed application template did not generate the application project");
  run("dotnet", ["tool", "restore", "--configfile", config], application, env);
}
