# `@runic-artifex/svelte`

Svelte 5 helpers for Runic Translations and inline content. Application Views
use the separate `@runic-artifex/views-svelte` outlet package.

For locale context in a Svelte tree:

```ts
import { createLocaleContext } from "@runic-artifex/svelte/translations";
```

The browser-safe `@runic-artifex/svelte/translations/testing` entry point
provides pseudo-localization, RTL isolation, plural boundary values, and
accessibility stress fixtures. `@runic-artifex/svelte/inline` exports
`LocalizedInline` and `inlineFactory` for localized structured content.

See the [Translations guide](https://github.com/Runic-Artifex/runic-sdk/tree/main/docs/guides/translations)
for catalog generation and the SvelteKit locale routing helpers.
