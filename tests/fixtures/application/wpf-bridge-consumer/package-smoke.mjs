// Proves that WPF applications generate their Runic bridge from the packed packages
// alone, with no RunicApplicationFrontendGenerationEnabled, on every platform (EnableWindowsTargeting):
//  1. Single: one UseWPF project that references only Runic.Application.Views.Wpf, declares a
//     RunicView and calls AddRunicViews(). It also has WPF's ThemeInfo and XmlnsDefinition
//     assembly attributes, a [RunicIgnore] ImageSource and an iterator over WPF objects in
//     its ViewModel (#269), and a BackgroundService from Microsoft.AspNetCore.App (#270),
//     none of which the generator can load without its framework. Its local XAML type makes WPF compile a
//     temporary *_wpftmp assembly, which must not generate again. The client lands in
//     Frontend/src/generated and the built frontend in www/. Its XAML names RunicViewHost
//     through https://runic-artifex.eu/xaml/views, Frontend/node_modules and Frontend/dist
//     are not project items, and the output has no Linux or macOS platform assemblies.
//  2. Split: the View lives in a WPF-free model project; the WPF application copies
//     the model's built frontend to its own www/.
//  3. Without a Frontend folder, the single project fails with one RUNICBRIDGE014.
//  4. A library that reaches Runic.Application.Views only through the model project and
//     declares a View gets one RUNICBRIDGE015 warning.
//  5. A copy of the single project that still sets the 0.7.0-preview.6 property names
//     builds with them and gets one RUNICBRIDGE017 warning per old name.
//  6. A copy of the split layout whose model names its composition after the application
//     (a 0.7.0-preview.6 app after the upgrade) builds without CS0121.
//  7. If that application declares a View too, it gets one RUNICBRIDGE018 error.
// Usage: bun tests/fixtures/application/wpf-bridge-consumer/package-smoke.mjs [nuget-feed] [--keep]
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../../../", import.meta.url));
const fixture = fileURLToPath(new URL(".", import.meta.url));
const args = process.argv.slice(2);
const keep = args.includes("--keep");
const feed = resolve(args.find(arg => !arg.startsWith("--")) ?? join(root, "artifacts/packages/nuget"));
const { version } = JSON.parse(readFileSync(join(root, "eng/workspace.json"), "utf8"));
const container = /<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="([^"]+)"/.exec(readFileSync(join(root, "Directory.Packages.props"), "utf8"))?.[1];
assert.ok(container, "Directory.Packages.props has no Microsoft.Extensions.DependencyInjection version.");
for (const id of ["Runic.Application.Views", "Runic.Application.Views.Desktop", "Runic.Application.Views.Wpf"])
  assert.ok(existsSync(join(feed, `${id}.${version}.nupkg`)), `Pack ${id} ${version} into ${feed} first.`);

const directory = mkdtempSync(join(tmpdir(), "runic-wpf-bridge-"));
const env = {
  ...process.env,
  NUGET_PACKAGES: join(directory, "nuget-cache"),
  DOTNET_CLI_HOME: join(directory, "dotnet-home"),
};
// MSBuild repeats each diagnostic in its summary; the summary counts are exact.
function build(project, { fail = false } = {}) {
  const commandArgs = ["build", project, "-c", "Release", "--nologo",
    `-p:RunicPackageVersion=${version}`, `-p:RunicContainerVersion=${container}`];
  console.log(`> dotnet ${commandArgs.join(" ")}`);
  const result = spawnSync("dotnet", commandArgs, { cwd: directory, env, encoding: "utf8" });
  const output = `${result.stdout}${result.stderr}`;
  process.stdout.write(output);
  assert.equal(result.status !== 0, fail, `${project} ${fail ? "built unexpectedly" : "failed"}`);
  return output;
}
const summary = (output, kind) => Number(new RegExp(`^ *(\\d+) ${kind}\\(s\\)`, "m").exec(output)?.[1]);
const count = (output, pattern) => output.split("\n").filter(line => pattern.test(line)).length;
const exists = path => assert.ok(existsSync(join(directory, path)), `${path} is missing`);

