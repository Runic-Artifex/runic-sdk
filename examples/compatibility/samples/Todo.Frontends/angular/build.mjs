import { buildTodoFrontend } from "../build-app.mjs";

await buildTodoFrontend({
  packageDirectory: import.meta.dirname,
  framework: "Angular",
  entry: "src/main.ts",
});
