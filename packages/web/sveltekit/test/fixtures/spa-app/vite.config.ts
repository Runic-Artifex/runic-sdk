import { runicToolkitAdapter } from "@runic-artifex/sveltekit";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [
    sveltekit({
      adapter: runicToolkitAdapter({ mode: "spa", desktop: true, strict: true }),
      router: { type: "hash" },
    }),
  ],
});