try {
  cpSync(fixture, directory, { recursive: true, filter: source => !/[\\/](bin|obj|node_modules|dist|generated)$/.test(source) });
  // The repository's global.json pins the SDK; the temporary tree must use it too.
  cpSync(join(root, "global.json"), join(directory, "global.json"));
  writeFileSync(join(directory, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${feed}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`);

  // 1. Single project: generation is on through the adapter, without duplicate members.
  const single = build("Single/WpfSingle.csproj");
  // The BackgroundService loads from Microsoft.AspNetCore.App, which the build passes
  // to the generator with --framework; without it the type is skipped (RUNICBRIDGE020).
  assert.equal(count(single, /RUNICBRIDGE020|Microsoft\.Extensions\.Hosting\.Abstractions/), 0,
    "The generator did not load the BackgroundService from Microsoft.AspNetCore.App.");
  assert.ok(count(single, /WpfSingle: skipped \d+ types that depend on PresentationCore, PresentationFramework\./) > 0,
    "The generator did not skip the WPF types.");
  assert.equal(summary(single, "Warning"), 0, "The single project reported warnings.");
  exists("Single/Frontend/src/generated/editor.ts");
  exists("Single/obj/Release/net10.0-windows/runic-bridge/RunicBridgeComposition.g.cs");
  exists("Single/bin/Release/net10.0-windows/www/index.html");
  exists("Single/bin/Release/net10.0-windows/www/assets/app.js");
  // A Windows-only application gets the -windows Runic.Application.Views.Desktop, which
  // references only the Windows platform provider.
  const output = readdirSync(join(directory, "Single/bin/Release/net10.0-windows"));
  assert.ok(output.includes("Runic.Platform.Windows.dll"), "The Windows platform provider is missing.");
  assert.deepEqual(output.filter(name => /^(Runic\.Platform\.(Linux|MacOS)|Tmds\.DBus)/.test(name)), [],
    "The Windows-only application ships Linux or macOS platform assemblies.");
  // The frontend's installed packages and build output are not project items.
  mkdirSync(join(directory, "Single/Frontend/node_modules/stand-in"), { recursive: true });
  writeFileSync(join(directory, "Single/Frontend/node_modules/stand-in/package.json"), "{}");
  const items = spawnSync("dotnet", ["msbuild", "Single/WpfSingle.csproj", "-getItem:None", "-getItem:Content", "-getItem:Page",
    "-p:Configuration=Release", `-p:RunicPackageVersion=${version}`, `-p:RunicContainerVersion=${container}`],
  { cwd: directory, env, encoding: "utf8" });
  assert.equal(items.status, 0, `${items.stdout}${items.stderr}`);
  const listed = Object.values(JSON.parse(items.stdout).Items).flat().map(item => item.Identity.replaceAll("\\", "/"));
  assert.ok(listed.includes("Frontend/package.json"), `Frontend/package.json is not a project item: ${listed}`);
  assert.deepEqual(listed.filter(path => /^Frontend\/(node_modules|dist)\//.test(path)), [],
    "Frontend/node_modules or Frontend/dist is listed as a project item.");
  rmSync(join(directory, "Single/Frontend/node_modules"), { recursive: true });

  // 2. Split layout: the model generates, the WPF application copies its frontend.
  assert.equal(summary(build("Split/App/SplitApp.csproj"), "Warning"), 0, "The split layout reported warnings.");
  exists("Split/Frontend/src/generated/editor.ts");
  assert.ok(!existsSync(join(directory, "Split/Model/Frontend")), "The model project wrote a stray Frontend folder.");
  exists("Split/App/bin/Release/net10.0-windows/www/index.html");
  exists("Split/App/bin/Release/net10.0-windows/www/assets/app.js");

  // 3. A missing frontend is one coded error, not MSB6003.
  cpSync(join(directory, "Single"), join(directory, "NoFrontend"), { recursive: true, filter: source => !/[\\/](bin|obj|Frontend)$/.test(source) });
  const missing = build("NoFrontend/WpfSingle.csproj", { fail: true });
  assert.equal(summary(missing, "Error"), 1, "Expected exactly one error.");
  assert.ok(count(missing, /error RUNICBRIDGE014:.*RunicApplicationFrontendDirectory/) > 0, "Expected RUNICBRIDGE014.");
  assert.equal(count(missing, /MSB6003|MSB3073/), 0, "A frontend command ran without a frontend.");

  // 4. Generation off by default with a declared View is one coded warning.
  const library = build("Split/Library/SplitLibrary.csproj");
  assert.equal(summary(library, "Warning"), 1, "Expected exactly one warning.");
  assert.ok(count(library, /warning RUNICBRIDGE015:.*PreviewView\.cs.*RunicApplicationFrontendGenerationEnabled=true/) > 0, "Expected RUNICBRIDGE015.");
  // 5. The 0.7.0-preview.6 property names no longer apply: each fails the build with RUNICBRIDGE017 (W319-003 D15).
  cpSync(join(directory, "Single"), join(directory, "Renamed"), { recursive: true, filter: source => !/[\\/](bin|obj|Frontend)$/.test(source) });
  const renamedProject = join(directory, "Renamed/WpfSingle.csproj");
  const renamedSource = readFileSync(renamedProject, "utf8");
  const newInstall = "<RunicApplicationFrontendInstallEnabled>false</RunicApplicationFrontendInstallEnabled>";
  assert.ok(renamedSource.includes(newInstall), "WpfSingle.csproj no longer sets RunicApplicationFrontendInstallEnabled.");
  writeFileSync(renamedProject, renamedSource.replace(newInstall,
    "<RunicBridgeInstallFrontend>false</RunicBridgeInstallFrontend>" +
    "<RunicBridgeFrontendDir>$(MSBuildProjectDirectory)/Client</RunicBridgeFrontendDir>"));
  const renamed = build("Renamed/WpfSingle.csproj", { fail: true });
  assert.ok(count(renamed, /error RUNICBRIDGE017:.*RunicBridgeFrontendDir is renamed to RunicApplicationFrontendDirectory/) > 0, "Expected RUNICBRIDGE017 for RunicBridgeFrontendDir.");
  assert.ok(count(renamed, /error RUNICBRIDGE017:.*RunicBridgeInstallFrontend is renamed to RunicApplicationFrontendInstallEnabled/) > 0, "Expected RUNICBRIDGE017 for RunicBridgeInstallFrontend.");
  assert.ok(!existsSync(join(directory, "Renamed/Client")), "The old frontend directory name is not used.");

  // 6. A 0.7.0-preview.6 split app upgraded as is: the model generates its composition
  //    under the application's default name, SplitApp.RunicBridgeComposition. The
  //    adapter turns generation on in the application too, whose bootstrap pass then
  //    uses the model's type instead of a stub beside it (CS0121 before W250-049).
  cpSync(join(directory, "Split"), join(directory, "Upgraded"), { recursive: true, filter: source => !/[\\/](bin|obj|dist|generated)$/.test(source) });
  const replaceIn = (path, from, to) => {
    const source = readFileSync(join(directory, path), "utf8");
    assert.ok(source.includes(from), `${path} no longer contains ${from}.`);
    writeFileSync(join(directory, path), source.replace(from, to));
  };
  replaceIn("Upgraded/Model/SplitModel.csproj", "SplitModel.RunicBridgeComposition", "SplitApp.RunicBridgeComposition");
  const upgraded = build("Upgraded/App/SplitApp.csproj");
  assert.equal(summary(upgraded, "Warning"), 0, "The upgraded split layout reported warnings.");
  assert.equal(count(upgraded, /CS0121/), 0, "AddRunicViews() was ambiguous.");
  assert.ok(!existsSync(join(directory, "Upgraded/App/obj/runic-bridge/bootstrap/obj/Release/net10.0-windows/RunicBridgeComposition.Bootstrap.g.cs")),
    "The bootstrap pass wrote a stub beside the model's composition.");
  exists("Upgraded/App/bin/Release/net10.0-windows/www/index.html");

  // 7. If that application declares a View of its own, its generated composition would
  //    clash with the model's: one RUNICBRIDGE018 naming the property, no CS0121.
  writeFileSync(join(directory, "Upgraded/App/StatusView.cs"), `using System.ComponentModel;
using Runic.Application.Views;

namespace SplitApp;

public sealed class StatusViewModel : INotifyPropertyChanged
{
    public string Text { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Raise() => PropertyChanged?.Invoke(this, new(nameof(Text)));
}

public sealed partial class StatusWebView : RunicView<StatusViewModel>;
`);
  replaceIn("Upgraded/App/SplitApp.csproj", "<OutputType>WinExe</OutputType>",
    "<OutputType>WinExe</OutputType><RunicApplicationFrontendBuildEnabled>false</RunicApplicationFrontendBuildEnabled>");
  const clash = build("Upgraded/App/SplitApp.csproj", { fail: true });
  assert.equal(summary(clash, "Error"), 1, "Expected exactly one error.");
  assert.ok(count(clash, /error RUNICBRIDGE018:.*SplitApp\.RunicBridgeComposition.*SplitModel.*RunicApplicationFrontendCompositionType/) > 0, "Expected RUNICBRIDGE018.");
  console.log("WPF_BRIDGE_PACKAGE_OK|single|split|missing-frontend|generation-off|renamed-properties|upgraded-split|composition-clash");
} finally {
  if (keep) console.log(`Kept ${directory}`);
  else rmSync(directory, { recursive: true, force: true, maxRetries: 5 });
}
