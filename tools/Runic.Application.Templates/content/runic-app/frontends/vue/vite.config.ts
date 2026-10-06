import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({
//#if (host == "desktop")
  // runic({ desktop: true }) loads the Runic Desktop bootstrap and builds with
  // relative asset URLs, because Runic Desktop serves each window below its own path.
  plugins: [vue(), runic({ desktop: true })],
//#else
  // Relative asset URLs keep the published www folder relocatable.
  base: "./",
  plugins: [vue(), runic()],
//#endif
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" }
});
