import { defineConfig } from "vite";
import { runic } from "@runic-artifex/vite-plugin-runic";

// `vite build` builds the widget's page into dist/ and, with the component target, its component into
// dist/_runic_component/: an ES module that defines the widget's custom element, which a host page of the same vendor
// loads into its own document (trusted component mode). The guest serves it, with CORS for that page's origin only.
export default defineConfig({
  // Relocatable: the page, and the component's chunks and assets, resolve against their own address.
  base: "./",
  plugins: [runic({
    component: {
      entry: "src/component.ts",
      // The tag prefix. The build adds a hash of the build, so the element is service-widget-<hash>; the same build from
      // a second widget's origin, as on the dashboard, is defined as service-widget-<hash>-2.
      name: "service-widget",
    },
  })],
});
