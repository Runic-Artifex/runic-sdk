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
import { describeBridgeError } from "@runic-artifex/views";
import type { CounterPageReference } from "./generated/counter";

@Component({
  selector: "counter-page",
  template: `
    <button [disabled]="!counter.client() || increment.pending()" (click)="increment.run()">
      {{ counter.state()?.count ?? "…" }}
    </button>
    @if (increment.error() ?? counter.error(); as issue) { <p role="alert">{{ describe(issue) }}</p> }
  `,
})
export class CounterPage {
  readonly page = input.required<CounterPageReference>();
  readonly counter = injectView(this.page);
  readonly increment = injectCommand(() => this.counter.client()?.increment());
  readonly describe = describeBridgeError;
}
```

Pass a signal, such as an input, to follow it: a reference with a different
`connect` function releases the previous client and connects the new one. An
already connected client is observed and left to its owner. Outside an
injection context, pass `{ injector }`; pass `{ release }` to defer disposal,
for example until queued commands finish.

A released client stays connected for `keepAlive` milliseconds (2000 by
default), and the next `injectView` of the same reference takes it over with
its state, before its effect runs: an `@if` that toggles, HMR and A→B→A
navigation show the View at once instead of "Connecting…" and do not open the
route again. `{ keepAlive: 0 }` disposes at once.

### As a resource: `injectViewResource(source, options?)`

`injectViewResource` connects like `injectView` and returns an Angular
`Resource` (D053.11): `status()` is `loading` until the View connected, then
`resolved`, or `error` with the failure in `error()`; `value()` is the state,
never `undefined` once `hasValue()` is true; `client()` is the client and
`reload()` connects again. Templates branch on it as on any resource:

```ts
@Component({
  selector: "counter-page",
  template: `
    @if (counter.hasValue()) {
      <button (click)="counter.client()?.increment()">{{ counter.value().count }}</button>
    } @else if (counter.error(); as issue) {
      <p role="alert">{{ describe(issue) }}</p>
      <button (click)="counter.reload()">Retry</button>
    } @else {
      <p>Connecting…</p>
    }
  `,
})
export class CounterPage {
  readonly page = input.required<CounterPageReference>();
  readonly counter = injectViewResource(this.page);
  readonly describe = describeBridgeError;
}
```

A changed reference loads the new one, `null` makes it `idle`, and the
injector's destruction releases the client, with the same `keepAlive` as
`injectView`.

### Selecting state: `computed`

`state()` changes with every change of the View. A `computed` that selects a
member notifies its consumers only when its value changed (`Object.is`, or its
`equal` option), so a template binding or an `effect` that reads `title()` does
not run again when another member changes:

```ts
readonly editor = injectView(this.page);
readonly title = computed(() => this.editor.state()?.title);
readonly done = computed(() => this.editor.state()?.items.filter(item => item.done) ?? [],
  { equal: (left, right) => left.length === right.length && left.every((item, index) => item === right[index]) });
```

Members the View did not change keep their identity, so selecting a member,
also an object or a list, needs no `equal`; a derived value such as `filter`
does.

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

### Text inputs

Bind a text input to a member with `injectTextField(view, read, write, options?)`,
which wraps the framework-neutral
[text field controller](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#text-inputs)
for the lifetime of the injection context:

```ts
@Component({
  selector: "person-page",
  template: `
    <input [value]="name.value()" (input)="name.input($event)"
      (compositionstart)="name.compositionStart()" (compositionend)="name.compositionEnd($event)">
    @if (name.error() !== undefined) { <p role="alert">{{ name.error() }}</p> }
  `,
})
export class PersonPage {
  readonly page = input.required<PersonPageReference>();
  readonly person = injectView(this.page);
  readonly name = injectTextField(this.person, state => state.name, (client, value) => client.setName(value));
}
```

It returns the `value`, `editing` and `error` signals with `input(event | text)`,
`compositionStart()`, `compositionEnd(event | text)` and `reset()`. `view` is an
`injectView` handle or any pair of `state` and `client` signals. The options are
`coalesce`, `keepDraftOnError` and `injector` outside an injection context.
The field shows what the user typed while its writes are in flight, so a reply or
frame that answers an older keystroke never replaces newer text. At most one write is
in flight; text typed meanwhile is written next as one value (`coalesce: false` writes
every change). Nothing is written during IME composition. Once the last write settled,
the field shows the View's value again, including any normalization the ViewModel
applied (trimmed, upper-cased). `write` may also be a command or a checked write,
whose receipt is read: a `rejected` or `conflict` receipt keeps the draft and sets
`error` to a `FieldError` of that kind, and `committed-with-error` drops the draft and
sets one too (see [Fields](#fields)). A failed write keeps the draft and sets `error`
(`keepDraftOnError: false` shows the View's value instead); `reset()` drops the draft. `editing` is true while a draft
exists. Don't disable the input on the View's busy state (`isSaving`, `can…`) while
the user types: the field's own write causes it, and keys typed meanwhile would be
lost. Test such fields by typing key by key (Playwright `pressSequentially`, Testing
Library `user.type`) against the mock with `latency: "slow"`; `fill()` sets the whole
text at once and hides the race.

## Fields

For generated Views prefer `injectField(view, field, options?)`: pass a descriptor of the
generated `{View}Fields`, such as `EditorFields.title`. It wraps the framework-neutral
[field controller](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#fields)
for every kind (text, numbers, booleans, enums) and returns signals and actions:

- `value`, `text` (what a text input shows: `1.` stays while typing `1.5`), `editing`,
  `error` (a `BridgeError` or a `FieldError`), `errors` (the member's validation
  messages) and `conflict`;
- `input(value)`, `inputText(event | text)`, `compositionStart()`,
  `compositionEnd(event | text)`, `commit()`, `retry()` and `reset()`;
- `field`: the descriptor's `kind`, `nullable`, `integer` and `choices`.

```ts
import { Component, input } from "@angular/core";
import { injectField, injectView } from "@runic-artifex/angular";
import { EditorFields, type EditorPageReference } from "./generated/editor";

