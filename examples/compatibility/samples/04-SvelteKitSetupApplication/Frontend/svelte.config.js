import { runicToolkitAdapter } from "@runic-artifex/sveltekit";
import packageMetadata from "./package.json" with { type: "json" };

export default {
  kit: {
    adapter: runicToolkitAdapter({ mode: "spa", desktop: true, fallback: "index.html" }),
    router: { type: "hash" },
    version: { name: packageMetadata.version },
  },
};
