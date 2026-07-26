import { svelte } from "@sveltejs/vite-plugin-svelte";
import { buildTodoFrontend } from "../build-app.mjs";

await buildTodoFrontend({
  packageDirectory: import.meta.dirname,
  framework: "Svelte",
  entry: "src/main.ts",
  plugins: [svelte()],
});
