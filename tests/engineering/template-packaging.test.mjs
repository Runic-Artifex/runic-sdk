import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cpSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";
import { root } from "../../eng/run.mjs";
import { checkApplicationId, fileTypes, fill, manifestFields, numericVersion, parseArguments, typeIdentifier, urlSchemes, webLinkHosts }
  from "../../tools/Runic.Application.Templates/content/runic-app/packaging/package.mjs";

// The runic-app template's packaging/package.mjs: its inputs, and the manifests it fills. The packaging CI job builds,
// installs and opens the packages themselves on each platform.
const packaging = resolve(root, "tools/Runic.Application.Templates/content/runic-app/packaging");
const manifests = {
  windows: ["windows/AppxManifest.xml"],
  macos: ["macos/Info.plist"],
  flatpak: ["linux/app.desktop", "linux/app.metainfo.xml", "linux/flatpak.yml"],
};
const identity = (schemes, types = [], hosts = []) => ({
  id: "org.example.Notes", name: "Notes & Co", version: "2.1.0-preview.3", urlSchemes: schemes, fileTypes: types, webLinkHosts: hosts,
  publisher: "CN=Example Ltd, O=Example Ltd", description: "Notes <for> everyone", homepage: "https://example.org/notes?a=1&b=2",
  executable: "Notes",
});

function parsesAsXml(text, name) {
  const result = spawnSync("python3", ["-c", "import sys, xml.etree.ElementTree as e; e.fromstring(sys.stdin.read())"], { input: text, encoding: "utf8" });
  assert.equal(result.status, 0, `${name} is not well-formed XML: ${result.stderr}\n${text}`);
}

test("package.mjs reads its target and options", () => {
  assert.deepEqual(parseArguments(["windows", "--certificate", "app.pfx", "--timestamp-url", "http://ts"]),
    { target: "windows", project: undefined, output: undefined, rid: undefined, certificate: "app.pfx", timestampUrl: "http://ts" });
  assert.equal(parseArguments(["macos", "--notary-profile", "notary"]).notaryProfile, "notary");
  assert.throws(() => parseArguments(["android"]), /Usage/);
  assert.throws(() => parseArguments(["flatpak", "--gpg-sign"]), /needs a value/);
  assert.throws(() => parseArguments(["flatpak", "extra"]), /Unexpected argument/);
});

