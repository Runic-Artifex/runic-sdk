# `@runic-artifex/views-svelte`

Svelte 5 outlet for generated Runic View references. The browser component owns its
visual tree and connection to the generated page client. The outlet selects it
from a statically typed registry and remounts it when the View reference changes.

```svelte
<script lang="ts">
  import ViewOutlet from "@runic-artifex/views-svelte/ViewOutlet.svelte";
  import type { ViewRegistry } from "@runic-artifex/views-svelte/view-registry";
  import DocumentPage from "./DocumentPage.svelte";
  import type { DocumentReference } from "./generated/document.js";

  let { current }: { current: DocumentReference | null } = $props();
  const registry = { document: DocumentPage } satisfies ViewRegistry<DocumentReference>;
</script>

<ViewOutlet content={current} {registry} />
```

`ViewRegistry` checks each generated `kind` and its component's `page` prop.
Missing kinds render an alert. A changed reference unmounts the previous
component, so each component can clean up its own page connection.

The generated TypeScript client remains usable directly without Svelte.
