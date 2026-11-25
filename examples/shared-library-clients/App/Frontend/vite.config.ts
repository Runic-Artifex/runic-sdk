import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import { runic } from "@runic-artifex/vite-plugin-runic";

const sharedUi = fileURLToPath(new URL("../../shared-ui", import.meta.url));

export default defineConfig({
  base: "./",
  plugins: [runic()],
  resolve: {
    // Inside this repository nothing installs the example's frontends, so the shared-ui package is resolved from its
    // folder (tsconfig.json maps the same specifiers). An installed workspace package needs no alias.
    alias: [
      { find: /^@acme\/shared-ui\/generated$/, replacement: `${sharedUi}/src/generated/runic-clients.ts` },
      { find: /^@acme\/shared-ui$/, replacement: `${sharedUi}/src/index.ts` },
    ],
    // The page and the shared package must load one @runic-artifex/views. A linked workspace package can resolve its
    // own copy, so dedupe it.
    dedupe: ["@runic-artifex/views"],
  },
  server: { fs: { allow: [fileURLToPath(new URL("..", import.meta.url)), sharedUi] } },
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" },
});
