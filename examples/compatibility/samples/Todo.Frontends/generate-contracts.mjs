import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const root = import.meta.dirname;
const repositoryRoot = resolve(root, "../..");
const nugetPackages = process.env.NUGET_PACKAGES ??
  resolve(repositoryRoot, ".packages/nuget");
const contractModule = await import(pathToFileURL(resolve(
  nugetPackages,
  "runictoolkit.frontend.sdk/0.1.0-preview.4.1/tools/generate-contracts.mjs",
)));
const { generateFrontendContracts } = contractModule;
await generateFrontendContracts({
  sourcePath: resolve(root, "todo.frontend.json"),
  csharpPath: resolve(root, "../Todo.FrontendHost/TodoContracts.g.cs"),
  typescriptPath: resolve(root, "shared/todo-contract.g.ts"),
  reactPath: resolve(root, "react/src/todo-bindings.g.ts"),
  vuePath: resolve(root, "vue/src/todo-bindings.g.ts"),
  sveltePath: resolve(root, "svelte/src/todo-bindings.g.ts"),
  angularPath: resolve(root, "angular/src/todo-bindings.g.ts"),
  verify: process.argv.includes("--verify"),
});