@Component({
  selector: "editor-page",
  template: `
    <input [value]="title.text()" [attr.aria-invalid]="title.errors().length > 0 || null"
      (input)="title.inputText($event)" (blur)="title.commit()"
      (compositionstart)="title.compositionStart()" (compositionend)="title.compositionEnd($event)">
    @for (message of title.errors(); track message) { <p role="alert">{{ message }}</p> }
  `,
})
export class EditorPage {
  readonly page = input.required<EditorPageReference>();
  readonly editor = injectView(this.page);
  readonly title = injectField(this.editor, EditorFields.title);
}
```

The options are those of the controller and `injector`:

- `commit: "pause"` writes the draft after `delay` milliseconds without input (700
  by default) and at once on `commit()`, which blur calls; use it where each write is
  an undo step or starts work in .NET. `commit()` resolves once the field's writes
  settled, so a command that depends on the field can follow it:
  `await this.title.commit(); await this.editor.client()?.save();`.
- `checked: true` writes with the member's checked write: a change by another writer
  since the draft started is a conflict instead of being overwritten.
- `coalesce` and `keepDraftOnError`, as for `injectTextField`.

A checked write's receipt is read: `rejected` keeps the draft with a `FieldError`
`rejected`; `conflict` keeps the draft, sets `conflict` to the producer's
`{ value, version }` and writes nothing until `retry()` (write the draft over it) or
`reset()` (take the producer's value); `committed-with-error` keeps the committed value
with a `FieldError`. `errors` follows the member's validation in the View's state:

```html
@if (title.conflict(); as theirs) {
  <p role="alert">Changed elsewhere to “{{ theirs.value }}”.
    <button (click)="title.retry()">Keep mine</button> <button (click)="title.reset()">Take theirs</button></p>
}
```

### `[runicField]` and Reactive Forms

`@runic-artifex/angular/forms` needs `@angular/forms` (an optional peer; the main
entry does not). `RunicFieldDirective` wires an `<input>`, `<textarea>` or
`<select>` in one attribute: input (`change` for checkboxes, radios and selects),
composition, `commit()` on blur, the element's `value` or `checked`, and
`aria-invalid`:

```ts
import { RunicFieldDirective } from "@runic-artifex/angular/forms";

@Component({
  selector: "editor-page",
  imports: [RunicFieldDirective],
  template: `<input [runicField]="title"> <input type="checkbox" [runicField]="pinned">`,
})
export class EditorPage {
  readonly page = input.required<EditorPageReference>();
  readonly editor = injectView(this.page);
  readonly title = injectField(this.editor, EditorFields.title, { commit: "pause" });
  readonly pinned = injectField(this.editor, EditorFields.pinned);
}
```

It is also the `ControlValueAccessor` and validator of a `formControl` or
`formControlName` on the same element. The View's value flows into the control
without marking it dirty or emitting `valueChanges`, and a control's initial value
is not written; what the user types reaches the control as usual; `setValue`,
`patchValue` and `reset` write through to the View when they change the value (a
`null` from `reset()` of a nullable control is ignored for a non-nullable field);
`disable()` disables the element; and the control has the error
`{ runic: errors }` while the View reports validation messages for the member:

```ts
import { FormControl, FormGroup, ReactiveFormsModule } from "@angular/forms";

