// A page's Vite build with the runic-views catalog of @runic-artifex/views-translations regenerated from its packed
// sources with the application's overrides (dependencies: "auto"). A library build, so that node can run the bundle.
import { runicTranslations } from "@runic-artifex/vite-plugin-runic-translations";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [runicTranslations({ dependencies: "auto" })],
  logLevel: "warn",
  build: { lib: { entry: "main.js", formats: ["es"], fileName: "page" }, outDir: "dist", emptyOutDir: true, minify: false },
});
