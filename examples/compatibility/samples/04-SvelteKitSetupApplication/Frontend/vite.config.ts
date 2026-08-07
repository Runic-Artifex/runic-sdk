import { DevTools } from "@vitejs/devtools";
import { runicToolkit } from "@runic-artifex/vite-plugin-runic-toolkit";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [
    DevTools({ visibility: "passive" }),
    runicToolkit({
      contract: { identity: "runic.artifex.setup", version: "1" },
    }),
    sveltekit(),
  ],
  build: { target: "es2022" },
});