@Component({
  selector: "editor-page",
  imports: [RunicFieldDirective, ReactiveFormsModule],
  template: `
    <form [formGroup]="form">
      <input formControlName="title" [runicField]="title">
      @if (form.controls.title.errors?.["runic"]; as messages) { <p role="alert">{{ messages[0] }}</p> }
    </form>
  `,
})
export class EditorPage {
  readonly page = input.required<EditorPageReference>();
  readonly editor = injectView(this.page);
  readonly title = injectField(this.editor, EditorFields.title);
  readonly form = new FormGroup({ title: new FormControl("", { nonNullable: true }) });
}
```

### Signal Forms

`runicFieldModel(handle)` is a `WritableSignal` over a field for `form()` from
`@angular/forms/signals`: it reads `value`, and `set`/`update` call `input`.
`validateRunicField(path, handle)` adds the field's `errors` to the schema as
errors of kind `runic` with the message:

```ts
import { form, FormField } from "@angular/forms/signals";
import { runicFieldModel, validateRunicField } from "@runic-artifex/angular/forms";

@Component({
  selector: "editor-page",
  imports: [FormField],
  template: `<input [formField]="titleForm" (blur)="title.commit()">`,
})
export class EditorPage {
  readonly page = input.required<EditorPageReference>();
  readonly editor = injectView(this.page);
  readonly title = injectField(this.editor, EditorFields.title);
  readonly titleForm = form(runicFieldModel(this.title), path => validateRunicField(path, this.title));
}
```

Signal Forms does not tell the field when the element loses focus: always bind
`(blur)="title.commit()"` next to `[formField]`, as above. Without it a
`commit: "pause"` field writes only after the pause, and a value typed just before
Save may still wait. `[formField]` also writes during IME composition; prefer
`[runicField]` where composition matters.

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
`RunicViewOutlet`. That name is still exported as a deprecated alias of the
same class, and is removed in the next major release.

The outlet is generic over the reference union it receives, so the registry
must have a component for every kind whose `page` input accepts that reference.
It remounts the component when the logical View reference changes, and creates
it outside its own signal tracking. A component's `page` input receives that
reference; `injectView(this.page)` owns its connection and cleanup. Declare
`page` as a required signal input, `input.required<DocumentReference>()`: a
decorator `@Input() page` or an optional `input<DocumentReference>()` does not
satisfy `ViewRegistry`. Missing kinds render an English `<p role="alert">`. The
generated TypeScript client can also be used directly without Angular.

The `missing` input replaces that alert with a template, for example with
localized text. Its context (`ViewOutletMissingContext`) passes the reference
whose kind has no component as `$implicit` and as `content`, and its markup
(including any `role`) is yours. The outlet then leaves its `error` signal unset:

```html
<runic-view-outlet [content]="current()" [registry]="registry" [missing]="missingView" />
<ng-template #missingView let-page><p role="alert">{{ missingText(page.kind) }}</p></ng-template>
```

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
`pending`, `admitting`, `operation`, `status`, `progress`, `outcome`, `failure`, `error`,
`cancelled`, `cancellationRequested`, `cancelling`, `cancellation` and `cancelError`
signals. `progress` is how far the running operation got, from 0 to 1, when its
command reports progress (`RunicOperationProgress.Report` in .NET), for example
`<progress [value]="push.progress()">`; it is `undefined` while unknown and after the run. A run that ends with a `cancelled` status sets `cancelled` and resolves to
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

## Connection state

`injectBridgeConnection(options?)` follows the page's connection to its .NET
host for the lifetime of the injection context. It wraps `onBridgeConnection`
from `@runic-artifex/views` (re-exported here since 0.11, with the
`BridgeConnectionState` and `BridgeConnectionOptions` types) and returns `{ state }`, a signal of a
`BridgeConnectionState`: `connecting`, `connected`, `reconnecting` or `lost` (a
closed set, see
[Connection state](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#connection-state)):

```ts
import { injectBridgeConnection } from "@runic-artifex/angular";

