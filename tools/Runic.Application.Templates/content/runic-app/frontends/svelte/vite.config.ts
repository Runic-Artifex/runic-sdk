import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import { DevTools } from "@vitejs/devtools";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({
  // Relative asset URLs keep the published www folder relocatable, and Runic
  // Desktop serves each window below its own path. The page names no host, so
  // one build runs on Runic Desktop and CS-WebUI.
  base: "./",
  plugins: [
    svelte(),
    runic(),
    // The Vite DevTools dock while Vite serves: the Runic panel shows each Bridge call with its C# handler and
    // opens it in your editor. vite build leaves it out. To turn the dock off, delete this line.
    DevTools(),
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
