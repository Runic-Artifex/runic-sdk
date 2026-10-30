import { defineConfig, mergeConfig } from "vitest/config";
import vite from "./vite.config.ts";

// The tests use the Vite configuration and render components in happy-dom. Svelte's browser build provides mount().
export default defineConfig(env => mergeConfig(vite(env), {
  resolve: { conditions: ["browser"] },
  test: { environment: "happy-dom" },
}));
