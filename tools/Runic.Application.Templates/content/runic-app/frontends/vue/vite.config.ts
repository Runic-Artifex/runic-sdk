import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({
  // Relative asset URLs keep the published www folder relocatable, and Runic
  // Desktop serves each window below its own path. The page names no host, so
  // one build runs on Runic Desktop and CS-WebUI.
  base: "./",
  plugins: [vue(), runic()],
  build: {
    // Every HTML page a Window opens is a build input. For a second Window, add
    // "settings.html" here and open it with RunicWindowOptions { StartPage = "settings.html" }.
    rolldownOptions: { input: ["index.html"] },
    outDir: "dist",
    emptyOutDir: true,
    target: "es2022"
  }
});
