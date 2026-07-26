import { buildTodoFrontend } from "../build-app.mjs";

await buildTodoFrontend({
  packageDirectory: import.meta.dirname,
  framework: "Vue",
  entry: "src/main.ts",
  alias: {
    vue: "vue/dist/vue.esm-bundler.js",
  },
});
