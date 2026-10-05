import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";

export default defineConfig({
  // Relative asset URLs: Runic Desktop serves each window below its own path.
  base: "./",
  plugins: [svelte()],
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" }
});
