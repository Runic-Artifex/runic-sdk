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
import { execFileSync } from "node:child_process";
import { extname, relative, resolve, join } from "node:path";
import { root, workspace, run, configuration } from "./run.mjs";

const nativeProviders = new Set([
  "runic.platform.windows",
  "runic.platform.linux",
  "runic.platform.linux.gtk4",
  "runic.platform.macos",
]);

const platformPackageConsumers = new Map([
  ["Runic.Translations.Wpf", {
    targetFramework: "net10.0-windows",
    runtimePlatform: "win32",
    useWpf: true,
    canaryType: "Runic.Translations.Wpf.WpfInlineRenderer",
  }],
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

function moduleSpecifier(directory, path) {
  const value = relative(directory, path).replaceAll("\\", "/");
  return value.startsWith(".") ? value : `./${value}`;
}

export function packageConsumerStrategy(packageEntry, platform = process.platform) {
  const project = readFileSync(resolve(root, packageEntry.project), "utf8");
  const frameworks = [
    ...[...project.matchAll(/<TargetFramework>([^<]+)<\/TargetFramework>/g)].flatMap(([, value]) => value.split(";")),
    ...[...project.matchAll(/<TargetFrameworks>([^<]+)<\/TargetFrameworks>/g)].flatMap(([, value]) => value.split(";")),
  ].map(value => value.trim()).filter(Boolean);
  const windowsOnly = frameworks.length > 0
    && frameworks.every(framework => /-windows(?:[0-9.]+)?$/i.test(framework));
  const declared = platformPackageConsumers.get(packageEntry.name);
  assert.equal(Boolean(declared), windowsOnly,
    windowsOnly
      ? `${packageEntry.name} targets only Windows and needs an explicit package consumer strategy`
      : `${packageEntry.name} declares a Windows package consumer strategy but is not Windows-only`);
  if (!declared) return {
    targetFramework: "net10.0",
    execute: true,
    enableWindowsTargeting: false,
    useWpf: false,
    canaryType: undefined,
  };
  assert.ok(frameworks.includes(declared.targetFramework),
    `${packageEntry.name} package consumer target ${declared.targetFramework} does not match ${frameworks.join(", ")}`);
  return {
    ...declared,
    execute: platform === declared.runtimePlatform,
    enableWindowsTargeting: true,
  };
}

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

export async function verifyPackages(packageName) {
  const directory = mkdtempSync(join(tmpdir(), "runic-sdk-consumers-"));
  console.log(`Package-only consumers: ${directory}`);
  const nuget = resolve(root, "artifacts/packages/nuget");
  const npm = resolve(root, "artifacts/packages/npm");
  // Separate minimal consumers prevent Application dependencies from concealing missing dependencies
  // in standalone CommandLine, Assets, Desktop, or Translations packages.
  const allLibraries = workspace.nuget.filter(
    (p) => !p.name.startsWith("dotnet-") && !p.name.endsWith(".Templates"),
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
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/><package pattern="dotnet-runic*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`,
  );
  const env = {
    NUGET_PACKAGES: join(directory, "nuget-cache"),
    DOTNET_CLI_HOME: join(directory, "dotnet-home"),
  };
  for (const p of libraries) {
    const strategy = packageConsumerStrategy(p);
    const consumer = join(directory, p.name);
    mkdirSync(consumer);
    writeFileSync(
      join(consumer, "Consumer.csproj"),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>${strategy.targetFramework}</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors>${strategy.enableWindowsTargeting ? "<EnableWindowsTargeting>true</EnableWindowsTargeting>" : ""}${strategy.useWpf ? "<UseWPF>true</UseWPF>" : ""}</PropertyGroup><ItemGroup><PackageReference Include="${p.name}" Version="${workspace.version}"/>${p.name === "Runic.Translations.Build" ? `<PackageReference Include="Runic.Translations" Version="${workspace.version}"/>` : ""}</ItemGroup></Project>`,
    );
    writeFileSync(
      join(consumer, "Program.cs"),
      p.name === "Runic.Translations.Tooling"
        ? `using Runic.Translations.Compiler;
using Runic.Translations.Tooling;
using System.Text;
var project = new TranslationSource("translations/runic.json", Encoding.UTF8.GetBytes("""{"schemaVersion":1,"catalog":"canary","code":{"namespace":"Canary","className":"Text"},"baseLocale":"en","locales":["en",{"tag":"de","fallback":"en"}]}"""));
TranslationSource[] messages = [
  new("translations/en/greeting.mf2", Encoding.UTF8.GetBytes("Hello")),
  new("translations/de/greeting.mf2", Encoding.UTF8.GetBytes("Hallo")),
];
Rmf2ProjectCompilationV5 compiled = TranslationCompiler.CompileMf2Project(project, messages);
if (!compiled.Success || compiled.CatalogId != "canary" || compiled.Locales.Count != 2 || compiled.CallerFingerprint is null || compiled.SourceHash is null) throw new Exception("Packaged public v5 compiler contract failed.");
TranslationXliffExportResult exported = TranslationInterchange.ExportXliff21(compiled);
if (exported.Documents.Count != 1 || !exported.Report.IsLossless) throw new Exception("Packaged public v5 XLIFF export failed.");
TranslationXliffImportResult imported = TranslationInterchange.ImportXliff21(exported.Documents[0].Bytes);
if (imported.Messages.Count != 1 || imported.CatalogId != "canary" || imported.TargetLocale != "de") throw new Exception("Packaged public v5 XLIFF import failed.");
Rmf2ProjectCompilationV5 grouped = TranslationCompiler.CompileProject(project, [
  new("translations/en.rmf2", Encoding.UTF8.GetBytes("greeting = Hello")),
  new("translations/de.rmf2", Encoding.UTF8.GetBytes("greeting = Hallo")),
], null, CancellationToken.None);
if (!grouped.Success || TranslationInterchange.ExportXliff21(grouped).Documents.Count != 1) throw new Exception("Packaged grouped RMF2 compiler and XLIFF export failed.");
using var canceled = new CancellationTokenSource();
canceled.Cancel();
try { _ = TranslationCompiler.CompileMf2Project(project, messages, null, canceled.Token); throw new Exception("Packaged compiler ignored cancellation."); }
catch (OperationCanceledException) { }
Console.WriteLine("Packaged public v5 compiler and XLIFF export passed.");`
        : p.name.endsWith(".Build")
        ? 'Console.WriteLine("Translation build targets restored.");'
        : strategy.useWpf
        ? `_ = new ${strategy.canaryType}("""{"version":1,"contracts":{},"messages":{}}""", _ => { });\nConsole.WriteLine(typeof(${strategy.canaryType}).Assembly.GetName().Name);`
        : strategy.canaryType
        ? `Console.WriteLine(typeof(${strategy.canaryType}).Assembly.GetName().Name);`
        : `Console.WriteLine(System.Reflection.Assembly.Load("${p.name}").GetName().Name);`,
    );
    if (p.name === "Runic.Translations.Build") {
      // Exercise the packaged analyzer without a CLI or source reference.
      const resources = join(consumer, "translations");
      mkdirSync(resources);
      writeFileSync(join(resources, "runic.json"), JSON.stringify({
        schemaVersion: 1, catalog: "canary",
        code: { namespace: "PackageCanary", className: "CanaryText" },
        baseLocale: "en", locales: ["en"],
      }));
      writeFileSync(join(resources, "en.rmf2"), "Greeting =\n  .input {$name :string}\n  {{Hello {$name}}}\n");
      writeFileSync(join(consumer, "Program.cs"),
        'var manager = await PackageCanary.CanaryTextCatalog.CreateManagerAsync();\n' +
        'var text = new PackageCanary.CanaryText(manager);\n' +
        'if (text.r_4772656574696e67("Ada") != "Hello Ada") throw new Exception("Packaged RMF2 accessor failed");\n' +
        'Console.WriteLine("Packaged RMF2 analyzer and runtime passed.");\n');
    }
    if (
      [
        "Runic.Application",
        "Runic.Application.Hosting",
        "Runic.Application.Desktop",
        "Runic.Application.CsWebUi",
        "Runic.Application.Testing",
        "Runic.Application.Platform",
        "Runic.Application.Platform.Desktop",
      ].includes(p.name)
    ) {
      const program = join(consumer, "Program.cs");
      writeFileSync(
        program,
        '[assembly: Runic.Application.RunicApplicationManifest("package-canary")]\n' +
          readFileSync(program, "utf8"),
      );
    }
    run("dotnet", strategy.execute
      ? ["run", "--project", "Consumer.csproj", "--configuration", configuration]
      : ["build", "Consumer.csproj", "--configuration", configuration], consumer, env);
    verifyConsumerGraph(consumer, p.name, {
      platformOnly: p.name === "Runic.Platform" || p.name.startsWith("Runic.Platform."),
      desktop: p.name === "Runic.Application.Platform.Desktop",
      selectedProvider: nativeProviders.has(p.name.toLowerCase()) ? p.name.toLowerCase() : undefined,
    });
  }
  if (packageName) {
    if (packageName === "Runic.Translations.Build")
      await verifyRmf2Consumer(directory, nuget, env);
    console.log(`Packed ${packageName} consumer passed.`);
    return;
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
  await verifyRmf2Consumer(directory, nuget, env);
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
  await verifyRmf2SvelteConsumer(frontend, directory);
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

async function verifyRmf2Consumer(directory, nuget, env) {
  for (const name of ["Runic.Translations", "Runic.Translations.Build", "dotnet-runic-translations"])
    assert.ok(readdirSync(nuget).includes(`${name}.${workspace.version}.nupkg`), `Pack ${name} first`);
  const consumer = join(directory, "rmf2-consumer");
  mkdirSync(join(consumer, "translations"), { recursive: true });
  mkdirSync(join(consumer, ".config"), { recursive: true });
  writeFileSync(join(consumer, ".config", "dotnet-tools.json"), JSON.stringify({
    version: 1,
    isRoot: true,
    tools: { "dotnet-runic-translations": { version: workspace.version, commands: ["runic-translations"] } },
  }, null, 2));
  writeFileSync(join(consumer, "Consumer.csproj"),
    `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><IsAotCompatible>true</IsAotCompatible><JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault><TranslationsGenerateOnBuild>true</TranslationsGenerateOnBuild><TranslationsEmitJson>true</TranslationsEmitJson><TranslationsEmitEsm>true</TranslationsEmitEsm></PropertyGroup><ItemGroup><PackageReference Include="Runic.Translations" Version="${workspace.version}"/><PackageReference Include="Runic.Translations.Build" Version="${workspace.version}" PrivateAssets="all"/></ItemGroup></Project>`);
  writeFileSync(join(consumer, "translations", "runic.json"), JSON.stringify({
    schemaVersion: 1,
    catalog: "checkout",
    code: { namespace: "PackageRmf2", className: "CheckoutText" },
    baseLocale: "en",
    locales: ["en", "de"],
  }, null, 2));
  writeFileSync(join(consumer, "translations", "en.rmf2"), "application_title = RMF2 checkout\n");
  writeFileSync(join(consumer, "translations", "de.rmf2"), "application_title = RMF2 Kasse\n");
  writeFileSync(join(consumer, "Program.cs"),
    'using PackageRmf2;\n' +
    'using Runic.Translations;\n' +
    'using System.Text;\n' +
    'if (args.Length != 1) throw new ArgumentException("Expected the generated v5 locale artifact path.");\n' +
    'var manager = await CheckoutTextCatalog.CreateManagerAsync();\n' +
    'var text = new CheckoutText(manager);\n' +
    'if (text.r_6170706c69636174696f6e5f7469746c65 != "RMF2 checkout") throw new Exception("RMF2 v5 C# accessor failed");\n' +
    'await manager.SetLocaleAsync("de");\n' +
    'if (text.r_6170706c69636174696f6e5f7469746c65 != "RMF2 Kasse") throw new Exception("RMF2 v5 locale switch failed");\n' +
    'if (CheckoutTextCatalog.Rmf2Profile != "rmf2-execution-v2" || CheckoutTextCatalog.Rmf2RuntimeAbiVersion != 2 || CheckoutTextCatalog.MessageGrammarVersion != 5) throw new Exception("RMF2 v5 generated contract failed");\n' +
    'var source = new FilePackSource(args[0]);\n' +
    'var verified = await CheckoutTextCatalog.LoadExternalPackAsync(source, "de");\n' +
    'if (verified is null || verified.Messages.Count != 1) throw new Exception("RMF2 v5 external pack verification failed");\n' +
    'var externalManager = await CheckoutTextCatalog.CreateExternalManagerAsync(source, "de");\n' +
    'if (new CheckoutText(externalManager).r_6170706c69636174696f6e5f7469746c65 != "External RMF2 Kasse") throw new Exception("RMF2 v5 external pack composition failed");\n' +
    'Console.WriteLine("RMF2 v5 package consumer and external pack passed.");\n' +
    'sealed class FilePackSource(string path) : IExternalTranslationSource\n' +
    '{\n' +
    '  public async ValueTask<ExternalTranslationPack?> LoadAsync(string catalog, string locale, CancellationToken cancellationToken)\n' +
    '  {\n' +
    '    if (catalog != "checkout" || locale != "de") return null;\n' +
    '    var generated = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, cancellationToken));\n' +
    '    var external = generated.Replace("\\\"value\\\":\\\"RMF2 Kasse\\\"", "\\\"value\\\":\\\"External RMF2 Kasse\\\"", StringComparison.Ordinal);\n' +
    '    if (external == generated) throw new InvalidOperationException("Generated v5 pack did not contain the expected value.");\n' +
    '    return new ExternalTranslationPack(Encoding.UTF8.GetBytes(external));\n' +
    '  }\n' +
    '}\n');
  run("dotnet", ["tool", "restore", "--configfile", join(directory, "NuGet.config")], consumer, env);
  run("dotnet", dotnetBuildArguments("Consumer.csproj"), consumer, env);
  const intermediate = msbuildProjectPath(consumer, "Consumer.csproj", "IntermediateOutputPath");
  const pack = join(intermediate, "translations", "checkout.de.locale-v5.json");
  assert.equal(JSON.parse(readFileSync(pack, "utf8")).profile, "rmf2-execution-v2",
    "RMF2 package consumer did not generate the selected v5 external pack");
  run("dotnet", ["run", "--project", "Consumer.csproj", "--configuration", configuration, "--no-build", "--", pack], consumer, env);
  verifyConsumerGraph(consumer, "Runic.Translations RMF2");
  const esmRoot = join(intermediate, "translations", "checkout.esm-v5");
  assert.ok(readdirSync(esmRoot, { withFileTypes: true }).some(entry => entry.name === "messages.js"), "RMF2 v5 consumer did not emit ESM messages");
  const webManifest = JSON.parse(readFileSync(join(esmRoot, "web-module-manifest-v3.json"), "utf8"));
  assert.equal(webManifest.profile, "rmf2-execution-v2", "RMF2 package consumer emitted the wrong execution profile");
  assert.equal(webManifest.esmAbiVersion, 4, "RMF2 package consumer emitted the wrong ESM ABI");
  writeFileSync(join(consumer, "verify-esm.mjs"), `
import assert from "node:assert/strict";
import { m } from ${JSON.stringify(moduleSpecifier(consumer, join(esmRoot, "messages.js")))};
import { configureLocaleResolver, createLocaleSource, esmAbiVersion, messageGrammarVersion, profile, rmf2RuntimeAbiVersion } from ${JSON.stringify(moduleSpecifier(consumer, join(esmRoot, "runtime.js")))};
assert.deepEqual({ esmAbiVersion, messageGrammarVersion, profile, rmf2RuntimeAbiVersion }, { esmAbiVersion: 4, messageGrammarVersion: 5, profile: "rmf2-execution-v2", rmf2RuntimeAbiVersion: 2 });
const source = createLocaleSource({ initialLocale: "en" });
const restore = configureLocaleResolver(() => source.getLocale());
assert.equal(m.application_title(), "RMF2 checkout");
source.setLocale("de");
assert.equal(m.application_title(), "RMF2 Kasse");
restore();
console.log("RMF2 v5 generated ESM import, execution, and locale switch passed.");
  `);
  run("node", ["verify-esm.mjs"], consumer, env);

  // Publish the same package-only, profile-selected generated consumer. Its
  // external artifact is generated by the packed build package and loaded by
  // the packed runtime; no checkout project reference or reflection fallback
  // participates in this NativeAOT path.
  const nativeOutput = join(consumer, "native");
  const nativeOs = { linux: "linux", darwin: "osx", win32: "win" }[process.platform];
  const nativeArch = { x64: "x64", arm64: "arm64" }[process.arch];
  assert.ok(nativeOs && nativeArch, `Unsupported NativeAOT package-consumer host ${process.platform}/${process.arch}`);
  const nativeRid = `${nativeOs}-${nativeArch}`;
  run("dotnet", ["publish", "Consumer.csproj", "--configuration", "Release", "--runtime", nativeRid,
    "--self-contained", "true", "-p:PublishAot=true", "-p:PublishTrimmed=true", "-p:TrimMode=full", "-p:IlcTreatWarningsAsErrors=true",
    "-p:JsonSerializerIsReflectionEnabledByDefault=false", "--output", nativeOutput], consumer, env);
  const nativeIntermediate = msbuildProjectPath(
    consumer,
    "Consumer.csproj",
    "IntermediateOutputPath",
    "Release",
    [`-property:RuntimeIdentifier=${nativeRid}`],
  );
  const nativePack = join(nativeIntermediate, "translations", "checkout.de.locale-v5.json");
  assert.equal(JSON.parse(readFileSync(nativePack, "utf8")).messageGrammarVersion, 5,
    "RMF2 NativeAOT publish did not generate a grammar-v5 external pack");
  run(join(nativeOutput, "Consumer" + (process.platform === "win32" ? ".exe" : "")), [nativePack], consumer, env);
  verifyConsumerGraph(consumer, "Runic.Translations RMF2 NativeAOT");
}

async function verifyRmf2SvelteConsumer(frontend, directory) {
  const project = join(frontend, "rmf2-svelte");
  mkdirSync(join(project, "translations"), { recursive: true });
  mkdirSync(join(project, "src"), { recursive: true });
  writeFileSync(join(project, "translations", "runic.json"), JSON.stringify({
    schemaVersion: 1,
    catalog: "checkout",
    code: { namespace: "PackageRmf2", className: "CheckoutText" },
    baseLocale: "en",
    locales: ["en", "de"],
  }, null, 2));
  writeFileSync(join(project, "translations", "en.rmf2"), "application_title = RMF2 browser checkout\n");
  writeFileSync(join(project, "translations", "de.rmf2"), "application_title = RMF2 browser Kasse\n");
  writeFileSync(join(project, "src", "App.svelte"), `<script>
import { m } from "virtual:runic-translations/checkout";
import { createLocaleContext } from "@runic-artifex/svelte/translations";
import { createLocaleSource } from "virtual:runic-translations/checkout/runtime";
let { initialLocale = "en" } = $props();
const localeContext = createLocaleContext();
// svelte-ignore state_referenced_locally
const locale = localeContext.provide(createLocaleSource({ initialLocale }));
</script>
<h1 data-title>{m.application_title(locale.messageOptions)}</h1>\n`);
  writeFileSync(join(project, "index.html"), '<div id="app"></div><script type="module" src="/src/main.js"></script>\n');
  writeFileSync(join(project, "src", "main.js"), 'import App from "./App.svelte"; import { mount } from "svelte"; mount(App, { target: document.getElementById("app") });\n');
  writeFileSync(join(project, "vite.config.js"), `import { defineConfig } from "vite";\nimport { svelte } from "@sveltejs/vite-plugin-svelte";\nimport { runicTranslations } from "@runic-artifex/vite-plugin-runic-translations";\nexport default defineConfig({ plugins: [runicTranslations({ project: "./translations", output: "./.runic", command: ${JSON.stringify(join(directory, "tools", "runic-translations"))}, commandArguments: [] }), svelte()] });\n`);
  run("node", [join(frontend, "node_modules/vite/bin/vite.js"), "build"], project);
  assert.ok(readFileSync(join(project, "dist", "index.html"), "utf8").length > 0, "RMF2 Svelte consumer did not produce a browser entrypoint");
  const browserAssets = [];
  const collectBrowserAssets = (directory) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) collectBrowserAssets(path);
      else if (entry.name.endsWith(".js")) browserAssets.push(readFileSync(path, "utf8"));
    }
  };
  collectBrowserAssets(join(project, "dist"));
  assert.ok(browserAssets.some(asset => asset.includes("RMF2 browser checkout")), "RMF2 Svelte bundle did not execute the generated message module");
  writeFileSync(join(project, "verify-ssr.mjs"), `
import assert from "node:assert/strict";
import { createServer } from "vite";
const vite = await createServer({ root: ${JSON.stringify(project)}, configFile: ${JSON.stringify(join(project, "vite.config.js"))}, appType: "custom", server: { middlewareMode: true } });
const app = await vite.ssrLoadModule("/src/App.svelte");
const { render } = await vite.ssrLoadModule("svelte/server");
const en = render(app.default, { props: { initialLocale: "en" } });
const de = render(app.default, { props: { initialLocale: "de" } });
assert.match(en.body, /RMF2 browser checkout/);
assert.match(de.body, /RMF2 browser Kasse/);
assert.match(en.body, /data-title/);
await vite.close();
console.log("RMF2 Svelte locale adapter SSR passed.");
`);
  run("node", ["verify-ssr.mjs"], project);
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
    dotnetBuildArguments("TranslationConsumer.csproj", configuration, ["--nologo"]),
    translations,
    env,
  );

  // Exercise both canonical templates from the installed package. The item
  // template is validated through the public tool; the project template is
  // restored and built against the candidate packages, so neither path can
  // hide a stale source-tree reference.
  const item = join(directory, "translation-item-template");
  run(
    "dotnet",
    [
      "new",
      "runic-translations",
      "--output",
      item,
      "--catalog",
      "checkout",
      "--defaultLocale",
      "en",
      "--namespace",
      "PackageRmf2",
      "--className",
      "CheckoutText",
    ],
    directory,
    env,
  );
  const translationTool = join(toolPath, "runic-translations" + (process.platform === "win32" ? ".exe" : ""));
  run(translationTool, ["validate", "--project", join(item, "translations")], directory, env);
  const installedSchemas = join(directory, "installed-translation-schemas");
  run(translationTool, ["schema", "--output", installedSchemas], directory, env);
  for (const schema of ["project-v1.schema.json", "message-ast-v5.schema.json", "locale-artifact-v5.schema.json", "external-pack-v5.schema.json", "web-module-manifest-v3.schema.json"])
    assert.ok(readFileSync(join(installedSchemas, schema), "utf8").length > 0, `Installed translation tool omitted ${schema}`);

  run(translationTool, ["validate", "--project", join(translations, "translations")], directory, env);
  verifyConsumerGraph(translations, "Runic.Translations v5 template");
  const templateIntermediate = msbuildProjectPath(translations, "TranslationConsumer.csproj", "IntermediateOutputPath");
  const templateManifest = JSON.parse(readFileSync(join(templateIntermediate, "translations", "product.esm-v5", "web-module-manifest-v3.json"), "utf8"));
  assert.equal(templateManifest.profile, "rmf2-execution-v2", "Translation project template did not execute the v5 profile");
}
