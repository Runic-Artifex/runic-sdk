import vue from "@vitejs/plugin-vue";

import { buildTodoFrontend } from "../build-app.mjs";

await buildTodoFrontend({
  packageDirectory: import.meta.dirname,
  framework: "Vue",
  entry: "src/main.ts",
  plugins: [vue()],
});
