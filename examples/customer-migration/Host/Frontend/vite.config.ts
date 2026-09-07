import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { runic } from "@runic-artifex/vite-plugin-runic";
export default defineConfig({
  plugins: [runic({ desktop: process.env.VITE_RUNIC_HOST !== "cswebui" }), react()],
  base: "./",
  build: { target: "es2022" },
});
