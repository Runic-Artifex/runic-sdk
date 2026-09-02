import { runicTranslations } from "@runic-artifex/vite-plugin-runic-translations";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [
    runicTranslations({
      project: "../translations",
      output: "../obj/net10.0/translations",
      commandArguments: [
        "run",
        "--project",
        "../../../../runic-translations/dotnet/tools/dotnet-runic-translations/dotnet-runic-translations.csproj",
        "--",
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
