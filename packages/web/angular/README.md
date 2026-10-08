# `@runic-artifex/angular`

Angular outlet and signals for generated Runic View clients. The generated
modules import `@runic-artifex/views`, which this package also builds on, so
install both:

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
injection context and returns `state`, `client`, `error` and `pending`
signals and `retry()`:

```ts
import { Component, input } from "@angular/core";
import { injectCommand, injectView } from "@runic-artifex/angular";
import type { CounterPageReference } from "./generated/counter";

@Component({
  selector: "counter-page",
  template: `
    <button [disabled]="!counter.client() || increment.pending()" (click)="increment.run()">
      {{ counter.state()?.count ?? "…" }}
    </button>
    @if (increment.error() ?? counter.error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class CounterPage {
  readonly page = input.required<CounterPageReference>();
  readonly counter = injectView(this.page);
  readonly increment = injectCommand(() => this.counter.client()?.increment());
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

The outlet is generic over the reference union it receives, so the registry
must have a component for every kind whose `page` input accepts that reference.
It remounts the component when the logical View reference changes, and creates
it outside its own signal tracking. A component's `page` input receives that
reference; `injectView(this.page)` owns its connection and cleanup. Missing
kinds render an alert. The generated TypeScript client can also be used
directly without Angular.

## Commands

`injectCommand(command)` returns `run(...args)` and `pending`, `error` and
`failure` signals. `run` resolves to the command's result, or to `undefined` after a
failure, which `error` then holds until the next run. It never rejects, so
handlers need no `try`/`catch`. One command can serve several buttons:

```ts
readonly navigate = injectCommand((name: "showWelcome" | "showCounter") => this.workspace.client()?.[name]());
// <button (click)="navigate.run('showWelcome')">Welcome</button>
```

For a command that declares a failure (`[RunicFailure]` in .NET), `run` resolves the
`BridgeOutcome`, and `failure` holds the declared failure of the latest run, typed
from the outcome, while `error` keeps unexpected failures. Starting a run and
`reset()` clear both, and a run superseded by a later run or by `reset()` sets
neither.

Outside an injection context, pass `{ injector }`.

`injectCommand` runs are independent; it doesn't order one command after
another. If an app queues commands so that each starts after the previous one
completes, a command whose completion waits for a later command deadlocks.
For example, a navigation command whose departure guard awaits a dialog's
answer never completes while the answer waits behind it. Order such commands
by dispatch instead: start the next one once the previous one has been sent,
as the Notes example's
[`WindowOperations.runDispatched`](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first/Angular/src/app/window-operations.ts)
does for its navigation commands.

## Collection viewports

`injectCollectionViewport(element, options)` follows a fixed-row-height scroll
container and returns a signal with its
[`collectionViewport`](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#collectionviewportoptions)
range. Pass the container as a signal, such as a `viewChild`, and the options as
a function or signal, then send the range to the ViewModel from an effect:

```ts
@Component({
  selector: "rows-page",
  template: `
    <div #scroller style="height: 480px; overflow: auto">
      <div [style.height.px]="viewport().totalSize"><!-- rows.state()?.rows --></div>
    </div>
  `,
})
export class RowsPage {
  readonly page = input.required<RowsPageReference>();
  readonly rows = injectView(this.page);
  readonly viewport = injectCollectionViewport(viewChild<ElementRef<HTMLElement>>("scroller"),
    () => ({ totalCount: this.rows.state()?.totalCount ?? 0, rowHeight: 32 }));

  constructor() {
    effect(() => {
      const { start, size } = this.viewport();
      const client = this.rows.client();
      if (client && size !== 0) void client.setViewport({ start, size });
    });
  }
}
```
