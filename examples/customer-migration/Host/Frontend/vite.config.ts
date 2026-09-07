import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";
import { runic } from "@runic-artifex/vite-plugin-runic";
export default defineConfig(({ mode }) => ({
  plugins: [runic({ desktop: loadEnv(mode, ".", "VITE_").VITE_RUNIC_HOST !== "cswebui" }), react()],
  base: "./",
  build: { target: "es2022" },
}));
