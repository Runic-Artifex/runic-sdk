import react from "@vitejs/plugin-react";
import { resolve } from "node:path";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [react()],
  publicDir: resolve(import.meta.dirname, "../../SharedAssets"),
  server: {
    cors: true,
    fs: { allow: [resolve(import.meta.dirname, "..")] },
  },
});
