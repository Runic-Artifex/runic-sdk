import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({
//#if (desktopHost)
  // runic({ desktop: true }) loads the Runic Desktop bootstrap and builds with
  // relative asset URLs, because Runic Desktop serves each window below its own path.
  plugins: [react(), runic({ desktop: true })],
//#else
  // Relative asset URLs keep the published www folder relocatable.
  base: "./",
  plugins: [react(), runic()],
//#endif
  build: { outDir: "dist", emptyOutDir: true, target: "es2022" }
});