test("package.mjs accepts only IDs and schemes every platform registers", () => {
  for (const id of ["org.example.Notes", "com.example.App2048Game", "a.b"]) assert.equal(checkApplicationId(id), id);
  for (const id of ["Notes", "org.example.my-notes", "org.example.2048", "org..example", "org.example.Notes_App", `org.example.${"a".repeat(40)}`])
    assert.throws(() => checkApplicationId(id), /^Error: ApplicationId '/, id);
  assert.deepEqual(urlSchemes(" Org.Example.Notes; notes+x ,"), ["org.example.notes", "notes+x"]);
  assert.deepEqual(urlSchemes(""), []);
  for (const scheme of ["https", "file", "1notes", "no_tes"]) assert.throws(() => urlSchemes(scheme), /RunicApplicationUrlSchemes/, scheme);
});

test("package.mjs accepts file types and web link hosts every platform declares", () => {
  assert.deepEqual(fileTypes(" .Notes=Application/X-Example-Notes; .tar.gz ,.c+x"),
    [{ extension: ".notes", mimeType: "application/x-example-notes" }, { extension: ".tar.gz", mimeType: undefined }, { extension: ".c+x", mimeType: undefined }]);
  assert.deepEqual(fileTypes(""), []);
  for (const entry of ["notes", ".", "..notes", ".notes.", ".no*tes", ".notes=text", ".notes=a/b=c", ".notes;.NOTES"])
    assert.throws(() => fileTypes(entry), /RunicApplicationFileTypes/, entry);
  assert.equal(typeIdentifier("org.example.Notes", ".c+x"), "org.example.Notes.c-x");
  assert.deepEqual(webLinkHosts("Notes.Example.org; links.example.com"), ["notes.example.org", "links.example.com"]);
  for (const host of ["example", "*.example.org", "example.org:443", "https://example.org", "-a.example.org", "a_b.example.org"])
    assert.throws(() => webLinkHosts(host), /RunicApplicationWebLinkHosts/, host);
});

test("package.mjs makes numeric package versions", () => {
  assert.equal(numericVersion("2.1.0-preview.3", 4), "2.1.0.0");
  assert.equal(numericVersion("1.2", 3), "1.2.0");
  assert.equal(numericVersion("1.2.3.4+build", 3), "1.2.3");
  assert.equal(numericVersion(undefined, 4), "1.0.0.0");
});

test("package.mjs fills every field of its manifests, escaped, with and without URL schemes", () => {
  assert.throws(() => fill("{{Unknown}}", {}), /does not fill/);
  for (const [target, files] of Object.entries(manifests))
    for (const schemes of [[], ["org.example.notes", "notes-x"]]) {
      const types = schemes.length ? [{ extension: ".notes", mimeType: "application/x-example-notes" }, { extension: ".tar.gz" }] : [];
      const fields = manifestFields(identity(schemes, types, schemes.length ? ["notes.example.org"] : []), target);
      for (const file of files) {
        const text = fill(readFileSync(resolve(packaging, file), "utf8"), fields);
        assert.doesNotMatch(text, /\{\{|\}\}/, file);
        if (file.endsWith(".xml") || file.endsWith(".plist")) {
          parsesAsXml(text, file);
          assert.match(text, /Notes &amp; Co/, file);
        }
        if (file === "windows/AppxManifest.xml") {
          assert.match(text, /Version="2\.1\.0\.0"/);
          assert.match(text, /Publisher="CN=Example Ltd, O=Example Ltd"/);
          assert.equal((text.match(/<uap:Protocol Name="/g) ?? []).length, schemes.length);
          // The startup task is always declared, so Extensions is never empty.
          assert.match(text, /<desktop:StartupTask TaskId="LoginStartup" Enabled="false" DisplayName="Notes &amp; Co" \/>/);
          assert.match(text, /<desktop:Extension Category="windows.startupTask" Executable="Notes\.exe" EntryPoint="Windows\.FullTrustApplication">/);
          assert.equal((text.match(/<uap3:FileTypeAssociation Name="(notes|tar\.gz)" MultiSelectModel="Player">/g) ?? []).length, types.length);
          assert.equal(/<uap:FileType ContentType="application\/x-example-notes">\.notes<\/uap:FileType>/.test(text), types.length > 0);
          assert.equal(/<uap3:Host Name="notes\.example\.org" \/>/.test(text), schemes.length > 0);
          assert.equal(/windows\.appUriHandler/.test(text), schemes.length > 0, "no handler without hosts");
        }
        if (file === "macos/Info.plist") {
          assert.match(text, /<key>CFBundleShortVersionString<\/key><string>2\.1\.0<\/string>/);
          assert.equal(/CFBundleURLSchemes<\/key><array><string>org\.example\.notes<\/string><string>notes-x<\/string>/.test(text), schemes.length > 0);
          assert.equal((text.match(/<key>LSItemContentTypes<\/key><array><string>org\.example\.Notes\.(notes|tar\.gz)<\/string>/g) ?? []).length, types.length);
          assert.equal((text.match(/<key>UTTypeIdentifier<\/key><string>org\.example\.Notes\.(notes|tar\.gz)<\/string>/g) ?? []).length, types.length);
          assert.equal(/<key>public\.mime-type<\/key><array><string>application\/x-example-notes<\/string>/.test(text), types.length > 0);
          assert.equal(/<array><string>tar\.gz<\/string><\/array>/.test(text), types.length > 0);
        }
        if (file === "linux/app.desktop") {
          assert.match(text, /^Exec=Notes %U$/m);
          assert.equal(text.match(/^MimeType=(.*)$/m)?.[1], schemes.length ? "x-scheme-handler/org.example.notes;x-scheme-handler/notes-x;" : undefined);
          assert.ok(text.endsWith("\n"));
        }
        if (file === "linux/app.metainfo.xml") {
          assert.match(text, /<developer id="org\.example">/);
          assert.match(text, /<url type="homepage">https:\/\/example\.org\/notes\?a=1&amp;b=2<\/url>/);
        }
        if (file === "linux/flatpak.yml") assert.match(text, /^app-id: org\.example\.Notes$/m);
      }
    }
});

test("the packaging probe declares what the template's packaging reads", () => {
  const probe = readFileSync(resolve(root, "tests/native/Runic.Packaging.Probe/Runic.Packaging.Probe.csproj"), "utf8");
  const template = readFileSync(resolve(packaging, "../RunicWindowApp.csproj"), "utf8");
  // W319-003 D17: the identity uses the MSBuild names ApplicationId and ApplicationTitle, which the assembly
  // metadata that ApplicationIdentity.cs reads repeats.
  for (const property of ["ApplicationId", "ApplicationTitle", "Version", "Description", "PackageProjectUrl", "RunicApplicationPublisher", "RunicApplicationUrlSchemes", "RunicApplicationFileTypes"]) {
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
  const packageScript = readFileSync(resolve(packaging, "package.mjs"), "utf8");
  assert.match(packageScript, /"ApplicationId", "ApplicationTitle"/);
  assert.doesNotMatch(packageScript, /RunicApplication(Id|Name)\b/);
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
      ["frontend", "packageManager", "host", "linuxGtk", "viewModels", "tests", "nix", "packaging", "restore", "applicationId"]);
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
    assert.deepEqual(create("gtk3-explicit", "--host", "desktop", "--linux-gtk", "3"), gtk3);
    assert.deepEqual(references(gtk3.project),
      [`Runic.Application.Views.ReactiveUI@${versions.nuget}`, `Runic.Application.Views.Desktop@${versions.nuget}`]);
    assert.match(gtk3.project, /<ApplicationId>com\.example\.Hosts<\/ApplicationId>/);
    assert.match(gtk3.project, /<ApplicationTitle>Hosts<\/ApplicationTitle>/);
    assert.match(gtk3.program, /EmbeddedBackend = LinuxEmbeddedBackend\.Gtk3WebKit41/);
    assert.doesNotMatch(gtk3.program, /Gtk4/);
    assert.match(gtk3.flake, /host = "desktop";/);

    const gtk4 = create("gtk4", "--host", "desktop", "--linux-gtk", "4");
    assert.deepEqual(references(gtk4.project), [`Runic.Application.Views.ReactiveUI@${versions.nuget}`,
      `Runic.Application.Views.Desktop@${versions.nuget}`, `Runic.Desktop.Gtk4@${versions.nuget}`]);
    assert.match(gtk4.program, /using Runic\.Desktop\.Gtk4;/);
    assert.match(gtk4.program, /\.WithGtk4\(ApplicationIdentity\.Id\);/);
    assert.match(gtk4.flake, /host = "desktop-gtk4";/);
    assert.match(gtk4.flake, /hosts = \[ "desktop-gtk4" \];/);

    // #435: CS-WebUI references its preview package at its own version, with either GTK choice.
    const cswebui = create("cswebui", "--host", "cswebui");
    assert.deepEqual(create("cswebui-gtk4", "--host", "cswebui", "--linux-gtk", "4"), cswebui);
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
