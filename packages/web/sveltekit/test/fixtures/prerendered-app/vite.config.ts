import { runicAdapter } from "@runic-artifex/sveltekit";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

// The adapter is strict, so the build fails unless +layout.ts makes every route prerenderable.
export default defineConfig({
  plugins: [sveltekit({ adapter: runicAdapter({ mode: "prerendered", desktop: true, strict: true }) })],
});
