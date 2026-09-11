import assert from "node:assert/strict";
import {
  mkdtempSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  writeFileSync,
  realpathSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { resolve, join } from "node:path";
import { root, workspace, run, configuration } from "./run.mjs";

const nativeProviders = new Set([
  "runic.platform.windows",
  "runic.platform.linux",
  "runic.platform.linux.gtk4",
  "runic.platform.macos",
]);

function verifyConsumerGraph(consumer, label, { platformOnly = false, desktop = false, selectedProvider } = {}) {
  const assets = JSON.parse(readFileSync(join(consumer, "obj/project.assets.json"), "utf8"));
  const libraries = Object.keys(assets.libraries);
  assert.ok(Object.values(assets.libraries).every(library => library.type !== "project"),
    `${label} leaked a project reference`);
  for (const library of libraries.filter(name => name.toLowerCase().startsWith("runic."))) {
    assert.equal(library.split("/")[1], workspace.version, `stale internal dependency ${library}`);
  }
  const isolated = platformOnly || label === "Runic.Application.Platform"
    || label === "Runic.Application.Platform.Desktop" || label === "Runic.Application.CsWebUi"
    || label === "CS-WebUI with Platform";
  if (!isolated) return;

  // Inspect the complete restored graph, including dependencies that are not loaded by the canary.
  const allowedPlatform = new Set(["runic.platform", label.toLowerCase()]);
  if (label !== "Runic.Platform") allowedPlatform.add("runic.platform.runtime");
  if (label === "Runic.Platform.Linux") allowedPlatform.add("runic.platform.linux.portal");
  for (const library of libraries) {
    const name = library.split("/")[0].toLowerCase();
    assert.ok(!nativeProviders.has(name) || name === selectedProvider,
      `${label} unexpectedly depends on native provider ${library}`);
    assert.ok(desktop || !name.startsWith("microsoft.aspnetcore"),
      `${label} unexpectedly depends on ASP.NET Core package ${library}`);
    if (!desktop) {
      assert.ok(!/^runic\.(desktop|application\.desktop|application\.platform\.desktop|assets\.desktop)(\.|$)/.test(name),
        `${label} unexpectedly depends on Desktop package ${library}`);
    }
    if (platformOnly) {
      assert.ok(!name.startsWith("runic.") || allowedPlatform.has(name),
        `${label} unexpectedly depends on host/application package ${library}`);
      assert.ok(name !== "cswebui", `${label} unexpectedly depends on CS-WebUI`);
    }
  }
  // Desktop currently brings its ASP.NET Core server framework; its optional adapter may do so too.
  if (desktop) return;
  const runtime = JSON.parse(readFileSync(join(consumer, `bin/${configuration}/net10.0/Consumer.runtimeconfig.json`), "utf8"));
  const frameworks = [runtime.runtimeOptions.framework,
    ...(runtime.runtimeOptions.frameworks ?? []), ...(runtime.runtimeOptions.includedFrameworks ?? [])];
  assert.ok(!frameworks.some(framework => framework?.name === "Microsoft.AspNetCore.App"),
    `${label} unexpectedly requires the ASP.NET Core shared framework`);
  for (const framework of Object.values(assets.project.frameworks ?? {})) {
    assert.ok(!Object.keys(framework.frameworkReferences ?? {}).includes("Microsoft.AspNetCore.App"),
      `${label} unexpectedly restores the ASP.NET Core shared framework`);
  }
}

export async function verifyPackages() {
  const directory = mkdtempSync(join(tmpdir(), "runic-sdk-consumers-"));
  console.log(`Package-only consumers: ${directory}`);
  const nuget = resolve(root, "artifacts/packages/nuget");
  const npm = resolve(root, "artifacts/packages/npm");
  for (const p of workspace.nuget) {
    assert.ok(
      readdirSync(nuget).includes(`${p.name}.${workspace.version}.nupkg`),
      `Pack ${p.name} first`,
    );
  }
  const archives = workspace.npm.map((p) => {
    const file = `${p.name.replace("@", "").replace("/", "-")}-${workspace.version}.tgz`;
    assert.ok(readdirSync(npm).includes(file), `Pack ${p.name} first`);
    return [p.name, resolve(npm, file)];
  });
  // The temporary tree has no parent workspace, source references, or reusable Runic package cache.
  writeFileSync(
    join(directory, "NuGet.config"),
    `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${nuget}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/><package pattern="dotnet-runic*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`,
  );
  const env = {
    NUGET_PACKAGES: join(directory, "nuget-cache"),
    DOTNET_CLI_HOME: join(directory, "dotnet-home"),
  };
  // Separate minimal consumers prevent Application dependencies from concealing missing dependencies
  // in standalone CommandLine, Assets, Desktop, or Translations packages.
  const libraries = workspace.nuget.filter(
    (p) => !p.name.startsWith("dotnet-") && !p.name.endsWith(".Templates"),
  );
  for (const p of libraries) {
    const consumer = join(directory, p.name);
    mkdirSync(consumer);
    writeFileSync(
      join(consumer, "Consumer.csproj"),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><PackageReference Include="${p.name}" Version="${workspace.version}"/>${p.name === "Runic.Translations.Build" ? `<PackageReference Include="Runic.Translations" Version="${workspace.version}"/>` : ""}</ItemGroup></Project>`,
    );
    writeFileSync(
      join(consumer, "Program.cs"),
      p.name.endsWith(".Build")
        ? 'Console.WriteLine("Translation build targets restored.");'
        : `Console.WriteLine(System.Reflection.Assembly.Load("${p.name}").GetName().Name);`,
    );
    if (p.name === "Runic.Translations.Build") {
      // Exercise the packaged analyzer and its parser without a CLI or source reference.
      const resources = join(consumer, "translations");
      mkdirSync(resources);
      writeFileSync(join(resources, "runic.json"), JSON.stringify({
        schemaVersion: 1, sourceLayout: "locale-toml", catalog: "canary",
        code: { namespace: "PackageCanary", className: "CanaryText" },
        baseLocale: "en", locales: ["en"],
      }));
      writeFileSync(join(resources, "en.toml"), "Greeting = '''\n.input {$name :string}\nHello {$name}\n'''\n");
      writeFileSync(join(consumer, "Program.cs"),
        'var manager = await PackageCanary.CanaryTextCatalog.CreateManagerAsync();\n' +
        'var text = new PackageCanary.CanaryText(manager);\n' +
        'if (text.Greeting("Ada") != "Hello Ada") throw new Exception("Packaged TOML accessor failed");\n' +
        'Console.WriteLine("Packaged TOML analyzer and runtime passed.");\n');
    }
    if (
      [
        "Runic.Application",
        "Runic.Application.Hosting",
        "Runic.Application.Desktop",
        "Runic.Application.CsWebUi",
        "Runic.Application.Testing",
      ].includes(p.name)
    ) {
      const program = join(consumer, "Program.cs");
      writeFileSync(
        program,
        '[assembly: Runic.Application.RunicApplicationManifest("package-canary")]\n' +
          readFileSync(program, "utf8"),
      );
    }
    run("dotnet", ["run", "--project", "Consumer.csproj", "--configuration", configuration], consumer, env);
    verifyConsumerGraph(consumer, p.name, {
      platformOnly: p.name === "Runic.Platform" || p.name.startsWith("Runic.Platform."),
      desktop: p.name === "Runic.Application.Platform.Desktop",
      selectedProvider: nativeProviders.has(p.name.toLowerCase()) ? p.name.toLowerCase() : undefined,
    });
  }
  // Composition must retain isolation too: resolve the shared services through the public API
  // alongside CS-WebUI without creating a native window or using workspace project references.
  const sharedConsumer = join(directory, "cswebui-platform");
  mkdirSync(sharedConsumer);
  writeFileSync(join(sharedConsumer, "Consumer.csproj"),
    `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.CsWebUi" Version="${workspace.version}"/><PackageReference Include="Runic.Application.Platform" Version="${workspace.version}"/></ItemGroup></Project>`);
  writeFileSync(join(sharedConsumer, "Program.cs"), `
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Platform;
using Runic.Platform;
[assembly: Runic.Application.RunicApplicationManifest("cswebui-platform-canary")]
var services = new ServiceCollection();
services.AddRunicPlatform();
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var presentation = scope.ServiceProvider.GetRequiredService<PlatformPresentation>();
var files = scope.ServiceProvider.GetRequiredService<IFileDialogs>();
var clipboard = scope.ServiceProvider.GetRequiredService<ITextClipboard>();
var capabilities = scope.ServiceProvider.GetRequiredService<IPlatformCapabilities>();
var dispatcher = scope.ServiceProvider.GetRequiredService<IUiDispatcher>();
if (await files.OpenFileAsync(new()) is not PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.ProviderNotConfigured }
    || await clipboard.ReadTextAsync(32) is not PlatformResult<string?>.Unavailable { Reason: UnavailableReason.ProviderNotConfigured }
    || capabilities.GetSnapshot().Statuses.Values.Any(status => status is not CapabilityStatus.Unavailable)
    || dispatcher.CheckAccess())
    throw new InvalidOperationException("Unconfigured shared platform services must report unavailable.");
await presentation.StopAsync();
Console.WriteLine(typeof(Runic.Application.CsWebUi.CsWebUiApplicationHost).Assembly.GetName().Name);
Console.WriteLine("CS-WebUI and shared Platform public API composition passed.");
`);
  run("dotnet", ["run", "--project", "Consumer.csproj", "--configuration", configuration], sharedConsumer, env);
  verifyConsumerGraph(sharedConsumer, "CS-WebUI with Platform");
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
          "@types/node": "24.10.12",
          effect: "4.0.0-rc.112",
          svelte: "5.57.0",
          vite: "8.2.2",
          "@angular/core": "22.1.5",
          "@sveltejs/kit": "2.70.2",
          "@sveltejs/adapter-static": "3.0.10",
          "@sveltejs/vite-plugin-svelte": "7.3.0",
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
import { defineApplicationBridgeContract } from '@runic-artifex/application-bridge';
import * as compiler from '@runic-artifex/application-bridge-tooling';
import * as desktop from '@runic-artifex/desktop';
import * as vite from '@runic-artifex/vite-plugin-runic';
import * as translations from '@runic-artifex/vite-plugin-runic-translations';
import { createViteApplicationBridgeObserver } from '@runic-artifex/svelte/vite';
import { runicToolkitSpaPageOptions } from '@runic-artifex/sveltekit/page-options';
assert.equal(typeof defineApplicationBridgeContract, 'function');
assert.equal(typeof createViteApplicationBridgeObserver, 'function');
assert.equal(runicToolkitSpaPageOptions.ssr, false);
for (const module of [compiler, desktop, vite, translations]) assert.ok(Object.keys(module).length);
console.log('Packed npm consumers passed.');
`,
  );
  run("node", ["consumer.mjs"], frontend);
  writeFileSync(
    join(frontend, "consumer.ts"),
    archives
      .map(([name], index) => `import * as package${index} from '${name}';`)
      .join("\n") +
      '\nimport type { RunicLocaleCookieOptions } from "@runic-artifex/sveltekit/translations";\nconst cookie: RunicLocaleCookieOptions = { httpOnly: true, sameSite: "lax" };\n',
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
    `All ${libraries.length} NuGet library consumers, CS-WebUI/Platform composition, 2 tools, 2 template packages, and 8 npm artifacts passed.`,
  );
}

export async function verifyToolAndTemplatePackages(directory, nuget, env) {
  const toolPath = join(directory, "tools");
  const config = join(directory, "NuGet.config");
  for (const [id, command] of [
    ["dotnet-runic", "dotnet-runic"],
    ["dotnet-runic-translations", "runic-translations"],
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
  for (const name of [
    "Runic.Application.Templates",
    "Runic.Translations.Templates",
  ]) {
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
  const translations = join(directory, "translation-template");
  run(
    "dotnet",
    [
      "new",
      "runic-translations-project",
      "--name",
      "TranslationConsumer",
      "--output",
      translations,
    ],
    directory,
    env,
  );
  run("dotnet", ["tool", "restore", "--configfile", config], translations, env);
  run(
    "dotnet",
    ["build", "TranslationConsumer.csproj", "--nologo"],
    translations,
    env,
  );
}
