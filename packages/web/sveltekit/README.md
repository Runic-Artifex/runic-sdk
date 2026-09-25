# `@runic-artifex/sveltekit`

SvelteKit adapter and locale routing helpers for Runic Desktop and Runic
Translations. Application Views use the separate `@runic-artifex/views-svelte`
outlet package.

```ts
import { runicToolkitAdapter } from "@runic-artifex/sveltekit";

export default { kit: { adapter: runicToolkitAdapter({ desktop: true }) } };
```

The adapter emits a relocatable page and `runic-toolkit.sveltekit.json` manifest.
Use `mode: "spa"` with hash routing for a Desktop SPA. The
`@runic-artifex/sveltekit/page-options` entry point provides matching route
options. `@runic-artifex/sveltekit/translations` supplies locale routing and
server request helpers, while `/translations/navigation` supplies browser
navigation helpers.
