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
`@runic-artifex/svelte/views` exports `ViewOutlet`, `ViewReference`,
`ViewRegistry` and `useView` for generated Window/View clients.

Generated View clients import `@runic-artifex/views`, so a Views application
installs both packages:

```sh
npm install @runic-artifex/svelte@preview @runic-artifex/views@preview
pnpm add @runic-artifex/svelte@preview @runic-artifex/views@preview
bun add @runic-artifex/svelte@preview @runic-artifex/views@preview
```

Previews are published under the `preview` dist-tag. Install the versions that
match your Runic SDK packages.

```svelte
<script lang="ts">
  import { useView } from "@runic-artifex/svelte/views";
  import type { CounterPageReference } from "./generated/counter.js";

  let { page }: { page: CounterPageReference } = $props();
  const counter = useView(() => page);
</script>

<button disabled={!counter.client} onclick={() => counter.client?.increment()}>
  {counter.state?.count ?? "…"}
</button>
```

Call `useView` during component initialization. It connects a generated page
reference, or `{ connect: connect<Name> }` for a root ViewModel, while the
component is mounted and disposes it on destroy or when the getter returns a
reference with a different `connect` function. An already connected client is
observed and left to its owner. The result exposes reactive `state`, `client`
and `error` properties and `retry()`.

See the [Translations guide](https://github.com/Runic-Artifex/runic-sdk/tree/main/docs/guides/translations)
for catalog generation and the SvelteKit locale routing helpers.
