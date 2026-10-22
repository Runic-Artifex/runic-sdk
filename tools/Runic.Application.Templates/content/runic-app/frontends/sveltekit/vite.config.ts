import { defineConfig } from "vite";
import { sveltekit } from "@sveltejs/kit/vite";
import { runicAdapter } from "@runic-artifex/sveltekit";
import { runicDevSession } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({
  plugins: [
    // The Runic adapter prerenders the page into dist/, the folder the Views build copies to www/. SvelteKit
    // writes relative asset URLs, so the page is relocatable: Runic Desktop serves each window below its own
    // path. The page names no host, so one build runs on Runic Desktop and CS-WebUI.
    sveltekit({ adapter: runicAdapter({ out: "dist" }) }),
    // Lets `dotnet runic dev` confirm that the development server on its port is the one it started.
    runicDevSession(),
  ],
});
