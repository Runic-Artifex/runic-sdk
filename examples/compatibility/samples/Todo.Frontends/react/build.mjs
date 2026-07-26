import { buildTodoFrontend } from "../build-app.mjs";

await buildTodoFrontend({
  packageDirectory: import.meta.dirname,
  framework: "React",
  entry: "src/main.tsx",
});
