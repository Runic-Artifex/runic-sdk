import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  // Relative asset URLs: Runic Desktop serves each window below its own path.
  base: "./",
  plugins: [react()],
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" }
});
