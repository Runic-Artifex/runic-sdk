import { runicToolkitAdapter } from "@runic-artifex/sveltekit";

export default {
  kit: {
    adapter: runicToolkitAdapter({ mode: "spa", desktop: true, strict: true }),
    router: { type: "hash" },
  },
};
