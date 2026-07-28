import vue from "@vitejs/plugin-vue";
import { resolve } from "node:path";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [vue()],
  publicDir: resolve(import.meta.dirname, "../../SharedAssets"),
  server: {
    cors: true,
    fs: { allow: [resolve(import.meta.dirname, "..")] },
  },
});
