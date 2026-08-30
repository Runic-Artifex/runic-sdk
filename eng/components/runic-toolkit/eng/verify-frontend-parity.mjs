#!/usr/bin/env node

import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const matrix = json("eng/frontend-support-matrix.json");
if (matrix.schema !== "runic-toolkit.frontend-support-matrix/4") {
  fail("Unsupported frontend support matrix schema.");
}
if (matrix.protocolOwner !== "@runic-artifex/application-bridge") {
  fail("Effect Application Bridge must be the only frontend protocol owner.");
}
for (const renderer of ["react", "vue", "svelte", "angular"]) {
  if (matrix.renderers[renderer]?.ownsProtocolState !== false) {
    fail(`${renderer} must not own protocol, reconnect, revision, or cancellation state.`);
  }
}

for (const renderer of ["react", "vue", "svelte", "angular"]) {
  const templateRoot = `templates/RunicToolkit.Templates/content/${renderer}`;
  const project = text(`${templateRoot}/RunicDesktopApp.csproj`);
  const program = text(`${templateRoot}/Program.cs`);
  const frontendPackage = json(`${templateRoot}/Frontend/package.json`);
  const bridge = text(`${templateRoot}/Frontend/src/counter-bridge.ts`);
  contains(project, 'PackageReference Include="Runic.Application.Desktop"', `${renderer} Desktop application package`);
  contains(project, 'PackageReference Include="Runic.Desktop"', `${renderer} Desktop runtime package`);
  contains(project, 'PackageReference Include="Runic.Assets.Desktop"', `${renderer} Desktop asset adapter`);
  contains(program, ".UseDesktop(", `${renderer} Desktop host composition`);
  contains(program, ".ToDesktopContentHandler()", `${renderer} request-scoped asset composition`);
  contains(bridge, "ApplicationBridgeLive", `${renderer} transport-neutral bridge Layer`);
  contains(bridge, "createDesktopFrameChannel", `${renderer} Desktop frame transport`);
  if (frontendPackage.dependencies?.["@runic-artifex/desktop"] === undefined) {
    fail(`The ${renderer} template must consume the Runic Desktop frontend transport.`);
  }
}
if (
  matrix.renderers.svelte.integrationPackage !== "@runic-artifex/svelte" ||
  matrix.renderers.svelte.supportedMajor !== 5
) {
  fail("Svelte 5 must use the integration owned by runic-svelte.");
}
if (
  matrix.renderers.angular.integrationPackage !== "@runic-artifex/angular" ||
  matrix.renderers.angular.supportedMajor !== 22
) {
  fail("Angular 22 must use the official Runic Angular integration package.");
}
if (
  matrix.developmentIntegrations.vite.package !==
    "@runic-artifex/vite-plugin-runic" ||
  matrix.developmentIntegrations.vite.supportedMajor !== 8 ||
  matrix.developmentIntegrations.vite.officialDevToolsPackage !== "@vitejs/devtools"
) {
  fail("Vite 8 must use the integration owned by runic-vite and official Vite DevTools.");
}
for (const gate of Object.values(matrix.sharedGates)) requirePath(gate);

const package_ = json("web/packages/application-bridge/package.json");
if (package_.name !== matrix.protocolOwner || package_.license !== "MIT") {
  fail("The Application Bridge npm package has inconsistent identity metadata.");
}
if (package_.dependencies?.effect === undefined) {
  fail("The Application Bridge runtime must have an explicit Effect dependency.");
}
const runtime = text("web/packages/application-bridge/src/runtime.ts");
contains(runtime, "ManagedRuntime", "single owned Effect runtime");
contains(runtime, "Stream.fromPubSub", "Effect host event stream");
contains(runtime, "ApplicationBridgeLive", "transport-neutral production Layer");
contains(text("web/packages/application-bridge/src/mock.ts"), "MockApplicationBridge", "mock Layer");
contains(text("web/packages/application-bridge/src/mock.ts"), "TestApplicationBridge", "fault-injection Layer");

const svelteTemplatePackage = json(
  "templates/RunicToolkit.Templates/content/svelte/Frontend/package.json",
);
if (svelteTemplatePackage.dependencies?.["@runic-artifex/svelte"] === undefined) {
  fail("The Svelte template must consume the official Svelte integration package.");
}
if (
  svelteTemplatePackage.devDependencies?.["@runic-artifex/vite-plugin-runic"] === undefined ||
  svelteTemplatePackage.devDependencies?.["@vitejs/devtools"] === undefined
) {
  fail("The Svelte template must consume the Runic Vite plugin and official Vite DevTools.");
}
const viteConfig = text("templates/RunicToolkit.Templates/content/svelte/Frontend/vite.config.ts");
contains(viteConfig, "DevTools({ visibility: \"passive\" })", "official Vite DevTools plugin");
contains(viteConfig, "runic({", "Runic Vite plugin");
if (existsSync(resolve(root, "tools/dotnet-runic-toolkit/ViteConfigurationBridge.cs"))) {
  fail("The CLI must not generate or own a synthetic Vite configuration.");
}
if (text("tools/dotnet-runic-toolkit/ViteDevelopmentServer.cs").includes('"--config"')) {
  fail("The CLI must launch the project's normal Vite configuration.");
}

const compatibilityGuide = text("docs/guides/frontend-frameworks.md");
contains(compatibilityGuide, "`runic.artifex.setup` / `1` / `f92970461e801b80f1e8b8fbf7bab346dece692b61c3c4c167b093ce6bc29336`", "generated host contract evidence");
contains(compatibilityGuide, "`@runic-artifex/svelte`", "Svelte ownership evidence");
contains(compatibilityGuide, "`@runic-artifex/angular`", "Angular ownership evidence");
contains(compatibilityGuide, "remain W30 gates", "W30 deferred boundary");
contains(compatibilityGuide, "a W70 gate", "W70 deferred boundary");

console.log("Application Bridge and official frontend integration policy passed.");

function json(path) { return JSON.parse(text(path)); }
function text(path) {
  const absolute = resolve(root, path);
  if (!existsSync(absolute)) fail(`Missing required frontend artifact '${path}'.`);
  return readFileSync(absolute, "utf8");
}
function requirePath(path) {
  if (!existsSync(resolve(root, path))) fail(`Missing required frontend path '${path}'.`);
}
function contains(source, expected, label) {
  if (!source.includes(expected)) fail(`Missing ${label}: '${expected}'.`);
}
function fail(message) { throw new Error(message); }
