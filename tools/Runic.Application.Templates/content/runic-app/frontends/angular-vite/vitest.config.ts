import { defineConfig, mergeConfig } from "vitest/config";
import vite from "./vite.config.ts";

// The tests use the Vite configuration and render the components in happy-dom; src/test-setup.ts starts Angular's
// test environment.
export default mergeConfig(vite, defineConfig({
  test: { environment: "happy-dom", include: ["src/**/*.spec.ts"], setupFiles: ["src/test-setup.ts"] },
}));
