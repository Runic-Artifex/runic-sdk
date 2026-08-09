# `@runic-artifex/sveltekit`

Static/native SvelteKit adapter for Runic Toolkit applications.

```js
// svelte.config.js
import { runicToolkitAdapter } from "@runic-artifex/sveltekit";

export default {
  kit: {
    adapter: runicToolkitAdapter({ mode: "prerendered" }),
  },
};
```

Use `mode: "spa"` for an entirely host-driven UI. It generates `200.html` by
default and records that entrypoint in `runic-toolkit.sveltekit.json`. The
prerendered mode keeps SvelteKit SSR/prerendering available and initializes the
native bridge only after hydration.

Import route constants from the browser-safe subpath so client compilation does
not traverse the Node-only adapter implementation:

```ts
import { runicToolkitSpaPageOptions } from "@runic-artifex/sveltekit/page-options";

export const ssr = runicToolkitSpaPageOptions.ssr;
export const prerender = runicToolkitSpaPageOptions.prerender;
```

Add `runicToolkit()` and the official `DevTools()` plugin to `vite.config.ts`.
The SvelteKit package does not duplicate their development runtime.

SvelteKit remains authoritative for URLs, history, and page state even when the
host uses Runic Flow. Map routes to named application commands in application
code; do not mirror a Flow process graph into a second router owned by this
adapter.
