# `@runic-artifex/svelte`

Svelte 5 helpers for Runic Translations, inline content, and Application Views.

For locale context in a Svelte tree:

```ts
import { createLocaleContext } from "@runic-artifex/svelte/translations";
```

The browser-safe `@runic-artifex/svelte/translations/testing` entry point
provides pseudo-localization, RTL isolation, plural boundary values, and
accessibility stress fixtures. `@runic-artifex/svelte/inline` exports
`LocalizedInline` and `inlineFactory` for localized structured content.
`@runic-artifex/svelte/views` exports `ViewOutlet`, `ViewReference`, and
`ViewRegistry` for generated Window/View clients.

See the [Translations guide](https://github.com/Runic-Artifex/runic-sdk/tree/main/docs/guides/translations)
for catalog generation and the SvelteKit locale routing helpers.
