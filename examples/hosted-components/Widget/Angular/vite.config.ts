import { defineConfig } from "vite";
import angular from "@analogjs/vite-plugin-angular";
// The component target is experimental (RUNICDESK103): the `runic` of the experimental entry takes it.
import { runic } from "@runic-artifex/vite-plugin-runic/experimental";

// The widget's Angular frontend (WidgetTool.csproj builds it with -p:WidgetFrontend=Angular). `vite build` compiles the
// components ahead of time with the Analog plugin and builds the widget's page into dist/ and, with the component
// target, its component into dist/_runic_component/, as the default frontend does (../Frontend/vite.config.ts).
export default defineConfig({
  // Relocatable: the page, and the component's chunks and assets, resolve against their own address.
  base: "./",
  plugins: [
    angular(),
    runic({ component: { entry: "src/component.ts", name: "service-widget" } }),
  ],
  resolve: {
    // The example takes the Runic packages from the repository's workspace, whose packages have Angular of their own:
    // one copy of each in the bundle.
    dedupe: ["@angular/core", "@angular/common", "@angular/platform-browser", "@runic-artifex/views"],
  },
  build: { target: "es2022" },
});
