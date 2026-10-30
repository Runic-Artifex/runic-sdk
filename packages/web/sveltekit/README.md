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

## Fields

Pages bind inputs with `useField` from `@runic-artifex/svelte` and the generated
`{View}Fields` descriptor, as any Svelte application does:

```svelte
<script lang="ts">
  import { useField, useView } from "@runic-artifex/svelte";
  import { EditorFields, type EditorPageReference } from "$lib/generated/editor.js";

  let { page }: { page: EditorPageReference } = $props();
  const editor = useView(() => page);
  const title = useField(() => editor, EditorFields.title, { commit: "pause", checked: true });
</script>

<input {@attach title.attach}>
```

`commit: "pause"` writes on blur or after a pause, `checked: true` keeps a rejected
or conflicting draft with its `FieldError` until `retry()` or `reset()`, and `errors`
holds the member's validation messages; see the
[`@runic-artifex/svelte` README](https://www.npmjs.com/package/@runic-artifex/svelte).
This package's entry is the build-time adapter, which runs in Node, so it does not
re-export the browser bindings.

## Connecting in `load`

A route's `load` function can connect its View before the page renders, so
SvelteKit keeps the previous page until the View is ready, a failure renders
`+error.svelte`, and the page shows the View's state in its first render.
`preloadView(reference)` from `@runic-artifex/svelte` (or
`@runic-artifex/views`) connects the reference, keeps the client for a few
seconds and resolves with the reference; the page's `useView` of that reference
takes the client over. Views connect in the browser only, so the route turns
off server rendering:

```ts
// src/routes/editor/+page.ts
import { preloadView } from "@runic-artifex/svelte";
import { connectEditor } from "$lib/generated/editor.js";

export const ssr = false;
export const load = async () => ({ editor: await preloadView({ connect: connectEditor }) });
```

```svelte
<!-- src/routes/editor/+page.svelte -->
<script lang="ts">
  import { useView } from "@runic-artifex/svelte";
  let { data } = $props();
  const editor = useView(() => data.editor);
</script>

<h1>{editor.state?.title}</h1>
```

The reference is identified by its `connect` function, so keep it stable (a
generated page reference, or `{ connect: connectEditor }` as above: a new
object with the same function is the same reference). A client that no page
takes over is disposed after `keepAlive` (2000 ms by default;
`preloadView(reference, { keepAlive: 10_000 })` for a slower page). A Desktop
SPA with the hash router sets no page options; its pages render in the browser
anyway. Without `load`, `useSuspenseView` and `{#await}` from
`@runic-artifex/svelte` connect inside the page.

Translation routing helpers moved to
[`@runic-artifex/translations-sveltekit`](https://www.npmjs.com/package/@runic-artifex/translations-sveltekit).
Import them from there instead of `@runic-artifex/sveltekit`.
