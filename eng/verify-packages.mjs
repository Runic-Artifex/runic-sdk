import assert from "node:assert/strict";
import {
  cpSync,
  mkdtempSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  writeFileSync,
  realpathSync,
  rmSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { execFileSync, spawnSync } from "node:child_process";
import { basename, extname, resolve, join } from "node:path";
import { root, workspace, run, configuration, packedNuget } from "./run.mjs";
import { nupkgFiles, nupkgDependencies, nupkgDependencyVersions, nupkgFileText } from "./nupkg.mjs";
import { packageVersion } from "./version.mjs";
import { npmReleaseAgeArguments } from "./dependencies/release-age.mjs";
import { trainFeedNpmSpec, trainFeedSource } from "./ci/train-feed.mjs";
import { packageSourceMapProblems } from "./source-map-comments.mjs";

const nativeProviders = new Set([
  "runic.platform.windows",
  "runic.platform.linux",
  "runic.platform.linux.gtk4",
  "runic.platform.macos",
]);


// Expected nupkg layouts. Every package added after W240-003 gets an entry here in
// the slice that creates it. `files` lists the complete layout, `includes` files
// that must be present, `dependencies` the complete nuspec dependency list, and
// `exactDependencies` the dependencies pinned to exactly the workspace version.
// `requires`/`forbidden` dependencies that must or must not appear in it.
const nativeServerRuntimes = ["linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "win-x64", "win-arm64", "osx-arm64"];
export const packageLayouts = {
  // One static library each, which only Runic.Desktop.NativeServer's targets use.
  ...Object.fromEntries(nativeServerRuntimes.map(rid => [`Runic.Desktop.NativeServer.Static.${rid}`, {
    files: [`Runic.Desktop.NativeServer.Static.${rid}.nuspec`, "README.md", "THIRD-PARTY-NOTICES.md", "icon.png",
      `runtimes/${rid}/native/${rid.startsWith("win-") ? "runic_server.lib" : "librunic_server.a"}`],
    dependencies: [],
  }])),
  // Navigation is a plain library: no build targets, tools or content (W240-001 D3).
  "Runic.Navigation": {
    files: ["README.md", "Runic.Navigation.nuspec", "icon.png", "lib/net10.0/Runic.Navigation.dll", "lib/net10.0/Runic.Navigation.xml"],
    dependencies: ["Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions"],
  },
  // The ReactiveUI navigation adapters depend on Runic.Navigation and ReactiveUI, never on
  // Runic.Application.Views (W240-001 §9). `requires` lists dependencies that must be present and
  // `forbidden` patterns that no dependency may match.
  "Runic.Navigation.ReactiveUI": {
    files: ["README.md", "Runic.Navigation.ReactiveUI.nuspec", "icon.png", "lib/net10.0/Runic.Navigation.ReactiveUI.dll", "lib/net10.0/Runic.Navigation.ReactiveUI.xml",
      // RUNICNAV002: both navigation flavors in one application.
      "buildTransitive/Runic.Navigation.ReactiveUI.props", "buildTransitive/Runic.Navigation.ReactiveUI.targets"],
    requires: ["Runic.Navigation", "ReactiveUI"],
    exactDependencies: ["Runic.Navigation"],
    forbidden: [/^Runic\.Application/i, /^ReactiveUI\.Reactive$/i],
  },
  "Runic.Navigation.ReactiveUI.Reactive": {
    files: ["README.md", "Runic.Navigation.ReactiveUI.Reactive.nuspec", "icon.png", "lib/net10.0/Runic.Navigation.ReactiveUI.Reactive.dll", "lib/net10.0/Runic.Navigation.ReactiveUI.Reactive.xml",
      // RUNICNAV002: both navigation flavors in one application.
      "buildTransitive/Runic.Navigation.ReactiveUI.Reactive.props", "buildTransitive/Runic.Navigation.ReactiveUI.Reactive.targets"],
    requires: ["Runic.Navigation", "ReactiveUI.Reactive"],
    exactDependencies: ["Runic.Navigation"],
    forbidden: [/^Runic\.Application/i, /^ReactiveUI$/i],
  },
  // The WPF hosts depend on Runic.Navigation and the WPF framework only (W240-001 §8) and
  // carry no build assets.
  "Runic.Navigation.Wpf": {
    files: ["README.md", "Runic.Navigation.Wpf.nuspec", "icon.png", "lib/net10.0-windows7.0/Runic.Navigation.Wpf.dll", "lib/net10.0-windows7.0/Runic.Navigation.Wpf.xml"],
    requires: ["Runic.Navigation"],
    exactDependencies: ["Runic.Navigation"],
    forbidden: [/^Runic\.(?!Navigation$)/i],
  },
  // The Application adapters own model-context scheduling and bring no navigation adapter, so an app
  // without navigation has no navigation package in its graph (W250-014).
  "Runic.Application.Views.ReactiveUI": {
    requires: ["Runic.Application.Views", "ReactiveUI"],
    forbidden: [/^Runic\.Navigation\.ReactiveUI/i, /^ReactiveUI\.Reactive$/i],
  },
  "Runic.Application.Views.ReactiveUI.Reactive": {
    requires: ["Runic.Application.Views", "ReactiveUI.Reactive"],
    forbidden: [/^Runic\.Navigation\.ReactiveUI/i],
  },
  // The props default RunicAssetsDistExclude before the project file, so a project can empty it.
  // The packer runs on a newer major runtime when 10.0 is absent (#614).
  "Runic.Assets": {
    includes: ["buildTransitive/Runic.Assets.props", "buildTransitive/Runic.Assets.HelpLinks.props", "buildTransitive/Runic.Assets.targets"],
    rollForwardMajor: ["tools/net10.0/Runic.Assets.Packer.runtimeconfig.json"],
  },
  // The packed generator loads region slots from Runic.Navigation (W240-001 §4.3). The RUNICBRIDGE shape analyzers and
  // their code fixes ship under analyzers/ (#962).
  "Runic.Application.Views": {
    includes: ["lib/net10.0/Runic.Application.Views.dll", "tools/net10.0/Runic.Navigation.dll", "buildTransitive/Runic.Application.Views.props", "buildTransitive/Runic.Application.Views.targets",
      "analyzers/dotnet/cs/Runic.Application.Views.Analyzers.dll", "analyzers/dotnet/cs/Runic.Application.Views.Analyzers.CodeFixes.dll"],
    rollForwardMajor: ["tools/net10.0/BridgeCodegen.runtimeconfig.json"],
    // The moved types must come from the matching Runic.Navigation build (W240-001 §3).
    exactDependencies: ["Runic.Navigation"],
  },
  // The optional en/de catalog of Runic's own text (D055.3): the packed catalog for application overrides, the Translations
  // runtime bounded to its minor, and no dependency on the development-only Runic.Translations.Build.
  "Runic.Application.Views.Translations": {
    files: ["README.md", "Runic.Application.Views.Translations.nuspec", "icon.png",
      "lib/net10.0/Runic.Application.Views.Translations.dll", "lib/net10.0/Runic.Application.Views.Translations.xml",
      "buildTransitive/Runic.Application.Views.Translations.props", "runic/runic-views/catalog.props", "runic/runic-views/contract.json",
      "runic/runic-views/runic.json", "runic/runic-views/en.rmf2", "runic/runic-views/de.rmf2"],
    dependencies: ["Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions",
      "Runic.Application.Views", "Runic.Translations", "Runic.Translations.Hosting"],
    exactDependencies: ["Runic.Application.Views"],
    // Runic.Translations.Build stamps the range when packing; Hosting keeps the floor and no upper bound, and a Hosting of another minor
    // needs a Runic.Translations outside that range.
    minorBounded: ["Runic.Translations"],
  },
  // An application's own spell checker (D074.10, preview): the bundled English dictionaries with their licences, which
  // its buildTransitive targets copy into the application as dictionaries/, and WeCantSpell.Hunspell, whose types no API
  // exposes. No Runic package: it shares Runic.Desktop's tag matching as source.
  "Runic.Spelling": {
    files: ["README.md", "Runic.Spelling.nuspec", "icon.png", "lib/net10.0/Runic.Spelling.dll", "lib/net10.0/Runic.Spelling.xml",
      "buildTransitive/Runic.Spelling.targets", "dictionaries/en_US.dic", "dictionaries/en_US.aff", "dictionaries/en_GB.dic",
      "dictionaries/en_GB.aff", "dictionaries/README_en_US.txt", "dictionaries/README_en_GB.txt", "dictionaries/LICENSE_WeCantSpell.Hunspell.txt"],
    dependencies: ["WeCantSpell.Hunspell"],
  },
  // Host adapters set _RunicApplicationHostAdapter, which turns Bridge generation on
  // in every project that references them, directly or transitively.
  "Runic.Application.Views.Desktop": { includes: ["buildTransitive/Runic.Application.Views.Desktop.props"] },
  "Runic.Application.Views.CsWebUi": { includes: ["buildTransitive/Runic.Application.Views.CsWebUi.props"] },
  "Runic.Application.Views.Wpf": { includes: ["buildTransitive/Runic.Application.Views.Wpf.props"] },
  // Runic Desktop shares its menu model (DesktopMenu, DesktopMenuItem, DesktopShortcut) with status icons through
  // Runic.Platform, whose internal menu rules it uses (D031.2). Runic.Platform itself has no dependencies. The Linux
  // global menu exports the bar over D-Bus through Runic.Platform.Linux.DBus (D036.11), whose wire library NuGet lists
  // as a direct dependency, as for Runic.Platform.Linux.Portal.
  // Since 0.15 (X2) it has no ASP.NET Core reference: the presentation server is Runic.Desktop.Kestrel or the
  // experimental Runic.Desktop.NativeServer, which bring the container, and the server-neutral code needs the
  // dependency injection and logging abstractions.
  "Runic.Desktop": {
    dependencies: ["Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions",
      "Microsoft.Web.WebView2", "Runic.Platform", "Runic.Platform.Linux.DBus", "Tmds.DBus.Protocol"],
    exactDependencies: ["Runic.Platform", "Runic.Platform.Linux.DBus"],
    includes: ["buildTransitive/Runic.Desktop.targets"],
  },
  // The native Desktop server (D053.24: the default of new applications): no assembly, the shared library of every
  // runtime identifier, and a runtime.json that brings Runic.Desktop.NativeServer.Static.<rid> for a runtime-specific
  // restore. eng/native/verify-native-server-package.mjs consumes it as an application does.
  "Runic.Desktop.NativeServer": {
    requires: ["Runic.Desktop", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Logging"],
    exactDependencies: ["Runic.Desktop"],
    includes: ["runtime.json", "THIRD-PARTY-NOTICES.md", "buildTransitive/Runic.Desktop.NativeServer.props",
      "buildTransitive/Runic.Desktop.NativeServer.HelpLinks.props", "buildTransitive/Runic.Desktop.NativeServer.targets"],
  },
  // ASP.NET Core Kestrel for Runic Desktop: its props announce it to Runic.Desktop's targets, which select it.
  "Runic.Desktop.Kestrel": {
    requires: ["Runic.Desktop"],
    exactDependencies: ["Runic.Desktop"],
    includes: ["lib/net10.0/Runic.Desktop.Kestrel.dll", "buildTransitive/Runic.Desktop.Kestrel.props",
      "buildTransitive/Runic.Desktop.Kestrel.HelpLinks.props", "buildTransitive/Runic.Desktop.Kestrel.targets"],
  },
  // The privileged-action targets validate RunicPrivilegedAction items and generate their declarations; the props
  // carry their help links (W300-004).
  // The RUNICPLAT analyzers and their code fixes ship under analyzers/ (0.14).
  "Runic.Platform": { includes: ["buildTransitive/Runic.Platform.props", "buildTransitive/Runic.Platform.targets",
    "analyzers/dotnet/cs/Runic.Platform.Analyzers.dll", "analyzers/dotnet/cs/Runic.Platform.Analyzers.CodeFixes.dll"] },
  // The macOS targets generate the launch daemon's property list; the props carry their help links (W300-026).
  "Runic.Platform.MacOS": { includes: ["buildTransitive/Runic.Platform.MacOS.props", "buildTransitive/Runic.Platform.MacOS.targets"] },
  // The Polkit targets generate the policy; the props carry their help links (W300-004).
  "Runic.Platform.Linux.Polkit": {
    files: ["README.md", "Runic.Platform.Linux.Polkit.nuspec", "icon.png", "lib/net10.0/Runic.Platform.Linux.Polkit.dll", "lib/net10.0/Runic.Platform.Linux.Polkit.xml",
      "buildTransitive/Runic.Platform.Linux.Polkit.props", "buildTransitive/Runic.Platform.Linux.Polkit.targets"],
    dependencies: ["Runic.Platform"],
    exactDependencies: ["Runic.Platform"],
  },
  // The D-Bus API wraps Tmds.DBus.Protocol; no Tmds type is public (W310-001). Its source generator, adapted from
  // Tmds.DBus.Generator, ships as an analyzer with the MIT notice; the props expose the AdditionalFiles metadata.
  "Runic.Platform.Linux.DBus": {
    files: ["README.md", "Runic.Platform.Linux.DBus.nuspec", "icon.png", "NOTICE", "licenses/Tmds.DBus-LICENSE.txt",
      "analyzers/dotnet/cs/Runic.Platform.Linux.DBus.Generator.dll", "buildTransitive/Runic.Platform.Linux.DBus.props",
      "lib/net10.0/Runic.Platform.Linux.DBus.dll", "lib/net10.0/Runic.Platform.Linux.DBus.xml"],
    dependencies: ["Microsoft.Extensions.DependencyInjection.Abstractions", "Runic.Platform", "Tmds.DBus.Protocol"],
    exactDependencies: ["Runic.Platform"],
  },
  // The elevation targets generate the WindowsElevation options and check helpers; the manifest is the helpers'
  // requireAdministrator default (W300-025).
  "Runic.Platform.Windows.Elevation": {
    files: ["README.md", "Runic.Platform.Windows.Elevation.nuspec", "icon.png", "lib/net10.0/Runic.Platform.Windows.Elevation.dll", "lib/net10.0/Runic.Platform.Windows.Elevation.xml",
      "buildTransitive/Runic.Platform.Windows.Elevation.props", "buildTransitive/Runic.Platform.Windows.Elevation.targets", "buildTransitive/Runic.Platform.Windows.Elevation.manifest"],
    // WindowsElevationOptions.Owner takes Runic.Platform.Runtime's INativeWindowOwner (W319-003 el-03).
    dependencies: ["Runic.Platform.Runtime"],
    exactDependencies: ["Runic.Platform.Runtime"],
  },
  // The unpacked Runic DevTools extension ships beside the tool (#1044).
  "dotnet-runic": {
    rollForwardMajor: ["tools/net10.0/any/dotnet-runic.runtimeconfig.json"],
    includes: ["tools/net10.0/any/devtools-extension/manifest.json", "tools/net10.0/any/devtools-extension/devtools.html",
      "tools/net10.0/any/devtools-extension/panel.html", "tools/net10.0/any/devtools-extension/sidebar.html"],
  },
  "Runic.Create": { rollForwardMajor: ["tools/net10.0/any/runic-create.runtimeconfig.json"] },
};

export function verifyPackageLayout(nupkg, name, version, layout = packageLayouts[name]) {
  if (!layout) return;
  const files = nupkgFiles(nupkg);
  if (layout.files) assert.deepEqual(files, [...layout.files].sort(), `${name} has an unexpected nupkg layout`);
  for (const file of layout.includes ?? []) assert.ok(files.includes(file), `${name} does not pack ${file}`);
  if (layout.dependencies)
    assert.deepEqual(nupkgDependencies(nupkg), [...layout.dependencies].sort(), `${name} has unexpected dependencies`);
  if (layout.exactDependencies) {
    const versions = nupkgDependencyVersions(nupkg);
    for (const id of layout.exactDependencies)
      assert.deepEqual(versions.get(id), [`[${version}]`], `${name} does not depend on exactly ${id} ${version}`);
  }
  const dependencies = nupkgDependencies(nupkg);
  for (const id of layout.requires ?? []) assert.ok(dependencies.includes(id), `${name} does not depend on ${id}`);
  for (const pattern of layout.forbidden ?? [])
    assert.ok(!dependencies.some(id => pattern.test(id)), `${name} depends on ${dependencies.filter(id => pattern.test(id)).join(", ")}`);
  // Runic Translations' library range before 1.0: [N, next minor-0.0), which keeps the next minor's previews out too.
  for (const id of layout.minorBounded ?? []) {
    const ranges = nupkgDependencyVersions(nupkg).get(id) ?? [];
    assert.ok(ranges.length > 0 && ranges.every(range => /^\[0\.(\d+)\.\d+, 0\.(\d+)\.0-0\.0\)$/.test(range)
      && Number(/^\[0\.(\d+)/.exec(range)[1]) + 1 === Number(/, 0\.(\d+)/.exec(range)[1])),
      `${name} does not bound ${id} to its minor: ${ranges.join(", ")}`);
  }
  for (const file of layout.rollForwardMajor ?? [])
    assert.equal(JSON.parse(nupkgFileText(nupkg, file)).runtimeOptions?.rollForward, "Major",
      `${name}: ${file} does not roll forward to a newer major runtime`);
}
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

// Windows-only packages declare how a packed consumer uses them: it builds on every
// platform and runs only on Windows.
const windowsConsumerStrategies = {
  "Runic.Application.Views.Wpf": { useWpf: true, canaryType: "Runic.Application.Views.Wpf.RunicWebView" },
  "Runic.Navigation.Wpf": { useWpf: true, canaryType: "Runic.Navigation.Wpf.NavigationHost" },
};

export function packageConsumerStrategy(packageEntry, platform = process.platform) {
  const windows = windowsConsumerStrategies[packageEntry.name];
  if (windows)
    return {
      targetFramework: "net10.0-windows",
      execute: platform === "win32",
      enableWindowsTargeting: true,
      useWpf: windows.useWpf,
      canaryType: windows.canaryType,
    };
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
    assert.equal(library.split("/")[1], packageVersion(workspace, library.split("/")[0]), `stale internal dependency ${library}`);
  }
  // Host adapters use the DI abstractions; the application chooses and references a container.
  if (["Runic.Application.Views.Desktop", "Runic.Application.Views.CsWebUi", "Runic.Application.Views.Wpf"].includes(label)) {
    assert.ok(!libraries.some(library => library.split("/")[0].toLowerCase() === "microsoft.extensions.dependencyinjection"),
      `${label} unexpectedly depends on the Microsoft.Extensions.DependencyInjection container`);
  }
  // A Runic.Desktop consumer without a server package needs no ASP.NET Core; Runic.Desktop.Kestrel brings it.
  if (label === "Runic.Desktop" || label === "Runic.Desktop.Kestrel" || label === "Runic.Desktop.NativeServer") {
    const frameworks = Object.values(assets.project.frameworks ?? {})
      .flatMap(framework => Object.keys(framework.frameworkReferences ?? {}));
    const targets = Object.values(assets.targets ?? {}).flatMap(target => Object.values(target))
      .flatMap(library => library.frameworkReferences ?? []);
    assert.equal([...frameworks, ...targets].includes("Microsoft.AspNetCore.App"), label === "Runic.Desktop.Kestrel",
      `${label} ${label === "Runic.Desktop" ? "unexpectedly restores" : "does not restore"} the ASP.NET Core shared framework`);
  }
  const isolated = platformOnly;
  if (!isolated) return;

  // Inspect the complete restored graph, including dependencies that are not loaded by the canary.
  const allowedPlatform = new Set(["runic.platform", label.toLowerCase()]);
  if (label !== "Runic.Platform") allowedPlatform.add("runic.platform.runtime");
  if (label === "Runic.Platform.Linux") allowedPlatform.add("runic.platform.linux.portal");
  // The portal makes every D-Bus call through the public D-Bus API (W310-002, W319 I-052).
  if (label === "Runic.Platform.Linux" || label === "Runic.Platform.Linux.Portal") allowedPlatform.add("runic.platform.linux.dbus");
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
  // The native server's static packages carry no assembly for a consumer to load; their layout is checked.
  const allLibraries = packedNuget().filter(
    (p) => !isToolPackage(p) && !p.name.endsWith(".Templates") && !p.name.startsWith("Runic.Desktop.NativeServer.Static."),
  );
  const libraries = packageName
    ? allLibraries.filter(packageEntry => packageEntry.name === packageName)
    : allLibraries;
  assert.ok(!packageName || libraries.length === 1, `Unknown NuGet library consumer ${packageName}`);
  for (const p of packageName ? libraries : packedNuget()) {
    assert.ok(
      readdirSync(nuget).includes(`${p.name}.${packageVersion(workspace, p.name)}.nupkg`),
      `Pack ${p.name} first`,
    );
  }
  for (const p of packageName ? libraries : packedNuget()) {
    const nupkg = join(nuget, `${p.name}.${packageVersion(workspace, p.name)}.nupkg`);
    verifyPackageLayout(nupkg, p.name, workspace.version);
    // Core packages keep no Runic Translations dependency (D055.3); the generator recognizes TranslationReference by name.
    if (p.name !== viewsTranslations)
      assert.ok(!nupkgDependencies(nupkg).some(id => /^Runic\.Translations/i.test(id)), `${p.name} depends on Runic Translations`);
  }
  const archives = (packageName ? [] : workspace.npm).map((p) => {
    const file = `${p.name.replace("@", "").replace("/", "-")}-${workspace.version}.tgz`;
    assert.ok(readdirSync(npm).includes(file), `Pack ${p.name} first`);
    return [p.name, resolve(npm, file)];
  });
  // The temporary tree has no parent workspace, source references, or reusable Runic package cache. During a train,
  // the Command Line and Translations candidates come from the train feed (eng/ci/train-feed.mjs).
  const train = trainFeedSource();
  writeFileSync(
    join(directory, "NuGet.config"),
    `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${nuget}"/>${train.source}<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate">${workspace.nuget.map(packageEntry => `<package pattern="${packageEntry.name}"/>`).join("")}</packageSource>${train.mapping}<packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`,
  );
  const env = {
    NUGET_PACKAGES: join(directory, "nuget-cache"),
    DOTNET_CLI_HOME: join(directory, "dotnet-home"),
  };
  for (const p of libraries) {
    const strategy = packageConsumerStrategy(p);
    const projectText = readFileSync(resolve(root, p.project), "utf8");
    // Runic.Desktop.NativeServer has no assembly; the consumer loads the Runic.Desktop it brings.
    const assemblyName = p.name === "Runic.Desktop.NativeServer" ? "Runic.Desktop"
      : projectText.match(/<AssemblyName>([^<]+)<\/AssemblyName>/)?.[1] ?? basename(p.project, ".csproj");
    const consumer = join(directory, p.name);
    const viewTestConsumer = p.name === "Runic.Application.Testing";
    const bridgeProperties = viewTestConsumer
      ? `<RunicApplicationFrontendGenerationEnabled>true</RunicApplicationFrontendGenerationEnabled><RunicApplicationFrontendBuildCommand>dotnet --version</RunicApplicationFrontendBuildCommand><RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/Frontend</RunicApplicationFrontendDirectory><RunicApplicationFrontendGeneratedDirectory>$(RunicApplicationFrontendDirectory)/src/generated</RunicApplicationFrontendGeneratedDirectory><OutputType Condition="'$(RunicApplicationFrontendBootstrap)' == 'true'">Library</OutputType>`
      : ["Runic.Application.Views.CsWebUi", "Runic.Application.Views.ReactiveUI", "Runic.Application.Views.ReactiveUI.Reactive", "Runic.Application.Views.Desktop", "Runic.Application.Views.Wpf"].includes(p.name)
        ? "<RunicApplicationFrontendGenerationEnabled>false</RunicApplicationFrontendGenerationEnabled>" : "";
    mkdirSync(consumer);
    writeFileSync(
      join(consumer, "Consumer.csproj"),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>${strategy.targetFramework}</TargetFramework><OutputType>Exe</OutputType><RunicDesktopServer Condition="'${p.name}' != 'Runic.Desktop.Kestrel'">None</RunicDesktopServer><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors>${bridgeProperties}${strategy.enableWindowsTargeting ? "<EnableWindowsTargeting>true</EnableWindowsTargeting>" : ""}${strategy.useWpf ? "<UseWPF>true</UseWPF>" : ""}</PropertyGroup><ItemGroup><PackageReference Include="${p.name}" Version="${packageVersion(workspace, p.name)}"/>${viewTestConsumer ? `<PackageReference Include="Runic.Application.Views" Version="${workspace.version}"/>` : ""}</ItemGroup>${viewTestConsumer ? `<ItemGroup Condition="'$(RunicApplicationFrontendBootstrap)' == 'true'"><Compile Remove="Program.cs"/></ItemGroup>` : ""}</Project>`,
    );
    writeFileSync(
      join(consumer, "Program.cs"),
      p.name === "Runic.Application.Testing"
        ? `using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Generated.Bridges;
using PackageConsumer;
var model = new ConsumerViewModel();
var window = new ConsumerWindow { DataContext = model };
if (!ReferenceEquals(window.DataContext, model)) throw new Exception("Window DataContext was lost.");
using var host = new RunicWindowTestHost<ConsumerViewModel>(model,
    (transport, content, vm) => new ConsumerBridge(transport, vm, content: content),
    new RunicWindowTestHostOptions { RootRoute = "consumer" });
if (host.Root.Snapshot().Json.GetProperty("value").GetInt32() != 7)
    throw new Exception("The packaged headless Window snapshot failed.");
host.Root.Set(vm => vm.Value, 8).ThrowIfFailed();
if (model.Value != 8) throw new Exception("The packaged generated setter failed.");
_ = host.Root.TakePublications();
model.Value = 9;
var frame = await host.Root.NextPublicationAsync();
if (frame.Kind != RunicPublicationKind.Frame || frame.Sets.Single().Value.GetInt32() != 9)
    throw new Exception("The packaged generated publication failed.");
Console.WriteLine("Packaged Window/View test host passed.");`
      : strategy.canaryType
        // Without a suppression: the canary types are stable, so using one compiles as is.
        ? `Console.WriteLine(typeof(${strategy.canaryType}).Assembly.GetName().Name);`
        : `Console.WriteLine(System.Reflection.Assembly.Load("${assemblyName}").GetName().Name);`,
    );
    if (viewTestConsumer)
      writeFileSync(join(consumer, "TestModel.cs"), `#nullable enable
using System.ComponentModel;
using Runic.Application.Views;
namespace PackageConsumer;
public sealed partial class ConsumerWindow : RunicWindow<ConsumerViewModel>;
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
  if (!packageName || packageName === viewsTranslations) verifyViewsTranslationsConsumer(directory, env);
  if (!packageName || packageName === "Runic.Application.Views") verifyViewsAnalyzerConsumers(directory, env);
  if (!packageName || packageName === "Runic.Platform") verifyPlatformAnalyzerConsumers(directory, env);
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
          vite: "8.3.4",
          "@angular/core": "22.2.3",
          react: "19.3.0",
          "@types/react": "19.3.0",
          vue: "3.5.43",
          "@sveltejs/kit": "3.1.1",
          "@sveltejs/adapter-static": "4.0.1",
          "@sveltejs/vite-plugin-svelte": "7.3.1",
          // The release of the compiler that builds the runic-views catalog (one pin, eng/workspace.test.mjs).
          // During a train, the candidate tarball of the train feed (eng/ci/train-feed.mjs) until npm has it.
          "@runic-artifex/vite-plugin-runic-translations": trainFeedNpmSpec("@runic-artifex/vite-plugin-runic-translations",
            pinnedPackageVersion("Runic.Translations.Build")),
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
      ...npmReleaseAgeArguments(),
    ],
    frontend,
  );
  for (const [name] of archives) {
    const path = realpathSync(join(frontend, "node_modules", name));
    assert.ok(
      path.startsWith(frontend),
      `${name} is linked to the source workspace`,
    );
    // A debugger reads a source map comment anywhere in a file as its source map (#1050).
    assert.deepEqual(packageSourceMapProblems(path), [], `${name} ships JavaScript with a source map comment that is not its own trailing one`);
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
import { BridgeError, onBridgeDiagnostic } from '@runic-artifex/views';
import { bridgeOperations, connectView, defineCollection, defineCollections, defineInteractions } from '@runic-artifex/views/generated';
import * as bridgeWire from '@runic-artifex/views/generated/wire';
import { installMockBridge } from '@runic-artifex/views/mock';
import { useView as useReactView } from '@runic-artifex/react';
import { useView as useVueView } from '@runic-artifex/vue';
import { command } from '@runic-artifex/views-effect';
import { FieldError } from '@runic-artifex/views';
import { runicViewsFieldErrorText, runicViewsMessages } from '@runic-artifex/views-translations';
import { Effect } from 'effect';
assert.equal(runicSpaPageOptions.ssr, false);
// The prebuilt runic-views catalog loads from the archive, with its German text.
assert.equal(runicViewsMessages.platform_untitled_file_name({ locale: 'de' }), 'Unbenannt');
assert.equal(runicViewsFieldErrorText(new FieldError('invalid-text', '1e is not a number.', undefined,
  { reason: 'not-a-number', arguments: { text: '1e' } }), 'de'), '1e ist keine Zahl.');
assert.ok(Object.keys(vite).length);
const bridge = installMockBridge();
const consumerView = bridge.view('consumer', { state: { value: 1, rows: [{ id: 1 }] }, contract: 'Consumer:fingerprint',
  collections: { rows: row => String(row.id) }, operations: { save: () => ({ result: 3 }) } });
const row = value => bridgeWire.object(value, item => ({ id: bridgeWire.integer(item.id, 0, 9) }));
const client = await connectView({ contract: 'Consumer:fingerprint', route: 'consumer',
  members: { value: wire => bridgeWire.integer(wire, 0, 9), rows: wire => bridgeWire.array(wire, row) },
  collections: defineCollections({ rows: defineCollection(row, item => String(item.id)) }),
  interactions: defineInteractions({ confirm: { contract: 'Consumer:interaction:Confirm', decodeInput: bridgeWire.string, encodeOutput: value => value } }),
  operations: bridgeOperations });
assert.equal(client.snapshot.value, 1);
assert.deepEqual(client.snapshot.rows, [{ id: 1 }]);
// The injected collection runtime rejects a state with duplicate keys.
const diagnostics = [];
const stopDiagnostics = onBridgeDiagnostic(diagnostic => diagnostics.push(diagnostic));
const consoleError = console.error;
console.error = () => {};
try {
  consumerView.update({ rows: [{ id: 2 }, { id: 2 }] });
  await bridge.flush();
  assert.ok(diagnostics.length >= 1);
  assert.deepEqual(client.snapshot.rows, [{ id: 1 }]);
  // The client recovers with a snapshot once the rows are valid again.
  consumerView.update({ rows: [{ id: 3 }] });
  for (let attempt = 0; attempt < 40 && client.snapshot.rows[0]?.id !== 3; attempt++) await new Promise(resolve => setTimeout(resolve, 50));
  assert.deepEqual(client.snapshot.rows, [{ id: 3 }]);
} finally { console.error = consoleError; stopDiagnostics(); }
const stopConfirm = client.interactions.confirm.handle(() => true);
stopConfirm();
const operation = await client.start('save', 'save-1', undefined, { decode: value => bridgeWire.integer(value, 0, 9) });
assert.equal((await operation.completion).result, 3);
client.dispose();
await assert.rejects(client.set('value', 2), error => error instanceof BridgeError && error.kind === 'released');
assert.equal((await Effect.runPromise(Effect.flip(command(() => client.set('value', 2)))))._tag, 'ViewReleased');
for (const hook of [useReactView, useVueView]) assert.equal(typeof hook, 'function');
console.log('Packed npm consumers passed.');
`,
  );
  run("node", ["consumer.mjs"], frontend);
  // The development build of @runic-artifex/views (the `development` export condition, views README "Development
  // build") runs the same consumer, and the production build contains none of its development tools.
  run("node", ["--conditions=development", "consumer.mjs"], frontend);
  writeFileSync(join(frontend, "views-builds.mjs"), `
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
const development = process.execArgv.includes('--conditions=development');
// What only each package's development build contains: views' development tools, and the bindings' DevTools
// integration (#1041).
const packages = {
  '@runic-artifex/views': ['runic.views.calls', 'createTask', 'runic.translations.text', 'trace-context/1', 'handler-timing/1', 'runic.views.describe', 'devtoolsFormatters'],
  '@runic-artifex/react': ['useDebugValue', 'describeViewClient'],
  '@runic-artifex/vue': ['@vue/devtools-api', 'runic.views.calls', 'runic.views.inspector'],
};
for (const [name, markers] of Object.entries(packages)) {
  const manifest = JSON.parse(readFileSync(new URL(import.meta.resolve(name + '/package.json')), 'utf8'));
  for (const entry of Object.keys(manifest.exports).filter(entry => entry !== './package.json')) {
    const url = import.meta.resolve(name + entry.slice(1));
    assert.equal(url.includes('/dist/development/'), development, url);
    assert.ok(existsSync(new URL(url.replace('/dist/development/', '/dist/').replace(/\\.js$/, '.d.ts'))), url + ' has no declarations');
    if (!development) for (const marker of markers)
      assert.ok(!readFileSync(new URL(url), 'utf8').includes(marker), url + ' contains ' + marker);
  }
  // No packed module of either build names the build's flag, which no application bundler defines (#958).
  const packed = new URL('dist/', import.meta.resolve(name + '/package.json'));
  for (const file of readdirSync(packed, { recursive: true }).filter(file => String(file).endsWith('.js')))
    assert.ok(!readFileSync(new URL(String(file), packed), 'utf8').includes('__RUNIC_DEVELOPMENT__'), name + ' ' + file + ' references __RUNIC_DEVELOPMENT__');
}
console.log('The ' + (development ? 'development' : 'production') + ' builds of @runic-artifex/views, /react and /vue resolve.');
`);
  run("node", ["views-builds.mjs"], frontend);
  run("node", ["--conditions=development", "views-builds.mjs"], frontend);
  verifyViewsTranslationsVite(frontend, env);
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

const viewsTranslations = "Runic.Application.Views.Translations";

// A version that Directory.Packages.props pins.
function pinnedPackageVersion(id) {
  const versions = readFileSync(resolve(root, "Directory.Packages.props"), "utf8");
  return new RegExp(`<PackageVersion Include="${id.replaceAll(".", "\\.")}" Version="([^"]+)"`).exec(versions)?.[1]
    ?? assert.fail(`Directory.Packages.props pins no ${id}`);
}

// The npm override path: a page's Vite build whose runic.json overrides runic-views messages and adds French, with
// @runic-artifex/vite-plugin-runic-translations' dependencies: "auto", which regenerates the packed package's prebuilt
// catalog modules from its packed sources: tests/fixtures/application/views-translations-vite. It catches a dist/catalog
// that does not match the prebuilt modules, a prebuilt module that index.js imports but the regenerated catalog lacks,
// and a runic.json the plugin rejects.
export function verifyViewsTranslationsVite(frontend, env) {
  const app = join(frontend, "views-translations-vite");
  cpSync(resolve(root, "tests/fixtures/application/views-translations-vite"), app, { recursive: true });
  // The plugin runs dotnet tool run runic-translations, the release that built the package.
  mkdirSync(join(app, ".config"));
  cpSync(resolve(root, ".config/dotnet-tools.json"), join(app, ".config/dotnet-tools.json"));
  run("dotnet", ["tool", "restore"], app, env);
  run("node", [join(frontend, "node_modules/vite/bin/vite.js"), "build"], app, env);
  run("node", ["check.mjs"], app, env);
}

// An application with German and its own wording of single runic-views messages (runic.json "overrides"), built against
// the packed catalog: tests/fixtures/application/views-translations-consumer.
function verifyViewsTranslationsConsumer(directory, env) {
  const consumer = join(directory, "views-translations-consumer");
  cpSync(resolve(root, "tests/fixtures/application/views-translations-consumer"), consumer, { recursive: true });
  writeFileSync(join(consumer, "Consumer.csproj"), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><RunicApplicationFrontendGenerationEnabled>false</RunicApplicationFrontendGenerationEnabled></PropertyGroup><ItemGroup><PackageReference Include="${viewsTranslations}" Version="${packageVersion(workspace, viewsTranslations)}"/><PackageReference Include="Runic.Translations.Build" Version="${pinnedPackageVersion("Runic.Translations.Build")}" PrivateAssets="all"/><PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="${pinnedPackageVersion("Microsoft.Extensions.DependencyInjection")}"/></ItemGroup></Project>`);
  run("dotnet", ["run", "--project", "Consumer.csproj", "--configuration", configuration], consumer, env);
}

// The RUNICBRIDGE shape analyzers of the packed Runic.Application.Views (#962), in a project that gets it through a host
// adapter, whose dependency excludes analyzers, and in one that also references it directly: each loads them once. A
// design-time build, like the IDE's, skips the Bridge generator, and the analyzer reports the error; a build reports it
// once, from the generator, which runs before the compiler and stops the build. A warning is reported once by either.
function verifyViewsAnalyzerConsumers(directory, env) {
  const adapter = "Runic.Application.Views.CsWebUi";
  const expected = "error RUNICBRIDGE009: MainViewModel.Save: Bridge commands must end with Command.";
  for (const direct of [false, true]) {
    const consumer = join(directory, direct ? "views-analyzers-direct" : "views-analyzers-adapter");
    mkdirSync(consumer);
    const references = [adapter, ...(direct ? ["Runic.Application.Views"] : [])]
      .map(name => `<PackageReference Include="${name}" Version="${packageVersion(workspace, name)}"/>`).join("");
    writeFileSync(join(consumer, "Consumer.csproj"), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><RunicApplicationFrontendBuildEnabled>false</RunicApplicationFrontendBuildEnabled></PropertyGroup><ItemGroup>${references}</ItemGroup></Project>`);
    writeFileSync(join(consumer, "MainWindow.cs"), `using System.ComponentModel;
using System.Windows.Input;
using Runic.Application.Views;
namespace AnalyzerConsumer;
public sealed partial class MainWindow : RunicWindow<MainViewModel>;
public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    [RunicCommandInput(typeof(string))] public ICommand Save { get; } = null!;
}
`);
    run("dotnet", ["restore", "Consumer.csproj"], consumer, env);
    for (const designTime of [true, false]) {
      const label = `${direct ? "direct" : "adapter"} ${designTime ? "design-time build" : "build"}`;
      const result = spawnSync("dotnet", ["build", "Consumer.csproj", "--no-restore", "--configuration", configuration,
        ...(designTime ? ["-p:DesignTimeBuild=true"] : [])], { cwd: consumer, encoding: "utf8", env: { ...process.env, ...env } });
      // The errors as the build logs them, before the summary that repeats them; the indented lines are the generator's
      // own output, which the build also logs as messages.
      const reports = `${result.stdout}${result.stderr}`.split(/\r?\nBuild (?:FAILED|succeeded)\./)[0].split(/\r?\n/)
        .filter(line => !/^\s/.test(line) && line.includes(expected));
      assert.notEqual(result.status, 0, `The ${label} of the analyzer consumer succeeded:\n${result.stdout}`);
      assert.equal(reports.length, 1, `The ${label} reported RUNICBRIDGE009 ${reports.length} times:\n${result.stdout}`);
      // The generator's report ends with "See <link>"; the compiler's, the analyzer's, with "(<link>)".
      assert.ok(designTime ? !reports[0].includes(" See https://") : reports[0].includes(" See https://"),
        `The ${label} reported RUNICBRIDGE009 from the ${designTime ? "generator" : "analyzer"}: ${reports[0]}`);
    }
    // A warning that the analyzer owns (BridgeDiagnosticCodes.AnalyzerWarnings): the generator leaves it to the
    // compiler, so a build and a design-time build each report it once, from the analyzer, and TreatWarningsAsErrors and
    // NoWarn apply to it as to any compiler warning.
    writeFileSync(join(consumer, "MainWindow.cs"), `using System.ComponentModel;
using System.Windows.Input;
using Runic.Application.Views;
namespace AnalyzerConsumer;
public sealed partial class MainWindow : RunicWindow<MainViewModel>;
public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    [RunicCommandInput(typeof(string))] public ICommand SaveCommand { get; } = null!;
    [RunicPresentationScoped] public int Total { get; }
}
`);
    const warning = "warning RUNICBRIDGE034: MainViewModel.Total: RunicPresentationScoped applies only to a Bridge command property";
    const cases = [["build", [], 0, warning], ["design-time build", ["-p:DesignTimeBuild=true"], 0, warning],
      ...(direct ? [] : [["build with TreatWarningsAsErrors", ["-p:TreatWarningsAsErrors=true"], 1, warning.replace("warning", "error")],
        ["build with NoWarn", ["-p:NoWarn=RUNICBRIDGE034"], 0, null]])];
    for (const [kind, properties, status, report] of cases) {
      const label = `${direct ? "direct" : "adapter"} ${kind}`;
      // A compiler warning appears only when the compiler runs, so each build compiles again.
      const result = spawnSync("dotnet", ["build", "Consumer.csproj", "--no-restore", "--no-incremental", "--configuration", configuration, ...properties],
        { cwd: consumer, encoding: "utf8", env: { ...process.env, ...env } });
      const output = `${result.stdout}${result.stderr}`;
      const reports = output.split(/\r?\nBuild (?:FAILED|succeeded)\./)[0].split(/\r?\n/)
        .filter(line => !/^\s/.test(line) && line.includes("RUNICBRIDGE034"));
      assert.equal(result.status === 0 ? 0 : 1, status, `The ${label} of the analyzer consumer exited with ${result.status}:\n${output}`);
      assert.equal(reports.length, report ? 1 : 0, `The ${label} reported RUNICBRIDGE034 ${reports.length} times:\n${output}`);
      if (report) assert.ok(reports[0].includes(report) && !reports[0].includes(" See https://"),
        `The ${label} did not report RUNICBRIDGE034 from the analyzer: ${reports[0]}`);
    }
  }
}

// The RUNICPLAT analyzers of the packed Runic.Platform (#972), in a project that gets it only through Runic.Desktop and
// in one that also references it directly: each loads them once and reports RUNICPLAT001 once, in a build and in a
// design-time build like the IDE's. Runic.Desktop's dependency excludes analyzers, but project.assets.json does not
// record that for a transitive package, and the SDK adds the analyzers of every package in it.
function verifyPlatformAnalyzerConsumers(directory, env) {
  const expected = "warning RUNICPLAT001: The DesktopMenuItem title 'file.new' looks like a command id";
  for (const direct of [false, true]) {
    const label = direct ? "direct" : "transitive";
    const consumer = join(directory, `platform-analyzers-${label}`);
    mkdirSync(consumer);
    const references = ["Runic.Desktop", ...(direct ? ["Runic.Platform"] : [])]
      .map(name => `<PackageReference Include="${name}" Version="${packageVersion(workspace, name)}"/>`).join("");
    writeFileSync(join(consumer, "Consumer.csproj"), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>${references}</ItemGroup></Project>`);
    writeFileSync(join(consumer, "Menu.cs"), `using Runic.Platform;
namespace AnalyzerConsumer;
public static class Menu
{
    public static DesktopMenuItem NewWindow() => new("file.new", "New Window");
}
`);
    run("dotnet", ["restore", "Consumer.csproj"], consumer, env);
    const items = JSON.parse(execFileSync("dotnet", ["msbuild", "Consumer.csproj", "-t:ResolveLockFileAnalyzers", "-getItem:Analyzer"],
      { cwd: consumer, encoding: "utf8", env: { ...process.env, ...env } })).Items.Analyzer.map(item => item.Filename);
    for (const name of ["Runic.Platform.Analyzers", "Runic.Platform.Analyzers.CodeFixes"])
      assert.equal(items.filter(item => item === name).length, 1, `The ${label} consumer loads ${name} ${items.filter(item => item === name).length} times: ${items.join(", ")}`);
    for (const designTime of [true, false]) {
      const build = `${label} ${designTime ? "design-time build" : "build"}`;
      // --no-incremental: the warning does not stop the build, so a second build would skip the compiler.
      const result = spawnSync("dotnet", ["build", "Consumer.csproj", "--no-restore", "--no-incremental", "--configuration", configuration,
        ...(designTime ? ["-p:DesignTimeBuild=true"] : [])], { cwd: consumer, encoding: "utf8", env: { ...process.env, ...env } });
      assert.equal(result.status, 0, `The ${build} of the Platform analyzer consumer failed:\n${result.stdout}`);
      // The warnings as the build logs them, before the summary that repeats them.
      const reports = `${result.stdout}${result.stderr}`.split(/\r?\nBuild (?:FAILED|succeeded)\./)[0].split(/\r?\n/)
        .filter(line => line.includes(expected));
      assert.equal(reports.length, 1, `The ${build} reported RUNICPLAT001 ${reports.length} times:\n${result.stdout}`);
    }
  }
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
  assert.ok(readFileSync(join(application, "ApplicationConsumer.csproj"), "utf8").includes("Runic.Application.Views"),
    "Installed application template did not generate the application project");
  run("dotnet", ["tool", "restore", "--configfile", config], application, env);
}
