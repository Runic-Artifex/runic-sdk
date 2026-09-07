import { defineConfig, loadEnv } from "vite";
import { DevTools } from "@vitejs/devtools";
import { runic } from "@runic-artifex/vite-plugin-runic";
import { svelte } from "@sveltejs/vite-plugin-svelte";

export default defineConfig(({ mode }) => ({
  plugins: [
    // The pinned official dock uses a Node-only transport; core Runic/HMR works with Bun.
    ...("Bun" in globalThis ? [] : [DevTools({ visibility: "passive" })]),
    runic({
      contract: { identity: "runic.artifex.counter", version: "1" },
      desktop: (loadEnv(mode, ".", "VITE_").VITE_RUNIC_HOST ?? "__RUNIC_HOST__") !== "cswebui",
      applicationBridge: { authority: "csharp", project: "../RunicDesktopApp.csproj" },
    }),
    svelte(),
  ],
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" },
}));
