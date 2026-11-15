import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import { DevTools } from "@vitejs/devtools";
import { runic } from "@runic-artifex/vite-plugin-runic";
import { runicTranslations } from "@runic-artifex/vite-plugin-runic-translations";

export default defineConfig({
  // Relative asset URLs keep the published www folder relocatable, and Runic
  // Desktop serves each window below its own path. The page names no host, so
  // one build runs on Runic Desktop and CS-WebUI.
  base: "./",
  plugins: [
    vue(),
    runic(),
    // The Vite DevTools dock while Vite serves: the Runic panel shows each Bridge call with its C# handler and
    // opens it in your editor. vite build leaves it out. To turn the dock off, delete this line.
    DevTools(),
    // Compiles the catalog, translations/runic.json, with the dotnet-runic-translations tool of the project's
    // .config/dotnet-tools.json into .runic/translations, and serves it as virtual:runic-translations/app with its types
    // in .runic/translations/virtual.d.ts. The development server compiles a changed, added or removed message again
    // and the page shows it, without a dotnet build.
    runicTranslations(),
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
