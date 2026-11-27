import { defineConfig } from "vite";

export default defineConfig({
  // Relative asset URLs keep the published www folder relocatable.
  base: "./",
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" },
});
