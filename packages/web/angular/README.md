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
match your Runic SDK packages. Angular 22 is supported.

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

### Read-only state and two-way binding

As in any MVVM View, the ViewModel's C# setters decide what the page may set.
Read state one way from the `state` signal. A property with a public C# setter
also gets a `set<Property>` client method: bind the control's value from
`state` and call the setter from its change event. Status with a
`private set`, such as `IsDirty` or `Error`, has no setter and is only rendered:

```ts
// C#: public partial int Count { get; private set; }  public partial int Step { get; set; }
@Component({
  selector: "counter-page",
  template: `
    <p>{{ counter.state()?.count ?? "…" }}</p>
    <select #step (change)="setStep.run(+step.value)">
      @for (value of [1, 5]; track value) {
        <option [value]="value" [selected]="value === (counter.state()?.step ?? 1)">{{ value }}</option>
      }
    </select>
  `,
})
export class CounterPage {
  readonly page = input.required<CounterPageReference>();
  readonly counter = injectView(this.page);
  readonly setStep = injectCommand((step: number) => this.counter.client()?.setStep(step));
}
```

## Outlet

Register a component for each generated View kind and pass the current
reference to the outlet.

```ts
import { ViewOutlet, type ViewRegistry } from "@runic-artifex/angular";
import { DocumentPage } from "./document-page";
import type { DocumentReference } from "./generated/document";

const registry = { document: DocumentPage } satisfies ViewRegistry<DocumentReference>;

// Add ViewOutlet to the parent's imports and bind:
// <runic-view-outlet [content]="current()" [registry]="registry">Connecting…</runic-view-outlet>
```

The outlet's projected content renders while there is no reference, like
React's `fallback`, Vue's default slot and Svelte's `fallback` snippet.

`ViewOutlet` is the outlet's name in every Runic frontend package (React, Vue,
Svelte and Angular); its element keeps Angular's prefix convention,
`<runic-view-outlet>`. Before 0.7.0-preview.7 the Angular class was
`RunicViewOutlet`. That name is still exported as a deprecated subclass that
renders the same element, and is removed in the next preview.

The outlet is generic over the reference union it receives, so the registry
must have a component for every kind whose `page` input accepts that reference.
It remounts the component when the logical View reference changes, and creates
it outside its own signal tracking. A component's `page` input receives that
reference; `injectView(this.page)` owns its connection and cleanup. Declare
`page` as a required signal input, `input.required<DocumentReference>()`: a
decorator `@Input() page` or an optional `input<DocumentReference>()` does not
satisfy `ViewRegistry`. Missing kinds render an alert. The generated TypeScript
client can also be used directly without Angular.

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

## Operations

