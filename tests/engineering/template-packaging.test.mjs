import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cpSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";
import { root } from "../../eng/run.mjs";
import { attachSelects, cdpEndpoint, webView2Debugging } from "../fixtures/application/dev-live-assets/webview2-debugging.mjs";

// The runic-app template's packaging inputs. dotnet runic package (tools/dotnet-runic/Packaging) embeds the template's
// packaging/ manifests and fills them; Runic.Application.Tool.Tests (PackageTests.cs) checks the identity rules and the
// filled manifests, and the packaging CI job builds, installs and opens the packages on each platform.
const packaging = resolve(root, "tools/Runic.Application.Templates/content/runic-app/packaging");
const translationsVersion = JSON.parse(readFileSync(resolve(root, ".config/dotnet-tools.json"), "utf8")).tools["dotnet-runic-translations"].version;
const translationsPlugin = "@runic-artifex/vite-plugin-runic-translations";

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
      ["frontend", "packageManager", "host", "linuxGtk", "desktopServer", "viewModels", "editor", "tests", "translations", "nix", "packaging", "restore", "applicationId"]);
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

// G10: --translations adds one Runic Translations catalog for the page and C#, the C# side of the language and each
// frontend's overlay files, and leaves the projects without it unchanged.
test("--translations scaffolds a translated application for every frontend", () => {
  const directory = mkdtempSync(join(tmpdir(), "runic-template-translations-"));
  try {
    const content = join(directory, "content");
    cpSync(templateContent, content, { recursive: true });
    writeFileSync(join(content, ".template.config/template.json"), stampTemplateJson(join(directory, "stamp")));
    const dotnet = (...args) => {
      const result = spawnSync("dotnet", [...args, "--debug:custom-hive", join(directory, "hive")],
        { cwd: directory, encoding: "utf8", env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: "1", DOTNET_NOLOGO: "1" } });
      assert.equal(result.status, 0, `dotnet ${args.join(" ")}\n${result.stdout}${result.stderr}`);
    };
    dotnet("new", "install", content);
    const create = (output, ...options) => {
      dotnet("new", "runic-app", "--name", "Translated", "--output", output, "--tests", ...options);
      return Object.fromEntries(readdirSync(join(directory, output), { recursive: true, withFileTypes: true })
        .filter(entry => entry.isFile())
        .map(entry => join(entry.parentPath, entry.name))
        .map(path => [path.slice(join(directory, output).length + 1).replaceAll("\\", "/"), readFileSync(path, "utf8")]));
    };
    const overlays = {
      react: ["src/translations.ts", "src/main.tsx", "src/App.tsx", "src/pages/WelcomePage.tsx", "src/pages/CounterPage.tsx", "src/App.test.tsx"],
      vue: ["src/translations.ts", "src/main.ts", "src/App.vue", "src/pages/WelcomePage.vue", "src/pages/CounterPage.vue", "src/App.test.ts"],
      svelte: ["src/translations.ts", "src/main.ts", "src/App.svelte", "src/pages/WelcomePage.svelte", "src/pages/CounterPage.svelte", "src/App.test.ts"],
      sveltekit: ["src/lib/translations.ts", "src/hooks.client.ts", "src/routes/+page.svelte", "src/lib/pages/WelcomePage.svelte", "src/lib/pages/CounterPage.svelte", "src/routes/page.test.ts"],
      angular: ["src/translations.ts", "src/main.ts", "src/app/app.ts", "src/app/app.html", "src/app/welcome.ts", "src/app/welcome.html", "src/app/counter.ts", "src/app/counter.html", "src/app/app.spec.ts"],
    };
    for (const [frontend, files] of Object.entries(overlays)) {
      for (const viewModels of ["reactiveui", "toolkit"]) {
        const plain = create(`${frontend}-${viewModels}`, "--frontend", frontend, "--view-models", viewModels);
        const translated = create(`${frontend}-${viewModels}-translations`, "--frontend", frontend, "--view-models", viewModels, "--translations");
        const label = `${frontend} ${viewModels}`;
        // The catalog and the overlays; the plain project has none of it.
        for (const file of ["runic.json", "en.rmf2", "de.rmf2"]) {
          assert.ok(translated[`Frontend/translations/${file}`], `${label}: no Frontend/translations/${file}`);
          assert.ok(!plain[`Frontend/translations/${file}`], `${label}: the plain project has Frontend/translations/${file}`);
        }
        assert.match(translated["Frontend/translations/runic.json"], /"namespace": "Translated"/);
        assert.ok(!plain[files[0]], `${label}: the plain project has ${files[0]}`);
        for (const file of files) {
          assert.ok(translated[`Frontend/${file}`], `${label}: no Frontend/${file}`);
          assert.notEqual(translated[`Frontend/${file}`], plain[`Frontend/${file}`], `${label}: Frontend/${file} is not the translated one`);
        }
        assert.match(translated[`Frontend/${files[0]}`], /configureSharedLocaleResolver/);
        assert.match(translated[`Frontend/${files.at(-1)}`], /setWindowLocale\("de"\)/);
        // The language list comes from the catalog's locales, and the starter's own text from the catalog.
        assert.match(translated[`Frontend/${files[0]}`], /export const languages = locales\.map/);
        for (const file of files.slice(1, -1)) assert.doesNotMatch(translated[`Frontend/${file}`], /"English"|>Counter<|Connecting to the/, `${label}: ${file}`);
        // The C# side: the packages, the catalog and the generated API.
        // The template pins the repository's own Translations (.config/dotnet-tools.json agrees with Directory.Packages.props).
        assert.ok(translated["Translated.csproj"].includes(`<PackageReference Include="Runic.Translations" Version="${translationsVersion}" />`),
          `${label}: Translated.csproj does not pin Runic.Translations ${translationsVersion}`);
        assert.match(translated["Translated.csproj"], /<RunicTranslationProject Include="Frontend\/translations\/runic\.json" \/>/);
        assert.doesNotMatch(plain["Translated.csproj"], /Translations/);
        assert.match(translated["WorkspaceViewModel.cs"], /AppText\.References\.welcome_greeting\(\)/);
        assert.match(translated["WorkspaceViewModel.cs"], viewModels === "reactiveui" ? /ChooseLanguageCommand = / : /ChooseLanguageAsync\(/);
        assert.doesNotMatch(plain["WorkspaceViewModel.cs"], /Translations|AppText/);
        assert.match(translated["WorkspaceServices.cs"], /StartLanguageAsync/);
        assert.match(translated["Program.cs"], /RUNIC_LOCALE/);
        assert.doesNotMatch(plain["Program.cs"] + plain["WorkspaceServices.cs"], /RUNIC_LOCALE|Translation/);
        assert.match(translated["Translated.Tests/WorkspaceWindowTests.cs"], /Deine erste Runic View ist bereit\./);
        // The tool manifest stays JSON and gains the Translations tool.
        const tools = JSON.parse(translated[".config/dotnet-tools.json"]).tools;
        assert.deepEqual(Object.keys(tools), ["dotnet-runic", "dotnet-runic-translations"]);
        assert.equal(tools["dotnet-runic-translations"].version, translationsVersion);
        assert.deepEqual(Object.keys(JSON.parse(plain[".config/dotnet-tools.json"]).tools), ["dotnet-runic"]);
        assert.match(translated[".gitignore"], /Frontend\/\.runic\//);
        // #944: the Vite frontends compile the catalog with the Translations Vite plugin of the same release; the
        // Angular CLI runs no Vite plugins, so Angular imports the ESM that the .NET build mirrors into Frontend/.runic.
        const page = Object.entries(translated).filter(([path]) => path.startsWith("Frontend/src/")).map(([, text]) => text).join("\n");
        if (frontend === "angular") {
          assert.match(translated["Translated.csproj"], /<RunicTranslationsEmitEsm>true<\/RunicTranslationsEmitEsm>/);
          assert.match(translated["Translated.csproj"], /<RunicApplicationFrontendTranslations>true<\/RunicApplicationFrontendTranslations>/);
          assert.match(page, /from "\.\.\/\.runic\/translations\/messages\.js"/);
          assert.equal(translated["Frontend/package.json"], plain["Frontend/package.json"]);
        } else {
          assert.doesNotMatch(translated["Translated.csproj"], /RunicTranslationsEmitEsm|RunicApplicationFrontendTranslations/);
          assert.doesNotMatch(page, /\.runic\/translations\/[a-z]+\.js/);
          assert.match(page, /from "virtual:runic-translations\/app"/);
          assert.match(page, /\/\/\/ <reference path="(\.\.\/)+\.runic\/translations\/virtual\.d\.ts" \/>/);
          assert.match(translated["Frontend/vite.config.ts"], /^    runicTranslations\(\),$/m);
          // The frontend's own package.json and lock with the plugin, pinned to the release of the .NET tool.
          const manifest = JSON.parse(translated["Frontend/package.json"]);
          assert.equal(manifest.devDependencies[translationsPlugin], translationsVersion);
          delete manifest.devDependencies[translationsPlugin];
          assert.deepEqual(manifest, JSON.parse(plain["Frontend/package.json"]));
          const lock = JSON.parse(translated["Frontend/package-lock.json"]);
          assert.equal(lock.packages[`node_modules/${translationsPlugin}`].version, translationsVersion);
          assert.ok(!translated["Frontend/pnpm-lock.yaml"] && !translated["Frontend/bun.lock"], `${label}: other lock files`);
          assert.doesNotMatch(plain["Frontend/package.json"] + plain["Frontend/package-lock.json"] + plain["Frontend/vite.config.ts"], /translations/i);
        }
        for (const [path, text] of Object.entries(translated))
          assert.doesNotMatch(text, /#if \(translations\)|\/\/#endif/, `${label}: ${path} keeps a template condition`);
      }
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

// #944: --translations replaces a Vite frontend's package.json and lock files with runic-app/translations/<framework>'s,
// which add the Translations Vite plugin; eng/dependencies/update-template-locks.mjs regenerates both sets together.
// Each is the frontend's own with the plugin added, so the two cannot drift apart.
test("the translated Vite frontends' package.json and locks are the frontend's own with the Translations Vite plugin", () => {
  const content = resolve(root, "tools/Runic.Application.Templates/content/runic-app");
  for (const framework of ["react", "vue", "svelte", "sveltekit"]) {
    const read = (variant, file) => readFileSync(resolve(content, variant, framework, file), "utf8");
    const manifest = JSON.parse(read("translations", "package.json"));
    assert.equal(manifest.devDependencies[translationsPlugin], translationsVersion, framework);
    delete manifest.devDependencies[translationsPlugin];
    assert.deepEqual(manifest, JSON.parse(read("frontends", "package.json")), framework);
    for (const file of ["package-lock.json", "pnpm-lock.yaml", "bun.lock"]) {
      const translated = read("translations", file).split("\n");
      assert.ok(translated.some(line => line.includes(`${translationsPlugin}@${translationsVersion}`) ||
        line.includes(`vite-plugin-runic-translations-${translationsVersion}.tgz`)), `${framework}/${file}: no plugin ${translationsVersion}`);
      // Every line of the frontend's lock, in order: the translated lock only adds the plugin.
      let next = 0;
      for (const line of read("frontends", file).split("\n")) {
        while (next < translated.length && translated[next] !== line) next++;
        assert.ok(next < translated.length, `${framework}/${file} lacks the frontend's line ${JSON.stringify(line)}`);
        next++;
      }
    }
  }
});

// tasks.json and launch.json are JSONC; the template's own comments are whole lines.
const parseJsonc = text => JSON.parse(text.split("\n").filter(line => !/^\s*\/\//.test(line)).join("\n"));
// VS Code's built-in $msCompile pattern (tests/dotnet/Runic.Application.Tool.Tests/DevIdeContractTests.cs).
const msCompile = /^\s*(?:\s*\d+>)?(\S.*?)(?:\((\d+|\d+,\d+|\d+,\d+,\d+,\d+)\))?\s*:\s+(?:(\S+)\s+)?((?:fatal +)?error|warning|info)\s+(\w+\d+)?\s*:\s*(.*)$/;

// #961 (D066.25): every project has launch profiles for Visual Studio, Rider and dotnet run; --editor vscode adds
// .vscode/ for the dotnet runic dev IDE contract (#959) and the IDE-run Window (#960); ide.host.json and an icon for
// Visual Studio's New Project and Add New Item dialogs.
test("--editor vscode adds VS Code tasks and launch configurations; every project has launch profiles", () => {
  const directory = mkdtempSync(join(tmpdir(), "runic-template-ide-"));
  try {
    const content = join(directory, "content");
    cpSync(templateContent, content, { recursive: true });
    writeFileSync(join(content, ".template.config/template.json"), stampTemplateJson(join(directory, "stamp")));
    const dotnet = (...args) => {
      const result = spawnSync("dotnet", [...args, "--debug:custom-hive", join(directory, "hive")],
        { cwd: directory, encoding: "utf8", env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: "1", DOTNET_NOLOGO: "1" } });
      assert.equal(result.status, 0, `dotnet ${args.join(" ")}\n${result.stdout}${result.stderr}`);
    };
    dotnet("new", "install", content);
    const create = (output, ...options) => {
      dotnet("new", "runic-app", "--name", "Ide.App", "--output", output, ...options);
      return Object.fromEntries(readdirSync(join(directory, output), { recursive: true, withFileTypes: true })
        .filter(entry => entry.isFile())
        .map(entry => join(entry.parentPath, entry.name))
        .map(path => [path.slice(join(directory, output).length + 1).replaceAll("\\", "/"), readFileSync(path, "utf8")]));
    };
    const framework = /<TargetFramework>([^<]+)<\/TargetFramework>/.exec(readFileSync(resolve(templateContent, "RunicWindowApp.csproj"), "utf8"))[1];
    // The port pattern dotnet runic dev reads for the session file's debugEndpoints (tools/dotnet-runic/DevSession.cs).
    const sessionPort = new RegExp(/GeneratedRegex\(@"(.+)"\)\]\s*\n\s*private static partial Regex RemoteDebuggingPort/
      .exec(readFileSync(resolve(root, "tools/dotnet-runic/DevSession.cs"), "utf8"))?.[1].replace("(?<port>", "(") ?? "^$");
    const environment = { DOTNET_ENVIRONMENT: "Development" };

    const plain = create("plain");
    assert.ok(!Object.keys(plain).some(path => path.startsWith(".vscode/")), "a project without --editor has .vscode/");
    assert.deepEqual(create("none", "--editor", "none"), plain);
    for (const [path, text] of Object.entries(plain)) assert.doesNotMatch(text, /\/\/#(if|else|endif)/, path);

    // The first profile is the one dotnet run uses (D066.25 2). Windows: WebView2's DevTools Protocol for script
    // debugging (D066.25 3); WebView2 appends the variable to Runic's own AdditionalBrowserArguments.
    const profiles = JSON.parse(plain["Properties/launchSettings.json"]).profiles;
    assert.deepEqual(Object.keys(profiles), ["Ide.App", "Ide.App (WebView2 script debugging)"]);
    assert.deepEqual(profiles["Ide.App"], { commandName: "Project", environmentVariables: environment });
    const webView2Profile = profiles["Ide.App (WebView2 script debugging)"];
    assert.equal(webView2Profile.commandName, "Project");
    assert.equal(webView2Profile.jsWebView2Debugging, true);
    const webView2Arguments = webView2Profile.environmentVariables.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS;
    assert.deepEqual(webView2Profile.environmentVariables, { ...environment, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: webView2Arguments });
    const port = Number(/--remote-debugging-port=(\d+)/.exec(webView2Arguments)?.[1]);
    assert.ok(port > 0, webView2Arguments);
    assert.match(webView2Arguments, sessionPort, "dotnet runic dev would not list the port in debugEndpoints");

    const vscode = create("vscode", "--editor", "vscode");
    assert.deepEqual(Object.keys(vscode).filter(path => path.startsWith(".vscode/")).sort(),
      [".vscode/extensions.json", ".vscode/launch.json", ".vscode/tasks.json"]);
    for (const [path, text] of Object.entries(vscode)) assert.doesNotMatch(text, /\/\/#(if|else|endif)/, path);
    assert.equal(vscode["Properties/launchSettings.json"], plain["Properties/launchSettings.json"]);

    // The C# extension's debugger; C# Dev Kit is optional, so no "dotnet" configurations.
    const extensions = parseJsonc(vscode[".vscode/extensions.json"]);
    assert.deepEqual(extensions.recommendations, ["ms-dotnettools.csharp"]);

    const tasks = parseJsonc(vscode[".vscode/tasks.json"]);
    assert.equal(tasks.version, "2.0.0");
    const task = Object.fromEntries(tasks.tasks.map(entry => [entry.label, entry]));
    assert.deepEqual(Object.keys(task), ["dotnet: tool restore", "runic: dev", "runic: dev --no-window", "build", "runic: window", "runic: doctor"]);
    for (const entry of tasks.tasks) {
      for (const dependency of [entry.dependsOn ?? []].flat()) assert.ok(task[dependency], `${entry.label} depends on ${dependency}`);
      if (entry.command) assert.deepEqual([entry.type, entry.command], ["process", "dotnet"], entry.label);
    }
    assert.deepEqual(task["runic: dev"].args, ["runic", "dev"]);
    assert.deepEqual(task["runic: dev --no-window"].args, ["runic", "dev", "--no-window"]);
    assert.deepEqual(task["build"].args.slice(0, 2), ["build", "${workspaceFolder}/Ide.App.csproj"]);
    assert.equal(task["build"].problemMatcher, "$msCompile");
    assert.deepEqual(task["runic: window"].dependsOn, ["runic: dev --no-window", "build"]);
    assert.equal(task["runic: window"].dependsOrder, "sequence");

    // The background tasks: $msCompile for dev's RAPPDEV lines and the build's, ready at each [dev] Ready (#959).
    const diagnostics = [
      ["/src/Ide.App/Ide.App.csproj : error RAPPDEV1007: The vite development server exited unexpectedly with code 1.", "RAPPDEV1007"],
      ["dotnet-runic : error RAPPDEV1014: --no-window cannot be combined with --no-dotnet-watch.", "RAPPDEV1014"],
      ["/src/Ide.App/WorkspaceViewModel.cs(18,5): error CS1002: ; expected [/src/Ide.App/Ide.App.csproj]", "CS1002"],
    ];
    for (const label of ["runic: dev", "runic: dev --no-window"]) {
      const { isBackground, problemMatcher: { base, background } } = task[label];
      assert.equal(isBackground, true, label);
      assert.equal(base, "$msCompile", label);
      assert.equal(background.activeOnStart, true, label);
      for (const [line, code] of diagnostics) assert.equal(msCompile.exec(line)?.[5], code, line);
      const begins = new RegExp(background.beginsPattern), ends = new RegExp(background.endsPattern);
      assert.match("[dev] Evaluating Views Window project...", begins, label);
      assert.match("[dev] Ready", ends, label);
      for (const line of ["[dev] Ready to build", "[dev] Window started: process 4242", "[dev] Frontend: vite"])
        assert.doesNotMatch(line, ends, `${label}: ${line}`);
    }
    assert.match("[dev] Window exited: process 4242", new RegExp(task["runic: dev"].problemMatcher.background.beginsPattern));
    assert.match("[dev] Reloading the Runic Views Window application.", new RegExp(task["runic: dev"].problemMatcher.background.beginsPattern));

    const launch = parseJsonc(vscode[".vscode/launch.json"]);
    assert.equal(launch.version, "0.2.0");
    const configuration = Object.fromEntries(launch.configurations.map(entry => [entry.name, entry]));
    assert.deepEqual(Object.keys(configuration), ["Window", "Attach to the Window (dotnet runic dev)",
      "Window with WebView2 debugging (Windows)", "Attach to WebView2 (Windows)"]);
    for (const entry of [...launch.configurations, ...launch.compounds])
      if (entry.preLaunchTask) assert.ok(task[entry.preLaunchTask], `${entry.name}: ${entry.preLaunchTask}`);
    assert.ok(launch.configurations.every(entry => entry.type === "coreclr" || entry.type === "msedge"));
    const program = `\${workspaceFolder}/bin/Debug/${framework}/Ide.App`;
    assert.deepEqual(configuration["Window"], {
      name: "Window", type: "coreclr", request: "launch", preLaunchTask: "runic: window", program,
      windows: { program: `${program}.exe` }, cwd: "${workspaceFolder}", env: environment, console: "internalConsole",
    });
    assert.equal(configuration["Attach to the Window (dotnet runic dev)"].request, "attach");
    assert.equal(configuration["Attach to the Window (dotnet runic dev)"].preLaunchTask, "runic: dev");
    // Windows: the Window with the same variable as the launch profile, and js-debug on the same port.
    const webView2Window = configuration["Window with WebView2 debugging (Windows)"];
    assert.equal(webView2Window.program, `${program}.exe`);
    assert.deepEqual(webView2Window.env, webView2Profile.environmentVariables);
    const webView2 = configuration["Attach to WebView2 (Windows)"];
    assert.deepEqual([webView2.type, webView2.request, webView2.port, webView2.useWebView], ["msedge", "attach", port, true]);
    assert.deepEqual(launch.compounds, [{
      name: "Window + WebView2 (Windows)", configurations: ["Window with WebView2 debugging (Windows)", "Attach to WebView2 (Windows)"],
      preLaunchTask: "runic: window", stopAll: true,
    }]);
    // The endpoint that tests/fixtures/application/dev-live-assets/webview2-debug-smoke.mjs proves against WebView2 on
    // Windows with the same rules, and that dev's session file lists: js-debug's attach reaches it and selects the page.
    const debugging = webView2Debugging(JSON.parse(plain["Properties/launchSettings.json"]), launch);
    assert.deepEqual([debugging.profileName, debugging.port, debugging.endpoint, debugging.windowConfiguration, debugging.attach.name],
      ["Ide.App (WebView2 script debugging)", port, cdpEndpoint(port), "Window with WebView2 debugging (Windows)", "Attach to WebView2 (Windows)"]);
    assert.equal(cdpEndpoint(port), `http://127.0.0.1:${port}/`);
    assert.ok(attachSelects(webView2, { type: "page", url: "http://127.0.0.1:49152/index.html" }));
    assert.ok(!attachSelects(webView2, { type: "service_worker", url: "http://127.0.0.1:49152/sw.js" }));

    // CS-WebUI has no WebView2 of its own: no script-debugging profile or configurations.
    const cswebui = create("cswebui", "--editor", "vscode", "--host", "cswebui");
    assert.deepEqual(Object.keys(JSON.parse(cswebui["Properties/launchSettings.json"]).profiles), ["Ide.App"]);
    const cswebuiLaunch = parseJsonc(cswebui[".vscode/launch.json"]);
    assert.deepEqual(cswebuiLaunch.configurations.map(entry => entry.name), ["Window", "Attach to the Window (dotnet runic dev)"]);
    assert.ok(!("compounds" in cswebuiLaunch));
    assert.equal(cswebui[".vscode/tasks.json"], vscode[".vscode/tasks.json"]);
    for (const [path, text] of Object.entries(cswebui)) assert.doesNotMatch(text, /\/\/#(if|else|endif)/, path);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("Visual Studio's host file shows the templates' parameters with an icon", () => {
  for (const name of ["runic-app", "runic-view"]) {
    const config = resolve(root, "tools/Runic.Application.Templates/content", name, ".template.config");
    const host = JSON.parse(readFileSync(resolve(config, "ide.host.json"), "utf8"));
    const symbols = JSON.parse(readFileSync(resolve(config, "template.json"), "utf8")).symbols;
    const parameters = Object.keys(symbols).filter(key => symbols[key].type === "parameter");
    assert.equal(host.$schema, "https://json.schemastore.org/ide.host.json", name);
    assert.equal(host.defaultSymbolVisibility, true, name);
    // Every parameter once, in the order dotnet new lists them.
    assert.deepEqual(host.symbolInfo.map(info => info.id), parameters, name);
    const icon = readFileSync(resolve(config, host.icon));
    assert.equal(icon.subarray(1, 4).toString(), "PNG", name);
    assert.deepEqual([icon.readUInt32BE(16), icon.readUInt32BE(20)], [64, 64], name);
  }
  const app = JSON.parse(readFileSync(resolve(templateContent, ".template.config/ide.host.json"), "utf8"));
  // Visual Studio restores the projects it opens.
  assert.deepEqual(app.symbolInfo.filter(info => info.isVisible === false).map(info => info.id), ["restore"]);
});
