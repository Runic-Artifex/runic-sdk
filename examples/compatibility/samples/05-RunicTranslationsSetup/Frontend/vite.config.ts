import { runicTranslations } from "@runic-artifex/vite-plugin-runic-translations";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

const sourceCommandArguments = process.env.RUNIC_TRANSLATIONS_TOOL_VERSION
  ? undefined
  : [
      "run",
      "--project",
      "../../../runic-translations/dotnet/tools/dotnet-runic-translations/dotnet-runic-translations.csproj",
      "--",
    ];

export default defineConfig({
  plugins: [
    runicTranslations({
      cwd: "..",
      project: "translations",
      output: "obj/net10.0/translations",
      ...(sourceCommandArguments ? { commandArguments: sourceCommandArguments } : {}),
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
