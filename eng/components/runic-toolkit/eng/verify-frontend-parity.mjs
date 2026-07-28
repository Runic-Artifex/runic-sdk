#!/usr/bin/env node

import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const matrix = json("eng/frontend-support-matrix.json");
if (matrix.schema !== "webuitoolkit.frontend-support-matrix/1") {
  fail("Unsupported frontend support matrix schema.");
}

const expected = ["cwhtml", "react", "vue", "svelte", "angular"];
if (JSON.stringify(Object.keys(matrix.frontends)) !== JSON.stringify(expected)) {
  fail("The support matrix must list cwhtml, React, Vue, Svelte, and Angular in policy order.");
}
if (!Array.isArray(matrix.requiredCapabilities) ||
    matrix.requiredCapabilities.length !== 12) {
  fail("The support matrix must retain all twelve first-class capability gates.");
}
for (const gate of Object.values(matrix.sharedGates)) requirePath(gate);

const sdkProps = text(
  "src/WebUIToolkit.Frontend.Sdk/buildTransitive/WebUIToolkit.Frontend.Sdk.props",
);
contains(sdkProps, "WebUIToolkitFrontendDevServerKind", "generic development-server property");
contains(sdkProps, "WebUIToolkitFrontendDevServerDocument", "native bootstrap document property");

const tool = text("tools/dotnet-webuitoolkit/DevApplication.cs");
contains(tool, "IFrontendDevelopmentServer", "shared development-server contract");
contains(tool, "AngularDevelopmentServer", "Angular development-server coordinator");
contains(tool, "ViteDevelopmentServer", "Vite development-server coordinator");

const owners = {
  react: ["web/packages/mvvm-react/src/application.ts", "startReactMvvmApplication"],
  vue: ["web/packages/mvvm-vue/src/index.ts", "startVueMvvmApplication"],
  svelte: ["web/packages/mvvm-svelte/src/application.ts", "startSvelteMvvmApplication"],
  angular: ["web/packages/mvvm-angular/src/application.ts", "startAngularMvvmApplication"],
};
for (const [framework, [path, symbol]] of Object.entries(owners)) {
  contains(text(path), symbol, `${framework} native application owner`);
}

for (const [framework, entry] of Object.entries(matrix.frontends)) {
  if (!entry.starter || !entry.owner || !entry.authoring || !entry.hmr) {
    fail(`${framework} is missing first-class policy metadata.`);
  }
  for (const project of entry.todo) requirePath(project);
}

const todoProps = text("samples/Todo.Frontends/TodoFrontendSample.props");
contains(todoProps, "simple/index.html;advanced/index.html", "both Todo development documents");
contains(todoProps, "'$(TodoFrontendDirectory)' == 'angular'", "Angular dev-server selection");
const angularPackage = json("samples/Todo.Frontends/angular/package.json");
if (!angularPackage.scripts.dev.startsWith("ng serve ")) {
  fail("Angular Todo must use ng serve for development.");
}
for (const framework of ["react", "vue", "svelte"]) {
  const package_ = json(`samples/Todo.Frontends/${framework}/package.json`);
  if (package_.scripts.dev !== "vite") {
    fail(`${framework} Todo must use its Vite development server.`);
  }
  requirePath(`samples/Todo.Frontends/${framework}/vite.config.mjs`);
}

const svelte = text("samples/Todo.Frontends/svelte/src/TodoApp.svelte");
for (const legacy of ["export let", "$:", "on:click", "on:submit"]) {
  if (svelte.includes(legacy)) fail(`Svelte Todo retains legacy syntax '${legacy}'.`);
}

const inspector = text("web/packages/mvvm/src/inspector.ts");
contains(inspector, "MvvmDevelopmentInspector", "private-binding inspector");
contains(inspector, "mountMvvmInspectorOverlay", "native inspector overlay");
const mock = text("web/packages/mvvm/src/mock.ts");
contains(mock, "MvvmMockFrameChannel", "frontend-only protocol mock");
contains(mock, 'mode = "mock"', "visible mock identity");
contains(
  text("web/packages/mvvm/src/native.ts"),
  "channelFactory",
  "production-owner mock channel seam",
);
for (const framework of ["react", "vue", "svelte", "angular"]) {
  const starter = `templates/WebUIToolkit.Templates/content/${framework}/Frontend`;
  const package_ = json(`${starter}/package.json`);
  if (package_.scripts["dev:mock"] === undefined) {
    fail(`${framework} starter does not expose its conventional dev:mock command.`);
  }
  requirePath(`${starter}/src/counter.mock.ts`);
}

console.log(
  "Frontend parity policy metadata passed for cwhtml, React, Vue, Svelte, and Angular.",
);

function json(path) {
  return JSON.parse(text(path));
}

function text(path) {
  const absolute = resolve(root, path);
  if (!existsSync(absolute)) fail(`Missing required parity artifact '${path}'.`);
  return readFileSync(absolute, "utf8");
}

function requirePath(path) {
  if (!existsSync(resolve(root, path))) fail(`Missing required parity path '${path}'.`);
}

function contains(source, expected, label) {
  if (!source.includes(expected)) fail(`Missing ${label}: '${expected}'.`);
}

function fail(message) {
  throw new Error(message);
}
