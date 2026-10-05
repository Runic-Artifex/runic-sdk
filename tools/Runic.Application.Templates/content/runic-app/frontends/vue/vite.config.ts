import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  // Relative asset URLs: Runic Desktop serves each window below its own path.
  base: "./",
  plugins: [vue()],
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" }
});
