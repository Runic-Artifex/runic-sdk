import { runicTranslations } from "@runic-artifex/vite-plugin-runic-translations";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [
    runicTranslations({
      manifest: "../obj/net10.0/translations/setup.esm/web-module-manifest-v1.json",
      sourceFiles: [
        "../Resources/setup.catalog.json",
        "../Resources/setup.en.json",
        "../Resources/setup.de.json",
      ],
    }),
    sveltekit(),
  ],
  build: { target: "es2022" },
  server: {
    proxy: {
      "/api": "http://127.0.0.1:5080",
    },
  },
});
