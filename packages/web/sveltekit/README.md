# `@runic-artifex/sveltekit`

SvelteKit adapter and locale routing helpers for Runic Desktop and Runic
Translations. Application Views use the `@runic-artifex/svelte/views` export.

```ts
// vite.config.ts
import { runicToolkitAdapter } from "@runic-artifex/sveltekit";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [sveltekit({ adapter: runicToolkitAdapter({ desktop: true }) })],
});
```

The package requires SvelteKit 3 and `@sveltejs/adapter-static` 4, which take
their configuration through the `sveltekit()` Vite plugin. The adapter emits a
relocatable page and `runic-toolkit.sveltekit.json` manifest. Use `mode: "spa"`
with `router: { type: "hash" }` for a Desktop SPA. The
`@runic-artifex/sveltekit/page-options` entry point provides matching route
options. `@runic-artifex/sveltekit/translations` supplies locale routing and
server request helpers, while `/translations/navigation` supplies browser
navigation helpers.
