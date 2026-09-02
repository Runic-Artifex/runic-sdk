import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({
  plugins: [runic({ desktop: true, applicationBridge: true }), vue()],
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" },
});
