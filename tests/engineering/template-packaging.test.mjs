import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cpSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";
import { root } from "../../eng/run.mjs";

// The runic-app template's packaging inputs. dotnet runic package (tools/dotnet-runic/Packaging) embeds the template's
// packaging/ manifests and fills them; Runic.Application.Tool.Tests (PackageTests.cs) checks the identity rules and the
// filled manifests, and the packaging CI job builds, installs and opens the packages on each platform.
const packaging = resolve(root, "tools/Runic.Application.Templates/content/runic-app/packaging");

test("the template's packaging folder holds the manifests the command embeds, without package.mjs", () => {
  const files = readdirSync(packaging, { recursive: true, withFileTypes: true }).filter(entry => entry.isFile())
    .map(entry => join(entry.parentPath, entry.name).slice(packaging.length + 1).replaceAll("\\", "/")).sort();
  assert.deepEqual(files, ["linux/app.desktop", "linux/app.metainfo.xml", "linux/app.service", "linux/flatpak.yml",
    "macos/Info.plist", "macos/entitlements.plist", "windows/AppxManifest.xml"]);
  const tool = readFileSync(resolve(root, "tools/dotnet-runic/Runic.Application.Tool.csproj"), "utf8");
  for (const file of files)
    assert.match(tool, new RegExp(`<EmbeddedResource Include="\\.\\./Runic\\.Application\\.Templates/content/runic-app/packaging/${file.replaceAll(".", "\\.")}"`), file);
  for (const file of files) assert.doesNotMatch(readFileSync(resolve(packaging, file), "utf8"), /package\.mjs/, file);
});

test("the template's flatpak manifest lets the app reach the global menu registrar", () => {
  const manifest = readFileSync(resolve(packaging, "linux/flatpak.yml"), "utf8");
  assert.match(manifest, /^  - --talk-name=com\.canonical\.AppMenu\.Registrar$/m);
});

test("the packaging probe declares what the template's packaging reads", () => {
  const probe = readFileSync(resolve(root, "tests/native/Runic.Packaging.Probe/Runic.Packaging.Probe.csproj"), "utf8");
  const template = readFileSync(resolve(packaging, "../RunicWindowApp.csproj"), "utf8");
  // W319-003 D17: the identity uses the MSBuild names ApplicationId and ApplicationTitle, which the assembly
  // metadata that ApplicationIdentity.cs reads repeats.
  for (const property of ["ApplicationId", "ApplicationTitle", "Version", "Description", "PackageProjectUrl", "RunicApplicationPublisher", "RunicApplicationUrlSchemes", "RunicApplicationFileTypes", "RunicApplicationDBusActivatable"]) {
    assert.match(probe, new RegExp(`<${property}>[^<]+</${property}>`), property);
    assert.match(template, new RegExp(`<${property}>`), property);
  }
  for (const project of [probe, template]) {
    assert.match(project, /<AssemblyMetadata Include="ApplicationId" Value="\$\(ApplicationId\)" \/>/);
    assert.match(project, /<AssemblyMetadata Include="ApplicationTitle" Value="\$\(ApplicationTitle\)" \/>/);
    assert.doesNotMatch(project, /RunicApplication(Id|Name)\b/);
  }
  const identitySource = readFileSync(resolve(packaging, "../ApplicationIdentity.cs"), "utf8");
  assert.match(identitySource, /Read\("ApplicationId"\)/);
  assert.match(identitySource, /Read\("ApplicationTitle"\)/);
  const identityReader = readFileSync(resolve(root, "tools/dotnet-runic/Packaging/PackageIdentity.cs"), "utf8");
  assert.match(identityReader, /"ApplicationId", "ApplicationTitle"/);
  assert.doesNotMatch(identityReader, /RunicApplication(Id|Name)\b/);
  assert.match(probe, /<Compile Include="\.\.\/\.\.\/\.\.\/tools\/Runic\.Application\.Templates\/content\/runic-app\/ApplicationIdentity\.cs"/);
});

