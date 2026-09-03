import { defineConfig } from "vite";
import { DevTools } from "@vitejs/devtools";
import { runic } from "@runic-artifex/vite-plugin-runic";
import { svelte } from "@sveltejs/vite-plugin-svelte";

export default defineConfig({
  plugins: [
    DevTools({ visibility: "passive" }),
    runic({
      contract: { identity: "runic.artifex.counter", version: "1" },
      desktop: true,
      applicationBridge: true,
    }),
    svelte(),
  ],
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" },
});
