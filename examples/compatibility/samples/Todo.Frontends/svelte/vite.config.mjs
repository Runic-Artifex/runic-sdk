import { svelte } from "@sveltejs/vite-plugin-svelte";
import { resolve } from "node:path";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [svelte()],
  publicDir: resolve(import.meta.dirname, "../../SharedAssets"),
  server: {
    cors: true,
    fs: { allow: [resolve(import.meta.dirname, "..")] },
  },
});
