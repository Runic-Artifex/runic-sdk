import { defineConfig, loadEnv } from "vite";
import vue from "@vitejs/plugin-vue";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig(({ mode }) => ({
  plugins: [runic({ desktop: (loadEnv(mode, ".", "VITE_").VITE_RUNIC_HOST ?? "__RUNIC_HOST__") !== "cswebui", applicationBridge: { authority: "csharp", project: "../RunicDesktopApp.csproj" } }), vue()],
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" },
}));
