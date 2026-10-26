# `@runic-artifex/sveltekit`

SvelteKit adapter for Runic Desktop applications. Application Views use the
`@runic-artifex/svelte` export.
`dotnet new runic-app --frontend sveltekit` (Runic.Application.Templates) starts
an application with this adapter, on Runic Desktop or CS-WebUI.

```ts
// vite.config.ts
import { runicAdapter } from "@runic-artifex/sveltekit";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [sveltekit({ adapter: runicAdapter({ desktop: true }) })],
});
```

Under `dotnet runic dev`, add `runicDevSession()` from
`@runic-artifex/vite-plugin-runic` to the plugins, as the template does, so
that `dev` can confirm that the development server on its port is the one it
started. Use it instead of `runic()`, whose `transformIndexHtml` hook SvelteKit
warns about.

The package requires SvelteKit 3 and `@sveltejs/adapter-static` 4, which take
their configuration through the `sveltekit()` Vite plugin, Svelte 5.57.1 or
later, and Node 24.18 or later 24.x, or Node 26. The adapter emits a relocatable page. Use `mode: "spa"` with
`router: { type: "hash" }` for a Desktop SPA.

The options depend on the mode, and a field the mode would ignore does not
type-check:

| Option | Modes | Default | Meaning |
| --- | --- | --- | --- |
| `mode` | | `"prerendered"` | `"prerendered"`: every page is an HTML file. `"spa"`: one fallback file renders every route in the browser. |
| `entrypoint` | prerendered | the page of `/`, or the only page | The output-relative HTML file the host opens, for a multi-page application. |
| `fallback` | spa | `200.html` | The output-relative fallback file, which is also the entrypoint. |
| `desktop` | both | `false` | Loads `runic-desktop.js` and makes the page relocatable under a Runic Desktop surface. |
| `out` | both | `build` | The output directory. |
| `precompress` | both | `false` | Writes `.br` and `.gz` copies. |
| `strict` | both | `true` | Fails when a route is neither prerendered nor covered by a fallback. |

`desktop: true` makes the page load `runic-desktop.js` and relocatable under a
Runic Desktop surface. It stays opt-in, because Views pages and CS-WebUI do not
need it: without it the adapter is `adapter-static` with Runic's entry-point
checks, for a frontend that another host serves, such as CS-WebUI, or a plain
browser build.

The `@runic-artifex/sveltekit/page-options` entry point, which runs in the
browser, provides matching route options; the root entry is Node-only and does
not export them. Apply them in the root `+layout.ts`; a prerendered application exports
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
