import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({
//#if (desktopHost)
  // runic({ desktop: true }) loads the Runic Desktop bootstrap and builds with
  // relative asset URLs, because Runic Desktop serves each window below its own path.
  plugins: [svelte(), runic({ desktop: true })],
//#else
  // Relative asset URLs keep the published www folder relocatable.
  base: "./",
  plugins: [svelte(), runic()],
//#endif
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" }
});
