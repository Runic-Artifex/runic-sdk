# `@runic-artifex/views-angular`

Angular outlet for generated Runic View references. Register a component for
each generated View kind and pass the current reference to the outlet.

```ts
import { RunicViewOutlet, type ViewRegistry } from "@runic-artifex/views-angular";
import { DocumentPage } from "./document-page";
import type { DocumentReference } from "./generated/document";

const registry = { document: DocumentPage } satisfies ViewRegistry<DocumentReference>;

// Add RunicViewOutlet to the parent's imports and bind:
// <runic-view-outlet [content]="current()" [registry]="registry" />
```

The outlet remounts the component when the logical View reference changes. A
component's `page` input receives that reference and owns its page connection
and cleanup. Missing kinds render an alert. The generated TypeScript client can
also be used directly without Angular.
