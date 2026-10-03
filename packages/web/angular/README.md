# `@runic-artifex/angular`

Angular outlet and signals for generated Runic View clients. The generated
modules import `@runic-artifex/views`, so install both:

```sh
npm install @runic-artifex/angular@preview @runic-artifex/views@preview
pnpm add @runic-artifex/angular@preview @runic-artifex/views@preview
bun add @runic-artifex/angular@preview @runic-artifex/views@preview
```

Previews are published under the `preview` dist-tag. Install the versions that
match your Runic SDK packages.

## Connecting a View

`injectView(source)` connects a generated page reference, or
`{ connect: connect<Name> }` for a root ViewModel, for the lifetime of the
injection context and returns `state`, `client` and `error` signals and
`retry()`:

```ts
import { Component, input } from "@angular/core";
import { injectView } from "@runic-artifex/angular";
import type { CounterPageReference } from "./generated/counter";

@Component({
  selector: "counter-page",
  template: `
    <button [disabled]="!counter.client()" (click)="counter.client()?.increment()">
      {{ counter.state()?.count ?? "…" }}
    </button>
  `,
})
export class CounterPage {
  readonly page = input.required<CounterPageReference>();
  readonly counter = injectView(this.page);
}
```

Pass a signal, such as an input, to follow it: a reference with a different
`connect` function releases the previous client and connects the new one. An
already connected client is observed and left to its owner. Outside an
injection context, pass `{ injector }`; pass `{ release }` to defer disposal,
for example until queued commands finish.

## Outlet

Register a component for each generated View kind and pass the current
reference to the outlet.

```ts
import { RunicViewOutlet, type ViewRegistry } from "@runic-artifex/angular";
import { DocumentPage } from "./document-page";
import type { DocumentReference } from "./generated/document";

const registry = { document: DocumentPage } satisfies ViewRegistry<DocumentReference>;

// Add RunicViewOutlet to the parent's imports and bind:
// <runic-view-outlet [content]="current()" [registry]="registry" />
```

The outlet remounts the component when the logical View reference changes. A
component's `page` input receives that reference; `injectView(this.page)` owns
its connection and cleanup. Missing kinds render an alert. The generated
TypeScript client can also be used directly without Angular.