readonly connection = injectBridgeConnection({ lostAfter: 5_000 });
// @if (connection.state() === "lost") { <p role="alert">The application stopped responding.</p> }
```

Destroying the injection context unsubscribes. Pass `injector` outside an
injection context. Under server rendering (`PLATFORM_ID` is `"server"`) it does
not observe and stays `connecting`. An invalid `lostAfter` throws a `RangeError`.

## In-page menu bar

`injectMenuBar(options?)` follows the declared menu bar of a Runic Desktop
window that leaves it to the page (`DesktopMenuBarPresentation.Page`) for the
lifetime of the injection context. It wraps `createMenuBarController` from
`@runic-artifex/views` (see [In-page menu bar](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#in-page-menu-bar))
and returns `{ state, invoke, handleKeyDown }`, where `state` is a signal of a
`MenuBarState`. The bar is rendered by the application's own menubar, for
example the CDK Menu's `cdkMenuBar`. Runic ships no menubar component.

```ts
import { injectMenuBar } from "@runic-artifex/angular";

readonly menuBar = injectMenuBar({
  onRequest: request => request.kind === "open" ? this.openMenu(request.menu.key) : this.focusFirstItem(),
});
// On macOS the application menu bar keeps the menus ("global"): render nothing.
// @if (menuBar.state().placement === "page") { <div cdkMenuBar>…(cdkMenuItemTriggered)="menuBar.invoke(item)"…</div> }
```

Options are those of `createMenuBarController` (`shortcuts`, `keyboard`)
without `signal`, plus `onRequest`, which receives the `focus` and `open`
requests, and `injector`, required outside an injection context. Destroying the
injection context disposes the controller, which also returns the
`shortcuts: "app"` lease. Under server rendering (`PLATFORM_ID` is `"server"`)
it creates none and stays `connecting`.

### With the CDK Menu

The CDK's `cdkMenuBar` with `CdkMenuTrigger.open()` for `open` requests; the
[Notes example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first#in-page-menu-bar) adds submenus, check and radio
items (`cdkMenuItemCheckbox`, `cdkMenuItemRadio` in a `cdkMenuGroup`) and is
tested with real keys:

```ts
@Component({
  selector: "app-menu-bar",
  imports: [CdkMenuBar, CdkMenu, CdkMenuItem, CdkMenuTrigger],
  template: `
    @if (menuBar.state().placement === "page") {
      <div cdkMenuBar aria-label="Application">
        @for (menu of menuBar.state().menus; track menu.key) {
          <button cdkMenuItem [cdkMenuTriggerFor]="popup" [cdkMenuTriggerData]="{ $implicit: menu.key }"
            [cdkMenuItemDisabled]="!menu.enabled">{{ menu.label }}</button>
        }
      </div>
    }
    <!-- The CDK keeps a trigger's first data, so the popup reads the current entries by key. -->
    <ng-template #popup let-key>
      <div cdkMenu>
        @for (entry of entriesOf(key); track entry.key) {
          @if (entry.kind === "separator") { <div role="separator"></div> }
          @else if (entry.kind === "item") {
            <button cdkMenuItem [cdkMenuItemDisabled]="!entry.enabled" [attr.aria-keyshortcuts]="entry.ariaKeyShortcuts ?? null"
              (cdkMenuItemTriggered)="menuBar.invoke(entry)">{{ entry.label }} <kbd aria-hidden="true">{{ entry.shortcutText }}</kbd></button>
          }
        }
      </div>
    </ng-template>
  `,
})
export class AppMenuBar {
  private readonly bar = viewChild(CdkMenuBar);
  private readonly items = viewChildren(CdkMenuItem);
  readonly menuBar = injectMenuBar({
    onRequest: request => {
      const bar = this.bar();
      if (request.kind === "focus") bar?.focusFirstItem("keyboard");
      else if (request.kind === "open") {
        const index = this.menuBar.state().menus.indexOf(request.menu);
        const item = this.items()[index];
        if (bar && item) { bar.setActiveMenuItem(item); item.getMenuTrigger()?.open(); }
      }
    },
  });
  entriesOf(key: string) { return this.menuBar.state().menus.find(menu => menu.key === key)?.entries ?? []; }
}
```

## Errors

The `error()` signal of `injectView` and of a command or operation is typed
`unknown`: it holds whatever the connection or the call rejected with. A Bridge
failure is a `BridgeError` from `@runic-artifex/views`. Show it with
`describeBridgeError(error)`, one English sentence, instead of interpolating
the error, and narrow it with `isBridgeError` (or `instanceof`) before reading
`kind`, `route` or `detail`:

```ts
import { describeBridgeError, isBridgeError } from "@runic-artifex/views";

readonly offline = computed(() => isBridgeError(this.view.error(), "disconnected"));
readonly message = computed(() => describeBridgeError(this.view.error()));
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
