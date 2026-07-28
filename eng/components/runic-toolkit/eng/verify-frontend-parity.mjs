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
  if (package_.scripts["dev:mock"] !== "vite --mode mock") {
    fail(`${framework} Todo must expose its Vite mock mode.`);
  }
  requirePath(`samples/Todo.Frontends/${framework}/vite.config.mjs`);
}
if (angularPackage.scripts["dev:mock"] !==
    "ng serve todo-angular --configuration mock") {
  fail("Angular Todo must expose its application-builder mock configuration.");
}

const svelte = text("samples/Todo.Frontends/svelte/src/TodoApp.svelte");
for (const legacy of ["export let", "$:", "on:click", "on:submit"]) {
  if (svelte.includes(legacy)) fail(`Svelte Todo retains legacy syntax '${legacy}'.`);
}
for (const path of [
  "samples/Todo.Frontends/svelte/src/simple/SimpleTodo.svelte",
  "samples/Todo.Frontends/svelte/src/advanced/AdvancedTodo.svelte",
  "samples/Todo.Frontends/svelte/src/components/AppHeader.svelte",
]) {
  requirePath(path);
}
if (svelte.includes("createSimpleTodoStores") ||
    svelte.includes("createAdvancedTodoStores")) {
  fail("The Svelte application root still owns feature-store machinery.");
}

const reactMain = text("samples/Todo.Frontends/react/src/main.tsx");
for (const path of [
  "samples/Todo.Frontends/react/src/simple/SimpleTodo.tsx",
  "samples/Todo.Frontends/react/src/simple/useSimpleTodo.ts",
  "samples/Todo.Frontends/react/src/advanced/AdvancedTodo.tsx",
  "samples/Todo.Frontends/react/src/advanced/useAdvancedTodo.ts",
  "samples/Todo.Frontends/react/src/components/AppHeader.tsx",
]) {
  requirePath(path);
}
for (const presentation of ["function SimpleTodo", "function AdvancedTodo", "useState("]) {
  if (reactMain.includes(presentation)) {
    fail(`The React application root retains presentation concern '${presentation}'.`);
  }
}

const angularMain = text("samples/Todo.Frontends/angular/src/main.ts");
for (const path of [
  "samples/Todo.Frontends/angular/src/simple/simple-todo.component.ts",
  "samples/Todo.Frontends/angular/src/simple/simple-todo.component.html",
  "samples/Todo.Frontends/angular/src/advanced/advanced-todo.component.ts",
  "samples/Todo.Frontends/angular/src/advanced/advanced-todo.component.html",
  "samples/Todo.Frontends/angular/src/components/app-header.component.ts",
]) {
  requirePath(path);
}
if (angularMain.includes("@Component") || angularMain.includes("template:")) {
  fail("The Angular application root still owns component presentation.");
}
contains(
  text("samples/Todo.Frontends/angular/src/advanced/advanced-todo.component.ts"),
  "signal(",
  "Angular-local signal state",
);

for (const path of [
  "samples/Todo.Frontends/vue/src/SimpleTodo.vue",
  "samples/Todo.Frontends/vue/src/AdvancedTodo.vue",
]) {
  requirePath(path);
}

const inspector = text("web/packages/mvvm/src/inspector.ts");
contains(inspector, "MvvmDevelopmentInspector", "private-binding inspector");
contains(inspector, "mountMvvmInspectorOverlay", "native inspector overlay");
const mock = text("web/packages/mvvm/src/mock.ts");
contains(mock, "MvvmMockFrameChannel", "frontend-only protocol mock");
contains(mock, 'mode = "mock"', "visible mock identity");
const todoMock = text("samples/Todo.Frontends/shared/todo.mock.ts");
contains(todoMock, "createTodoMockChannel", "shared Todo protocol fixture");
contains(todoMock, "webuitoolkit.todo.mock/1", "visible Todo mock identity");
contains(
  text("samples/Todo.Frontends/angular/angular.json"),
  '"browser": "src/main.mock.ts"',
  "Angular Todo mock entrypoint",
);
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
