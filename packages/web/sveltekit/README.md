# `@runic-artifex/sveltekit`

SvelteKit adapter for Runic Desktop applications. Application Views use the
`@runic-artifex/svelte/views` export.

```ts
// vite.config.ts
import { runicAdapter } from "@runic-artifex/sveltekit";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [sveltekit({ adapter: runicAdapter({ desktop: true }) })],
});
```

The package requires SvelteKit 3 and `@sveltejs/adapter-static` 4, which take
their configuration through the `sveltekit()` Vite plugin, and Svelte 5.57.1 or
later. The adapter emits a relocatable page. Use `mode: "spa"` with
`router: { type: "hash" }` for a Desktop SPA.

`desktop: true` makes the page load `runic-desktop.js` and relocatable under a
Runic Desktop surface. It is off by default: without it the adapter is
`adapter-static` with Runic's entry-point checks, for a frontend that another
host serves, such as CS-WebUI, or a plain browser build.

The `@runic-artifex/sveltekit/page-options` entry point provides matching route
options. Apply them in the root `+layout.ts`; a prerendered application exports
`prerender`:

```ts
// src/routes/+layout.ts
import { runicPrerenderedPageOptions } from "@runic-artifex/sveltekit/page-options";

export const { prerender } = runicPrerenderedPageOptions;
```

A SPA without `desktop` exports `ssr` and `prerender` from `runicSpaPageOptions`
the same way. A Desktop SPA exports neither: SvelteKit rejects page options with
the hash router.

Translation routing helpers moved to
[`@runic-artifex/translations-sveltekit`](https://www.npmjs.com/package/@runic-artifex/translations-sveltekit).
Import them from there instead of `@runic-artifex/sveltekit`.