// tmpl-01: Runic.Application.Views.CsWebUi is the preview package and ships on a prerelease version of its own, so
// --host cswebui must not reference the release train's version. tests/templates/Test-Templates.sh restores the
// cswebui variants from the candidate feed with the stamped default.
test("the cswebui host references the preview package's separately stamped version", () => {
  const content = resolve(root, "tools/Runic.Application.Templates/content/runic-app");
  const project = readFileSync(resolve(content, "RunicWindowApp.csproj"), "utf8");
  const references = [...project.matchAll(/<PackageReference Include="([^"]+)" Version="(__[A-Z_]+__)" \/>/g)]
    .map(([, id, version]) => [id, version]);
  assert.deepEqual(references, [
    ["Runic.Application.Views.ReactiveUI", "__RUNIC_NUGET_VERSION__"],
    ["Runic.Application.Views.Desktop", "__RUNIC_NUGET_VERSION__"],
    ["Runic.Desktop.NativeServer", "__RUNIC_NUGET_VERSION__"],
    ["Runic.Desktop.Kestrel", "__RUNIC_NUGET_VERSION__"],
    ["Runic.Desktop.Gtk4", "__RUNIC_NUGET_VERSION__"],
    ["Runic.Application.Views.CsWebUi", "__RUNIC_CSWEBUI_NUGET_VERSION__"],
  ]);
  const symbols = JSON.parse(readFileSync(resolve(content, ".template.config/template.json"), "utf8")).symbols;
  assert.deepEqual(symbols.runicCsWebUiVersion, {
    type: "generated", generator: "constant", parameters: { value: "__RUNIC_CSWEBUI_NUGET_VERSION_DEFAULT__" },
    replaces: "__RUNIC_CSWEBUI_NUGET_VERSION__", description: symbols.runicCsWebUiVersion.description,
  });

  // The template pack stamps the default from the version the package itself ships with.
  const templates = resolve(root, "tools/Runic.Application.Templates/Runic.Application.Templates.csproj");
  assert.match(readFileSync(templates, "utf8"), /\.Replace\('__RUNIC_CSWEBUI_NUGET_VERSION_DEFAULT__', '\$\(RunicCsWebUiNuGetTemplateVersion\)'\)/);
  const evaluate = (path, properties, extra = []) => {
    const result = spawnSync("dotnet", ["msbuild", path, "-nologo", `-getProperty:${properties.join(",")}`, ...extra],
      { cwd: root, encoding: "utf8" });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    return properties.length === 1 ? { [properties[0]]: result.stdout.trim() } : JSON.parse(result.stdout).Properties;
  };
  const cswebui = resolve(root, "packages/dotnet/Runic.Application.Views.CsWebUi/Runic.Application.Views.CsWebUi.csproj");
  const preview = ["-p:RunicSdkVersion=1.0.0", "-p:RunicCsWebUiPackageVersion=1.0.0-preview.1"];
  assert.deepEqual(evaluate(templates, ["RunicNuGetTemplateVersion", "RunicCsWebUiNuGetTemplateVersion"], preview),
    { RunicNuGetTemplateVersion: "1.0.0", RunicCsWebUiNuGetTemplateVersion: "1.0.0-preview.1" });
  assert.deepEqual(evaluate(cswebui, ["PackageVersion"], preview), { PackageVersion: "1.0.0-preview.1" });
  // Without overrides, the template uses the preview package's version from eng/workspace.json.
  const workspace = JSON.parse(readFileSync(resolve(root, "eng/workspace.json"), "utf8"));
  const shipped = workspace.nuget.find(entry => entry.name === "Runic.Application.Views.CsWebUi")?.version ?? workspace.version;
  const train = evaluate(templates, ["RunicSdkVersion", "RunicCsWebUiNuGetTemplateVersion"]);
  assert.deepEqual(train, { RunicSdkVersion: workspace.version, RunicCsWebUiNuGetTemplateVersion: shipped });
});

const templateProject = resolve(root, "tools/Runic.Application.Templates/Runic.Application.Templates.csproj");
const templateContent = resolve(root, "tools/Runic.Application.Templates/content/runic-app");
const versions = { nuget: "9.8.7", cswebui: "9.8.7-preview.1", npm: "9.8.6" };

