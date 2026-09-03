import { DevTools } from "@vitejs/devtools";
import { runic } from "@runic-artifex/vite-plugin-runic";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [
    DevTools({ visibility: "passive" }),
    runic({
      contract: { identity: "runic.artifex.setup", version: "1" },
      applicationBridge: {
        source: "../../03-SetupApplication/Frontend/src/application.bridge.ts",
        ir: "../Contract/bridge.ir.json",
        facade: "src/lib/application.bridge.generated.ts",
      },
    }),
    sveltekit(),
  ],
  build: { target: "es2022" },
});
