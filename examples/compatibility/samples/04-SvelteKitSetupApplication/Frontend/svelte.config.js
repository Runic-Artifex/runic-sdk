import { runicToolkitAdapter } from "@runic-artifex/sveltekit";
import packageMetadata from "./package.json" with { type: "json" };

export default {
  kit: {
    adapter: runicToolkitAdapter({ mode: "spa", fallback: "index.html" }),
    version: { name: packageMetadata.version },
  },
};
