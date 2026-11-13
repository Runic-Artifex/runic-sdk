import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { homedir, tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { root } from "../../eng/run.mjs";

// RunicApplicationFrontendTranslations mirrors Runic Translations' ESM into the frontend for a page that imports it
// without the Vite plugin (the runic-app template's --translations option, the multi-window example). These tests run
// the real Runic.Translations.Build targets and dotnet-runic-translations tool of the repository's pins.
const viewsTargets = resolve(root, "packages/dotnet/Runic.Application.Views/build/Runic.Application.Views.targets");
const version = readFileSync(resolve(root, "Directory.Packages.props"), "utf8")
  .match(/<PackageVersion Include="Runic\.Translations\.Build" Version="([^"]+)"/)[1];
let directory;
let buildPackage;

function run(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, encoding: "utf8" });
  return { status: result.status, output: `${result.stdout}${result.stderr}` };
}

before(() => {
  directory = mkdtempSync(join(tmpdir(), "runic-views-translations-"));
  mkdirSync(join(directory, ".config"));
  cpSync(resolve(root, ".config/dotnet-tools.json"), join(directory, ".config/dotnet-tools.json"));
  // During a train, Runic Translations' release comes from the train feed (eng/ci/train-feed.mjs).
  const feed = process.env.RUNIC_TRAIN_FEED ? ["--add-source", process.env.RUNIC_TRAIN_FEED] : [];
  const restoredTool = run("dotnet", ["tool", "restore", ...feed], directory);
  assert.equal(restoredTool.status, 0, restoredTool.output);
  const packages = process.env.NUGET_PACKAGES || join(homedir(), ".nuget/packages");
  buildPackage = join(packages, "runic.translations.build", version);
  if (!existsSync(buildPackage)) {
    // A cold NuGet cache: restore the package the targets come from.
    const restore = join(directory, "restore");
    mkdirSync(restore);
    writeFileSync(join(restore, "Restore.csproj"), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="Runic.Translations.Build" Version="${version}" /></ItemGroup>
</Project>
`);
    const restored = run("dotnet", ["restore", "Restore.csproj", ...feed.map(item => item === "--add-source" ? "--source" : item),
      ...(feed.length ? ["--source", "https://api.nuget.org/v3/index.json"] : [])], restore);
    assert.equal(restored.status, 0, restored.output);
  }
  assert.ok(existsSync(buildPackage), `Runic.Translations.Build ${version} is in ${packages}`);
});

after(() => {
  rmSync(directory, { recursive: true, force: true });
});

function fixture(name, { catalog = "app", messages = "hello = Hello.\ngoodbye = Goodbye.\n", properties = "" } = {}) {
  const project = join(directory, name);
  const translations = join(project, "frontend/translations");
  mkdirSync(translations, { recursive: true });
  const app = {
    project,
    page: join(project, "frontend/.runic/translations"),
    catalog(name) {
      writeFileSync(join(translations, "runic.json"), JSON.stringify({
        schemaVersion: 1, catalog: name, code: { namespace: "App", className: "AppText" }, baseLocale: "en", locales: ["en"],
      }));
    },
    messages(text) {
      writeFileSync(join(translations, "en.rmf2"), text);
    },
  };
  app.catalog(catalog);
  app.messages(messages);
  const build = join(buildPackage, "build");
  writeFileSync(join(project, "App.proj"), `<Project>
  <Import Project="${build}/Runic.Translations.Build.props" />
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <IntermediateOutputPath>obj/</IntermediateOutputPath>
    <RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/frontend</RunicApplicationFrontendDirectory>
    <RunicTranslationsEmitEsm>true</RunicTranslationsEmitEsm>
    <RunicApplicationFrontendTranslations>true</RunicApplicationFrontendTranslations>
${properties}  </PropertyGroup>
  <ItemGroup>
    <RunicTranslationProject Include="frontend/translations/runic.json" />
  </ItemGroup>
  <Import Project="${viewsTargets}" />
  <Import Project="${build}/Runic.Translations.Build.targets" />
</Project>
`);
  return app;
}

function build({ project }, properties = []) {
  const result = run("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicTranslationsGenerateTranslationArtifacts",
    ...properties.map(property => `-p:${property}`), "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], project);
  const errors = [...new Set(result.output.split("\n").filter(line => / error [A-Z]+\d+:/.test(line))
    .map(line => line.replace(/\s*\[[^\]]*\]\s*$/, "").trim()))];
  return { ...result, errors };
}

const files = directory => existsSync(directory)
  ? readdirSync(directory, { recursive: true, withFileTypes: true }).filter(entry => entry.isFile())
    .map(entry => join(entry.parentPath, entry.name).slice(directory.length + 1).replaceAll("\\", "/")).sort()
  : [];
const messageModules = app => files(app.page).filter(file => file.startsWith("messages/r_"));
const moduleOf = key => `messages/r_${Buffer.from(key).toString("hex")}.js`;
const pageText = app => files(app.page).filter(file => file.endsWith(".js"))
  .map(file => readFileSync(join(app.page, file), "utf8")).join("\n");

test("the page's ESM follows a removed and a renamed message", () => {
  const app = fixture("follows");
  let result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(messageModules(app), [moduleOf("goodbye"), moduleOf("hello")].sort());
  assert.ok(files(app.page).includes("web-module-manifest-v3.json"), files(app.page).join(", "));
  assert.match(pageText(app), /Goodbye\./);

  // Renamed: the old key's module goes, the new one comes.
  app.messages("hello = Hello.\nfarewell = Goodbye.\n");
  result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(messageModules(app), [moduleOf("farewell"), moduleOf("hello")].sort());
  assert.doesNotMatch(readFileSync(join(app.page, "messages.d.ts"), "utf8"), /goodbye/);

  // Removed.
  app.messages("hello = Hello.\n");
  result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(messageModules(app), [moduleOf("hello")]);
  assert.doesNotMatch(pageText(app), /Goodbye\./);

  // An up-to-date build, where the Translations tool does not run, keeps the mirror as it is.
  result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.deepEqual(messageModules(app), [moduleOf("hello")]);
});

test("a renamed catalog's ESM folder replaces the old one", () => {
  const app = fixture("renamed-catalog");
  assert.equal(build(app).status, 0);
  const before = JSON.parse(readFileSync(join(app.page, "web-module-manifest-v3.json"), "utf8"));
  assert.equal(before.catalog, "app");
  app.catalog("notes");
  const result = build(app);
  assert.equal(result.status, 0, result.output);
  assert.equal(JSON.parse(readFileSync(join(app.page, "web-module-manifest-v3.json"), "utf8")).catalog, "notes");
  assert.deepEqual(messageModules(app), [moduleOf("goodbye"), moduleOf("hello")].sort());
});

test("RUNICBRIDGE037: no ESM to copy, and a directory with other files", () => {
  const noEsm = fixture("no-esm");
  let result = build(noEsm, ["RunicTranslationsEmitEsm=false", "RunicTranslationsEmitJson=true"]);
  assert.notEqual(result.status, 0);
  assert.equal(result.errors.length, 1, result.output);
  assert.match(result.errors[0], /error RUNICBRIDGE037: RunicApplicationFrontendTranslations is true, but the build wrote no Runic Translations ESM/);
  assert.deepEqual(files(noEsm.page), []);

  const occupied = fixture("occupied", { properties: "    <RunicApplicationFrontendTranslationsDirectory>frontend/src</RunicApplicationFrontendTranslationsDirectory>\n" });
  mkdirSync(join(occupied.project, "frontend/src"), { recursive: true });
  writeFileSync(join(occupied.project, "frontend/src/main.ts"), "export {};");
  result = build(occupied);
  assert.notEqual(result.status, 0);
  assert.match(result.errors.join("\n"), /error RUNICBRIDGE037: RunicApplicationFrontendTranslationsDirectory .*frontend\/src\/ holds files but no Runic Translations ESM/);
  assert.equal(readFileSync(join(occupied.project, "frontend/src/main.ts"), "utf8"), "export {};", "Nothing is deleted.");
});

test("a catalog with errors leaves the mirror alone and reports only the catalog's errors", () => {
  const app = fixture("catalog-errors");
  assert.equal(build(app).status, 0);
  const mirrored = files(app.page);
  const manifest = readFileSync(join(app.page, "web-module-manifest-v3.json"), "utf8");
  app.messages("hello = {$\n");
  // In a C# project CoreCompile fails on these errors; this project has no compilation.
  const result = build(app);
  assert.match(result.output, /error RTR0066:/);
  assert.doesNotMatch(result.output, /RUNICBRIDGE037/);
  assert.deepEqual(files(app.page), mirrored);
  assert.equal(readFileSync(join(app.page, "web-module-manifest-v3.json"), "utf8"), manifest);
});
