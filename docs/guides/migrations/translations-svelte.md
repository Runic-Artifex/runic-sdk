# Migrating Svelte translation imports

Runic Translations is moving its Svelte integrations out of the SDK. The first
independent previews are not published yet. Existing applications can remain on
their current SDK release until they choose the matching Translations preview.

| Current import                                     | Independent Translations import                                 |
| -------------------------------------------------- | --------------------------------------------------------------- |
| `@runic-artifex/svelte`                            | `@runic-artifex/translations-svelte`                            |
| `@runic-artifex/svelte/translations`               | `@runic-artifex/translations-svelte/translations`               |
| `@runic-artifex/svelte/translations/testing`       | `@runic-artifex/translations-svelte/translations/testing`       |
| `@runic-artifex/svelte/inline`                     | `@runic-artifex/translations-svelte/inline`                     |
| `@runic-artifex/sveltekit/translations`            | `@runic-artifex/translations-sveltekit/translations`            |
| `@runic-artifex/sveltekit/translations/navigation` | `@runic-artifex/translations-sveltekit/translations/navigation` |

`@runic-artifex/svelte/views` remains an SDK import. The root
`@runic-artifex/svelte` import becomes Views-only. The root
`@runic-artifex/sveltekit` import and its `/page-options` subpath also remain
SDK-owned. Do not mix the independent preview packages with an older SDK
Translations integration; upgrade the translation packages as one product
family and run the application's normal build and tests.
