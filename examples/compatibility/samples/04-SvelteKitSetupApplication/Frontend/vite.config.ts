import { DevTools } from "@vitejs/devtools";
import { runic } from "@runic-artifex/vite-plugin-runic";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [
    DevTools({ visibility: "passive" }),
    runic({
      contract: { identity: "runic.artifex.setup", version: "1" },
    }),
    sveltekit(),
  ],
  build: { target: "es2022" },
});
