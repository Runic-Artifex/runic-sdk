import { defineConfig } from "vite";
import { DevTools } from "@vitejs/devtools";
import { runicToolkit } from "@runic-artifex/vite-plugin-runic-toolkit";
import { svelte } from "@sveltejs/vite-plugin-svelte";

export default defineConfig({
  plugins: [
    DevTools({ visibility: "passive" }),
    runicToolkit({
      contract: { identity: "runic.artifex.counter", version: "1" },
    }),
    svelte(),
  ],
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" },
});