// Runs the pack's StampTemplateJson target into a temporary directory and returns the template.json it ships.
function stampTemplateJson(directory) {
  const result = spawnSync("dotnet", ["msbuild", templateProject, "-nologo", "-v:q", "-t:StampTemplateJson",
    `-p:TemplatePackContentRoot=${directory}/`, `-p:RunicNuGetTemplateVersion=${versions.nuget}`,
    `-p:RunicCsWebUiNuGetTemplateVersion=${versions.cswebui}`, `-p:RunicNpmTemplateVersion=${versions.npm}`],
    { cwd: root, encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  return readFileSync(join(directory, "runic-app/.template.config/template.json"), "utf8");
}

// W319-003 D19 tmpl-04: the shipped template has no hidden or version parameters. The pack stamps the versions it
// was built with into the content, so a candidate for other versions is packed with -p:RunicNuGetTemplateVersion=...
test("the shipped template has no hidden or version parameters, and the pack stamps the versions", () => {
  const host = JSON.parse(readFileSync(resolve(templateContent, ".template.config/dotnetcli.host.json"), "utf8"));
  const directory = mkdtempSync(join(tmpdir(), "runic-template-stamp-"));
  try {
    const shipped = stampTemplateJson(directory);
    assert.doesNotMatch(shipped, /__[A-Z_]+_DEFAULT__|__[A-Z_]+_PACKAGE_MANAGER_VERSION__/);
    const symbols = JSON.parse(shipped).symbols;
    const parameters = Object.entries(symbols).filter(([, symbol]) => symbol.type === "parameter").map(([name]) => name);
    assert.deepEqual(parameters,
      ["frontend", "packageManager", "host", "linuxGtk", "desktopServer", "viewModels", "tests", "nix", "packaging", "restore", "applicationId"]);
    for (const name of parameters) {
      assert.doesNotMatch(`${name} ${symbols[name].replaces ?? ""}`, /version/i, `${name} sets a version`);
      assert.ok(host.symbolInfo[name], `${name} has no dotnetcli.host.json entry`);
    }
    assert.deepEqual(Object.keys(host.symbolInfo), parameters);
    assert.ok(Object.values(host.symbolInfo).every(info => !("isHidden" in info)), "dotnetcli.host.json hides a parameter");
    assert.doesNotMatch(JSON.stringify(host), /runic-(cswebui-|npm-)?version/);
    // The versions are constants the template replaces in the content; nothing on the command line changes them.
    for (const [name, token, version] of [
      ["runicVersion", "__RUNIC_NUGET_VERSION__", versions.nuget],
      ["runicCsWebUiVersion", "__RUNIC_CSWEBUI_NUGET_VERSION__", versions.cswebui],
      ["runicNpmVersion", "__RUNIC_NPM_VERSION__", versions.npm],
    ]) {
      assert.equal(symbols[name].type, "generated", name);
      assert.equal(symbols[name].generator, "constant", name);
      assert.equal(symbols[name].replaces, token, name);
      assert.equal(symbols[name].parameters.value, version, name);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

// W319-003 D19 tmpl-03: --host desktop|cswebui and --linux-gtk 3|4. --host desktop --linux-gtk 4 is the former
// --host desktop-gtk4; CS-WebUI has no GTK choice, so --linux-gtk leaves its project unchanged. The projects come from a
// copy of the content with the stamped template.json, as the pack ships it.
test("--linux-gtk 4 selects GTK 4 for Runic Desktop and leaves CS-WebUI unchanged", () => {
  const directory = mkdtempSync(join(tmpdir(), "runic-template-hosts-"));
  try {
    const content = join(directory, "content");
    cpSync(templateContent, content, { recursive: true });
    writeFileSync(join(content, ".template.config/template.json"), stampTemplateJson(join(directory, "stamp")));
    const dotnet = (...args) => {
      const result = spawnSync("dotnet", [...args, "--debug:custom-hive", join(directory, "hive")],
        { cwd: directory, encoding: "utf8", env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: "1", DOTNET_NOLOGO: "1" } });
      assert.equal(result.status, 0, `dotnet ${args.join(" ")}\n${result.stdout}${result.stderr}`);
      return result.stdout;
    };
    dotnet("new", "install", content);
    // Every generated file by path, with the project file, Program.cs and flake.nix by name.
    const create = (output, ...options) => {
      dotnet("new", "runic-app", "--name", "Hosts", "--output", output, "--nix", "--packaging", "--tests", ...options);
      const files = Object.fromEntries(readdirSync(join(directory, output), { recursive: true, withFileTypes: true })
        .filter(entry => entry.isFile())
        .map(entry => join(entry.parentPath, entry.name))
        .map(path => [path.slice(join(directory, output).length + 1), readFileSync(path, "utf8")]));
      return { files, project: files["Hosts.csproj"], program: files["Program.cs"], flake: files["flake.nix"] };
    };
    const references = project => [...project.matchAll(/<PackageReference Include="(Runic[^"]+)" Version="([^"]+)" \/>/g)]
      .map(([, id, version]) => `${id}@${version}`);

    const gtk3 = create("gtk3");
    assert.deepEqual(create("gtk3-explicit", "--host", "desktop", "--linux-gtk", "3", "--desktop-server", "native"), gtk3);
    // The native Desktop server by default (D053.24); it is no longer experimental (D053.26), so nothing is suppressed.
    assert.deepEqual(references(gtk3.project),
      [`Runic.Application.Views.ReactiveUI@${versions.nuget}`, `Runic.Application.Views.Desktop@${versions.nuget}`,
        `Runic.Desktop.NativeServer@${versions.nuget}`]);
    assert.doesNotMatch(gtk3.project + gtk3.files["Hosts.Tests/Hosts.Tests.csproj"], /RUNICDESK102/);
    const kestrel = create("kestrel", "--desktop-server", "kestrel");
    assert.deepEqual(references(kestrel.project),
      [`Runic.Application.Views.ReactiveUI@${versions.nuget}`, `Runic.Application.Views.Desktop@${versions.nuget}`,
        `Runic.Desktop.Kestrel@${versions.nuget}`]);
    assert.doesNotMatch(kestrel.project, /RUNICDESK102|NativeServer/);
    assert.match(gtk3.project, /<ApplicationId>com\.example\.Hosts<\/ApplicationId>/);
    assert.match(gtk3.project, /<ApplicationTitle>Hosts<\/ApplicationTitle>/);
    assert.match(gtk3.program, /EmbeddedBackend = LinuxEmbeddedBackend\.Gtk3WebKit41/);
    assert.doesNotMatch(gtk3.program, /Gtk4/);
    assert.match(gtk3.flake, /host = "desktop";/);

    const gtk4 = create("gtk4", "--host", "desktop", "--linux-gtk", "4");
    assert.deepEqual(references(gtk4.project), [`Runic.Application.Views.ReactiveUI@${versions.nuget}`,
      `Runic.Application.Views.Desktop@${versions.nuget}`, `Runic.Desktop.NativeServer@${versions.nuget}`,
      `Runic.Desktop.Gtk4@${versions.nuget}`]);
    assert.match(gtk4.program, /using Runic\.Desktop\.Gtk4;/);
    assert.match(gtk4.program, /\.WithGtk4\(ApplicationIdentity\.Id\);/);
    assert.match(gtk4.flake, /host = "desktop-gtk4";/);
    assert.match(gtk4.flake, /hosts = \[ "desktop-gtk4" \];/);

    // #435: CS-WebUI references its preview package at its own version, with either GTK choice.
    const cswebui = create("cswebui", "--host", "cswebui");
    assert.deepEqual(create("cswebui-gtk4", "--host", "cswebui", "--linux-gtk", "4"), cswebui);
    assert.deepEqual(create("cswebui-kestrel", "--host", "cswebui", "--desktop-server", "kestrel"), cswebui);
    assert.deepEqual(references(cswebui.project),
      [`Runic.Application.Views.ReactiveUI@${versions.nuget}`, `Runic.Application.Views.CsWebUi@${versions.cswebui}`]);
    assert.match(cswebui.flake, /host = "cswebui";/);

    const frontend = gtk3.files["Frontend/package.json"];
    assert.match(frontend, new RegExp(`"@runic-artifex/views": "${versions.npm.replaceAll(".", "\\.")}"`));
    assert.match(gtk3.files[".config/dotnet-tools.json"], new RegExp(`"version": "${versions.nuget.replaceAll(".", "\\.")}"`));

    for (const removed of [["--host", "desktop-gtk4"], ["--runic-version", "1.0.0"], ["--linux-gtk", "5"]]) {
      const result = spawnSync("dotnet", ["new", "runic-app", "--name", "Removed", "--output", "removed", ...removed,
        "--debug:custom-hive", join(directory, "hive")], { cwd: directory, encoding: "utf8" });
      assert.notEqual(result.status, 0, `dotnet new runic-app ${removed.join(" ")} succeeded`);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
