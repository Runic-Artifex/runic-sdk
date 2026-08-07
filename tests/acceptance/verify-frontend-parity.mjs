#!/usr/bin/env node

import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const matrix = json("eng/frontend-support-matrix.json");
if (matrix.schema !== "runic-toolkit.examples.frontend-support-matrix/1") {
  fail("Unsupported frontend support matrix schema.");
}

const expected = ["react", "vue", "svelte", "angular"];
if (JSON.stringify(Object.keys(matrix.frontends)) !== JSON.stringify(expected)) {
  fail("The support matrix must list React, Vue, Svelte, and Angular in policy order.");
}
if (!Array.isArray(matrix.requiredCapabilities) ||
    matrix.requiredCapabilities.length !== 12) {
  fail("The support matrix must retain all twelve first-class capability gates.");
}
for (const gate of Object.values(matrix.sharedGates)) requirePath(gate);

for (const entry of Object.values(matrix.frontends)) {
  if (!entry.starter || !entry.owner || !entry.authoring || !entry.hmr) {
    fail("A frontend is missing first-class policy metadata.");
  }
  for (const project of entry.todo) requirePath(project);
}

const packageVersion = "0.1.0-preview.4.1";
const packages = {
  react: "@runic-artifex/mvvm-react",
  vue: "@runic-artifex/mvvm-vue",
  svelte: "@runic-artifex/mvvm-svelte",
  angular: "@runic-artifex/mvvm-angular",
};
for (const [framework, adapter] of Object.entries(packages)) {
  const package_ = json(`samples/Todo.Frontends/${framework}/package.json`);
  if (package_.name !== `@runic-artifex/sample-todo-${framework}`) {
    fail(`${framework} has an inconsistent sample package identity.`);
  }
  if (package_.dependencies?.["@runic-artifex/mvvm"] !== packageVersion ||
      package_.dependencies?.[adapter] !== packageVersion) {
    fail(`${framework} must consume the exact published Toolkit package version.`);
  }
}

const todoProps = text("samples/Todo.Frontends/TodoFrontendSample.props");
contains(todoProps, "RunicToolkit.Frontend.Sdk", "published frontend SDK reference");
contains(todoProps, "RunicToolkitFrontendWorkspace", "Toolkit workspace property");
contains(todoProps, "simple/index.html;advanced/index.html", "both Todo development documents");
doesNotContain(todoProps, "../../src/", "source-tree project dependency");

const angularPackage = json("samples/Todo.Frontends/angular/package.json");
if (!angularPackage.scripts.dev.startsWith("ng serve ") ||
    angularPackage.scripts["dev:mock"] !== "ng serve todo-angular --configuration mock") {
  fail("Angular Todo must expose its Angular development and mock servers.");
}
for (const framework of ["react", "vue", "svelte"]) {
  const package_ = json(`samples/Todo.Frontends/${framework}/package.json`);
  if (package_.scripts.dev !== "vite" || package_.scripts["dev:mock"] !== "vite --mode mock") {
    fail(`${framework} Todo must expose its Vite development and mock servers.`);
  }
  requirePath(`samples/Todo.Frontends/${framework}/vite.config.mjs`);
}

const svelte = text("samples/Todo.Frontends/svelte/src/TodoApp.svelte");
for (const legacy of ["export let", "$:", "on:click", "on:submit"]) {
  if (svelte.includes(legacy)) fail(`Svelte Todo retains legacy syntax '${legacy}'.`);
}
for (const path of [
  "samples/Todo.Frontends/svelte/src/simple/SimpleTodo.svelte",
  "samples/Todo.Frontends/svelte/src/advanced/AdvancedTodo.svelte",
  "samples/Todo.Frontends/svelte/src/components/AppHeader.svelte",
  "samples/Todo.Frontends/react/src/simple/SimpleTodo.tsx",
  "samples/Todo.Frontends/react/src/advanced/AdvancedTodo.tsx",
  "samples/Todo.Frontends/angular/src/simple/simple-todo.component.ts",
  "samples/Todo.Frontends/angular/src/advanced/advanced-todo.component.ts",
  "samples/Todo.Frontends/vue/src/SimpleTodo.vue",
  "samples/Todo.Frontends/vue/src/AdvancedTodo.vue",
]) {
  requirePath(path);
}

contains(text("samples/Todo.Frontends/shared/todo.mock.ts"),
  "createTodoMockChannel", "shared Todo protocol fixture");
contains(text("samples/Todo.Frontends/angular/angular.json"),
  '"browser": "src/main.mock.ts"', "Angular Todo mock entrypoint");
contains(text(".npmrc"), "npm.pkg.github.com", "GitHub Packages npm registry");

for (const path of ["samples", "package.json"]) {
  doesNotContain(textTree(path), "@webuitoolkit/", "retired npm package scope");
  doesNotContain(textTree(path), "../../src/", "source-tree dependency");
  doesNotContain(textTree(path), "../../web/packages/", "source-tree npm dependency");
}

console.log("Package-only frontend parity passed for React, Vue, Svelte, and Angular.");

function json(path) {
  return JSON.parse(text(path));
}

function text(path) {
  const absolute = resolve(root, path);
  if (!existsSync(absolute)) fail(`Missing required frontend artifact '${path}'.`);
  return readFileSync(absolute, "utf8");
}

function textTree(path) {
  const absolute = resolve(root, path);
  if (!existsSync(absolute)) fail(`Missing package-boundary path '${path}'.`);
  if (!statSync(absolute).isDirectory()) return readFileSync(absolute, "utf8");
  const extensions = new Set([
    ".cs", ".csproj", ".props", ".targets", ".json", ".js", ".mjs",
    ".ts", ".tsx", ".svelte", ".vue",
  ]);
  const files = [];
  const pending = [absolute];
  while (pending.length !== 0) {
    const directory = pending.pop();
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (["bin", "obj", "dist", "node_modules", ".angular-output"].includes(entry.name)) continue;
      const candidate = resolve(directory, entry.name);
      if (entry.isDirectory()) pending.push(candidate);
      else if (extensions.has(entry.name.slice(entry.name.lastIndexOf(".")))) files.push(candidate);
    }
  }
  return files.sort().map((file) => readFileSync(file, "utf8")).join("\n");
}

function requirePath(path) {
  if (!existsSync(resolve(root, path))) fail(`Missing required frontend path '${path}'.`);
}

function contains(source, expected, label) {
  if (!source.includes(expected)) fail(`Missing ${label}: '${expected}'.`);
}

function doesNotContain(source, unexpected, label) {
  if (source.includes(unexpected)) fail(`Found ${label}: '${unexpected}'.`);
}

function fail(message) {
  throw new Error(message);
}
