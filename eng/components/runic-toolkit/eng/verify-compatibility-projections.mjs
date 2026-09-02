#!/usr/bin/env node
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { compatibilitySetValue } from "./compatibility-set-value.mjs";

const repository = resolve(fileURLToPath(new URL("..", import.meta.url)));
const compatibility = JSON.parse(readFileSync(resolve(repository, "eng/runic.compatibility-set.json"), "utf8"));
const nuget = new Map(
  compatibility.packages
    .filter((entry) => entry.ecosystem === "nuget")
    .map((entry) => [entry.identity, entry.version]),
);
const npm = new Map(
  compatibility.packages
    .filter((entry) => entry.ecosystem === "npm")
    .map((entry) => [entry.identity, entry.version]),
);

function text(path) {
  return readFileSync(resolve(repository, path), "utf8");
}

function fail(message) {
  throw new Error(`Compatibility projection failure: ${message}`);
}

function property(xml, name) {
  const match = new RegExp(`<${name}(?:\\s[^>]*)?>([^<]+)</${name}>`).exec(xml);
  if (!match) fail(`missing ${name}.`);
  return match[1];
}

function expect(actual, expected, label) {
  if (actual !== expected) fail(`${label} is '${actual}', expected '${expected}'.`);
}

function expectedNpm(identity) {
  const version = npm.get(identity);
  if (!version) fail(`authority does not select npm package '${identity}'.`);
  return version;
}

function expectedNuget(identity) {
  const version = nuget.get(identity);
  if (!version) fail(`authority does not select NuGet package '${identity}'.`);
  return version;
}

const central = text("Directory.Packages.props");
expect(property(central, "RunicAssetsPackageVersion"), expectedNuget("Runic.Assets"), "Runic Assets central pin");
expect(property(central, "RunicTranslationsPackageVersion"), expectedNuget("Runic.Translations"), "Runic Translations central pin");
for (const [identity, propertyName] of [
  ["Runic.CommandLine", "RunicCommandLinePackageVersion"],
  ["Runic.Desktop", "RunicDesktopPackageVersion"],
]) {
  expect(property(central, propertyName), expectedNuget(identity), `${identity} central pin`);
  const match = new RegExp(`<PackageVersion Include="${identity}" Version="\\$\\(${propertyName}\\)"`).exec(central);
  if (!match) fail(`missing ${identity} central package pin.`);
}

const templateProject = text("templates/RunicToolkit.Templates/RunicToolkit.Templates.csproj");
expect(property(templateProject, "PackageVersion"), expectedNuget("Runic.Application.Templates"), "template package version");
for (const [propertyName, identity] of [
  ["ApplicationBridgeTemplateVersion", "@runic-artifex/application-bridge"],
  ["RunicAngularTemplateVersion", "@runic-artifex/angular"],
  ["RunicSvelteTemplateVersion", "@runic-artifex/svelte"],
  ["RunicViteTemplateVersion", "@runic-artifex/vite-plugin-runic"],
  ["RunicDesktopNpmTemplateVersion", "@runic-artifex/desktop"],
]) {
  expect(property(templateProject, propertyName), expectedNpm(identity), `${propertyName} default`);
}
expect(property(templateProject, "RunicDesktopTemplateVersion"), expectedNuget("Runic.Desktop"), "RunicDesktopTemplateVersion default");

const sourceRevisions = new Map(compatibility.sources.map((source) => [source.repository, source.revision]));
const verificationScript = text("eng/verify.sh");
const packScript = text("eng/pack.sh");
for (const repositoryName of ["runic-command-line", "runic-assets", "runic-translations", "runic-desktop", "runic-svelte", "runic-vite"]) {
  const expectedRevision = sourceRevisions.get(repositoryName);
  if (!expectedRevision) fail(`authority does not pin source '${repositoryName}'.`);
  expect(compatibilitySetValue("source", repositoryName), expectedRevision, `${repositoryName} source resolver`);
  if (!verificationScript.includes(`compatibility-set-value.mjs source ${repositoryName}`)) {
    fail(`verification script does not derive ${repositoryName} from compatibility authority.`);
  }
  if (["runic-command-line", "runic-assets", "runic-translations", "runic-desktop"].includes(repositoryName) &&
      !packScript.includes(`compatibility-set-value.mjs\" source ${repositoryName}`)) {
    fail(`pack script does not derive ${repositoryName} from compatibility authority.`);
  }
}

