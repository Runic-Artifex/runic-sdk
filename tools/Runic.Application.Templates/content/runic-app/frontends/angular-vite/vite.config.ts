import { defineConfig } from "vite";
import angular from "@analogjs/vite-plugin-angular";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({
  // Relative asset URLs keep the published www folder relocatable, and Runic
  // Desktop serves each window below its own path. The page names no host, so
  // one build runs on Runic Desktop and CS-WebUI.
  base: "./",
  plugins: [
    // Compiles the Angular components ahead of time with tsconfig.app.json, and the tests with tsconfig.spec.json.
    // liveReload updates an edited component template in place. vite build checks no types: the build and typecheck
    // scripts run ngc for that.
    angular({ liveReload: true }),
    runic(),
  ],
  build: {
    // Every HTML page a Window opens is a build input. For a second Window, add
    // "settings.html" here and open it with RunicWindowOptions { StartPage = "settings.html" }.
    rolldownOptions: { input: ["index.html"] },
    outDir: "dist",
    emptyOutDir: true,
    target: "es2022"
  }
});
