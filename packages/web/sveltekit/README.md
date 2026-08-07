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

Add `runicToolkit()` and the official `DevTools()` plugin to `vite.config.ts`.
The SvelteKit package does not duplicate their development runtime.