const profilePackages = {
  angular: ["@runic-artifex/application-bridge", "@runic-artifex/desktop", "@runic-artifex/angular"],
  react: ["@runic-artifex/application-bridge", "@runic-artifex/desktop", "@runic-artifex/vite-plugin-runic"],
  svelte: ["@runic-artifex/application-bridge", "@runic-artifex/desktop", "@runic-artifex/svelte", "@runic-artifex/vite-plugin-runic"],
  vue: ["@runic-artifex/application-bridge", "@runic-artifex/desktop", "@runic-artifex/vite-plugin-runic"],
};

for (const [profile, selectedPackages] of Object.entries(profilePackages)) {
  const base = `templates/RunicToolkit.Templates/content/${profile}/Frontend`;
  if (existsSync(resolve(repository, `templates/RunicToolkit.Templates/content/${profile}/package.json`))) {
    fail(`${profile} template must not wrap Frontend in an ancestor npm workspace.`);
  }
  const manifest = JSON.parse(text(`${base}/package.json`));
  expect(manifest.packageManager, `npm@${compatibility.toolchain.npm}`, `${profile} package manager`);
  expect(manifest.engines?.node, compatibility.toolchain.node.replace(/\.0$/, ".x"), `${profile} Node engine`);
  expect(manifest.engines?.npm, compatibility.toolchain.npm.replace(/\.0$/, ".x"), `${profile} npm engine`);
  const npmrc = text(`${base}/.npmrc`);
  if (!npmrc.includes("engine-strict=true") || !npmrc.includes("omit-lockfile-registry-resolved=true")) {
    fail(`${profile} frontend must enforce engines and registry-portable locks.`);
  }

  const lock = JSON.parse(text(`${base}/package-lock.json`));
  if (lock.lockfileVersion !== 3) fail(`${profile} lock file must use lockfileVersion 3.`);
  for (const identity of selectedPackages) {
    const entry = lock.packages?.[`node_modules/${identity}`];
    if (!entry) fail(`${profile} lock file is missing ${identity}.`);
    expect(entry.version, expectedNpm(identity), `${profile} ${identity} lock pin`);
    if (typeof entry.integrity !== "string" || !entry.integrity.startsWith("sha512-")) {
      fail(`${profile} ${identity} lock entry must include sha512 integrity.`);
    }
    if (entry.resolved !== undefined) fail(`${profile} ${identity} lock entry must not pin a registry host.`);
  }
  for (const [path, entry] of Object.entries(lock.packages ?? {})) {
    if (!path.startsWith("node_modules/@runic-artifex/")) continue;
    const identity = path.slice("node_modules/".length);
    if (!selectedPackages.includes(identity)) {
      fail(`${profile} lock file selects unexpected Runic package '${identity}'.`);
    }
  }

  const project = text(`templates/RunicToolkit.Templates/content/${profile}/RunicDesktopApp.csproj`);
  if (!project.includes('<Import Project="RunicTemplateFrontend.targets" />')) {
    fail(`${profile} template does not import the incremental frontend build target.`);
  }
  const target = text(`templates/RunicToolkit.Templates/content/${profile}/RunicTemplateFrontend.targets`);
  for (const requirement of ["ci --ignore-scripts", "BeforeTargets=\"RunicAssetsPackFileSystem\"", "Inputs=", "Outputs=", "RunicToolkitFrontendBuild"]) {
    if (!target.includes(requirement)) fail(`${profile} frontend target is missing '${requirement}'.`);
  }

  const serialized = JSON.stringify(manifest);
  if (serialized.includes("CsWebUi") || serialized.includes("cs-webui")) {
    fail(`${profile} template must not select CS-WebUI.`);
  }
}

console.log(`Compatibility projections match ${compatibility.id}.`);
