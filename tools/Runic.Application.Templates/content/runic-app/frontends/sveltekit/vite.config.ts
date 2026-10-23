import { defineConfig } from "vite";
import { sveltekit } from "@sveltejs/kit/vite";
import { runicAdapter } from "@runic-artifex/sveltekit";
import { runicDevSession } from "@runic-artifex/vite-plugin-runic";

export default defineConfig(({ command }) => ({
  plugins: [
    // The Runic adapter prerenders the page into dist/, the folder the Views build copies to www/. SvelteKit
    // writes relative asset URLs, so the page is relocatable: Runic Desktop serves each window below its own
    // path. The page names no host, so one build runs on Runic Desktop and CS-WebUI.
    // A production build adds a page policy that allows SvelteKit's inline start script by its hash; Program.cs
    // sends the rest of the Content-Security-Policy outside Development. The Views Bridge needs no
    // 'unsafe-eval': its state arrives as data. The development server adds none.
    sveltekit(command === "build"
      ? { adapter: runicAdapter({ out: "dist" }), csp: { mode: "hash", directives: { "script-src": ["self"] } } }
      : { adapter: runicAdapter({ out: "dist" }) }),
    // Lets `dotnet runic dev` confirm that the development server on its port is the one it started.
    runicDevSession(),
  ],
}));
