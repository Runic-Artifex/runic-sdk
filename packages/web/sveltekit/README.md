# `@runic-artifex/sveltekit`

SvelteKit adapter for Runic Desktop applications. Application Views use the
`@runic-artifex/svelte/views` export.

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
options.

Translation routing helpers moved to
[`@runic-artifex/translations-sveltekit`](https://github.com/Runic-Artifex/runic-translations-sdk/tree/main/packages/web/translations-sveltekit).
Use a prior SDK preview until application imports have been migrated.
