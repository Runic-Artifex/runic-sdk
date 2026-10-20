import { runicAdapter } from "@runic-artifex/sveltekit";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

// A SPA outside Runic Desktop, such as a CS-WebUI frontend: the default path router accepts page options.
export default defineConfig({
  plugins: [sveltekit({ adapter: runicAdapter({ mode: "spa", strict: true }) })],
});
