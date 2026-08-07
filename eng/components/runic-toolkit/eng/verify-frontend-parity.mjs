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
if (
  matrix.renderers.svelte.integrationPackage !== "@runic-artifex/svelte" ||
  matrix.renderers.svelte.supportedMajor !== 5
) {
  fail("Svelte 5 must use the integration owned by runic-svelte.");
}
if (
  matrix.developmentIntegrations.vite.package !==
    "@runic-artifex/vite-plugin-runic-toolkit" ||
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
contains(runtime, "CsWebUiApplicationBridgeLive", "production Layer");
contains(text("web/packages/application-bridge/src/mock.ts"), "MockApplicationBridge", "mock Layer");
contains(text("web/packages/application-bridge/src/mock.ts"), "TestApplicationBridge", "fault-injection Layer");

const svelteTemplatePackage = json(
  "templates/RunicToolkit.Templates/content/svelte/Frontend/package.json",
);
if (svelteTemplatePackage.dependencies?.["@runic-artifex/svelte"] === undefined) {
  fail("The Svelte template must consume the official Svelte integration package.");
}
if (
  svelteTemplatePackage.devDependencies?.["@runic-artifex/vite-plugin-runic-toolkit"] === undefined ||
  svelteTemplatePackage.devDependencies?.["@vitejs/devtools"] === undefined
) {
  fail("The Svelte template must consume the Runic Vite plugin and official Vite DevTools.");
}
const viteConfig = text("templates/RunicToolkit.Templates/content/svelte/Frontend/vite.config.ts");
contains(viteConfig, "DevTools({ visibility: \"passive\" })", "official Vite DevTools plugin");
contains(viteConfig, "runicToolkit({", "Runic Toolkit Vite plugin");
if (existsSync(resolve(root, "tools/dotnet-runic-toolkit/ViteConfigurationBridge.cs"))) {
  fail("The CLI must not generate or own a synthetic Vite configuration.");
}
if (text("tools/dotnet-runic-toolkit/ViteDevelopmentServer.cs").includes('"--config"')) {
  fail("The CLI must launch the project's normal Vite configuration.");
}

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
