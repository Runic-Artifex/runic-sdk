import { resolve } from "node:path";
import { generateFrontendContracts } from "../../src/WebUIToolkit.Frontend.Sdk/tools/generate-contracts.mjs";

const root = import.meta.dirname;
await generateFrontendContracts({
  sourcePath: resolve(root, "todo.frontend.json"),
  csharpPath: resolve(root, "../Todo.FrontendHost/TodoContracts.g.cs"),
  typescriptPath: resolve(root, "shared/todo-contract.g.ts"),
  verify: process.argv.includes("--verify"),
});