`injectOperation(start, options?)` observes a generated `start<Command>()` operation
through the framework-neutral
[operation controller](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#operation-feedback-and-latest-selection)
for the lifetime of the injection context:

```ts
readonly push = injectOperation((remote: string) => this.workspace.client()?.startPush(remote));
// <button [disabled]="!workspace.client() || push.pending()" (click)="push.run('origin')">Push</button>
// @if (push.pending()) { <button [disabled]="push.cancellationRequested()" (click)="push.cancel()">Cancel</button> }
// @if (push.cancelled()) { <p role="status">Push cancelled.</p> }
// @if (push.error() !== undefined) { <p role="alert">{{ push.error() }}</p> }
```

It returns `run(...args)`, `cancel()` and `reset()`, which never reject, and the
`pending`, `admitting`, `operation`, `status`, `outcome`, `failure`, `error`,
`cancelled`, `cancellationRequested`, `cancelling`, `cancellation` and `cancelError`
signals. A run that ends with a `cancelled` status sets `cancelled` and resolves to
`undefined` without an `error`; `failed` and `timed-out` statuses are still `error`.
`pending` stays true after a Cancel response until the operation reaches its terminal
status. Destroying the injection context settles run promises without cancelling
accepted work. The options also take the controller's `cancel(operation)` and
`waitForCompletion(operation, status)` callbacks, and `injector` outside an injection
context.

`injectLatestOperation(options?)` admits only the newest of several reads, for example
loading the item the user selected last, through the framework-neutral
[latest-operation controller](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#operation-feedback-and-latest-selection).
Each `run(intent)` replaces the queued intent and cancels the active read; the next
read starts only after the previous one reaches its terminal status and the intent's
optional `waitForCompletion` barrier:

```ts
readonly load = injectLatestOperation<Item>();
select(id: string) {
  const client = this.items.client();
  if (client) void this.load.run({ start: () => client.startLoad(id), isCurrent: () => this.items.client() === client });
}
// @if (load.outcome()?.ok) { <item-view [item]="load.outcome()!.value" /> }
// @if (load.blocked()) { <p role="alert">Reconnect before selecting again.</p> }
```

It returns the `injectOperation` signals plus `blocked`, with `run(intent)` and
`clear(mode?)`, which never reject. The intent carries `start` and the optional
`cancel`, `waitForCompletion` and `isCurrent` callbacks, so it captures the client or
session it belongs to. `blocked` means admission is uncertain, for example after a
lost Start reply; only `clear("replace")`, called after the application has
established a fresh session, clears it. `clear()` drops the queued intent and cancels
the active read. Destroying the injection context settles run promises without
cancelling accepted work. Pass `injector` outside an injection context.

## Errors

The `error()` signal of `injectView` and of a command or operation is typed
`unknown`: it holds whatever the connection or the call rejected with. A Bridge
failure is a `BridgeError` from `@runic-artifex/views`; narrow it with
`instanceof` before reading `kind`, `route` or `detail`:

```ts
import { BridgeError } from "@runic-artifex/views";

const error = this.view.error();
const offline = error instanceof BridgeError && error.kind === "disconnected";
```

`instanceof` holds for errors from every copy of the package on the page.
[Errors and diagnostics](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#errors-and-diagnostics)
lists the kinds. Any other value, such as an exception that your own connect
function or command callback throws, arrives unchanged.

The signals run in the browser or webview that a Runic host serves. `injectView`
connects in an effect, which Angular also runs in a server render: render the
components that use it on the client only.

## Interactions

A ReactiveUI `Interaction<TInput, TOutput>` on the ViewModel, such as the
question of a `ReactiveLeaveConfirmation.WithInteraction` leave guard, is
`client.interactions.<name>` on the generated client. Register the page's answer
in an `effect`: `handle((prompt, { signal }) => …)` returns the function that
removes it, so pass that to `onCleanup`, and Angular removes the handler when
the client changes or the component is destroyed:

```ts
import { Component, effect, input } from "@angular/core";
import { injectView } from "@runic-artifex/angular";
import type { EditPageReference } from "./generated/edit";

@Component({ selector: "edit-page", template: `…` })
export class EditPage {
  readonly page = input.required<EditPageReference>();
  readonly view = injectView(this.page);

  constructor() {
    effect(onCleanup => {
      const remove = this.view.client()?.interactions.confirmDiscard.handle(
        async (prompt, { signal }) => confirmDialog(`Discard the changes to ${prompt.itemName}?`, signal),
      );
      if (remove) onCleanup(remove);
    });
  }
}
```

`prompt` and the answer are typed from the ViewModel's interaction. The handler
may return the answer or a promise of it; `confirmDialog` stands for the
application's own dialog.

- The page is asked for a departure that its own window starts: its Close, the
  shell's Back in the same window, or a guarded window close.
- `signal` aborts when .NET no longer needs the answer, for example when the
  departure is superseded or the window closes. Close the dialog then; a late
  answer is ignored.
- The handler exists only after the client connected and the effect ran. A
  departure before that is answered by the ViewModel's .NET fallback handler, or
  fails closed without one.

The [ReactiveUI leave confirmation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#leave-confirmation-in-a-web-view)
has the .NET side and which View answers which departure. In a frontend test,
the generated mock's `interactions.<name>.request(input)` resolves with the
registered handler's answer; see the [Testing guide](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#testing).

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
