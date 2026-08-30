import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({
  plugins: [runic({ desktop: true }), react()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    target: "es2022",
  },
});
