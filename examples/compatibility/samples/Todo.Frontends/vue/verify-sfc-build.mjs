import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

const packageDirectory = import.meta.dirname;
const buildScript = await readFile(resolve(packageDirectory, "build.mjs"), "utf8");
const entry = await readFile(resolve(packageDirectory, "src/main.ts"), "utf8");
const simple = await readFile(resolve(packageDirectory, "src/SimpleTodo.vue"), "utf8");
const advanced = await readFile(resolve(packageDirectory, "src/AdvancedTodo.vue"), "utf8");
if (!buildScript.includes("@vitejs/plugin-vue") ||
    !entry.includes("./SimpleTodo.vue") ||
    !entry.includes("./AdvancedTodo.vue") ||
    !simple.includes('<script setup lang="ts">') ||
    !simple.includes("<template>") ||
    !advanced.includes('<script setup lang="ts">') ||
    !advanced.includes("<template>")) {
  throw new Error("The Vue Todo sample is not authored and compiled as Vue SFCs.");
}

const manifest = JSON.parse(await readFile(
  resolve(packageDirectory, "dist/runic-toolkit.assets.json"),
  "utf8",
));
if (manifest.mode !== "production") {
  throw new Error("The Vue SFC verification requires an optimized production bundle.");
}
const application = await readFile(
  resolve(packageDirectory, "dist", manifest.entrypoints.app),
  "utf8",
);
if (application.includes("compileToFunction") ||
    application.includes("Runtime compilation is not supported")) {
  throw new Error("The Vue SFC bundle unexpectedly contains the runtime template compiler.");
}

console.log("Vue SFC production output verified.");
