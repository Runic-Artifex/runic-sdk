import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";

const webuiOrigin = process.env.RUNIC_WEBUI_ORIGIN;
const devOrigin = process.env.RUNIC_DEV_ORIGIN;

export default defineConfig({
  plugins: [svelte(), ...(devOrigin ? [{
    name: "runic-dev-reload",
    apply: "serve" as const,
    transformIndexHtml: () => [{ tag: "script", attrs: { type: "module", src: "/src/dev-reload.ts" } }],
  }] : [])],
  publicDir: fileURLToPath(new URL("../../../packages/dotnet/Runic.Application.Views.CsWebUi/www", import.meta.url)),
  build: { outDir: "dist" },
  server: {
    host: "127.0.0.1",
    fs: { allow: [fileURLToPath(new URL("../../..", import.meta.url))] },
    proxy: {
      ...(webuiOrigin ? { "/webui.js": webuiOrigin } : {}),
      ...(devOrigin ? { "/__runic_dev": devOrigin } : {}),
    },
  },
});
