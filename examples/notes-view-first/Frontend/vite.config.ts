import { defineConfig } from "vite";

const webuiOrigin = process.env.RUNIC_WEBUI_ORIGIN;
const devOrigin = process.env.RUNIC_DEV_ORIGIN;

export default defineConfig({
  server: {
    host: "127.0.0.1",
    proxy: {
      ...(webuiOrigin ? { "/webui.js": webuiOrigin } : {}),
      ...(devOrigin ? { "/__runic_dev": devOrigin } : {}),
    },
  },
  plugins: devOrigin ? [{
    name: "runic-dev-reload",
    apply: "serve",
    transformIndexHtml: () => [{ tag: "script", attrs: { type: "module", src: "/src/dev-reload.ts" } }],
  }] : [],
});
