# `@runic-artifex/views`

Shared browser runtime for the TypeScript clients that Runic Views generates
from .NET ViewModels. Generated modules import it; applications use it for
error handling and for development without a .NET host.

```sh
npm install @runic-artifex/views@preview
pnpm add @runic-artifex/views@preview
bun add @runic-artifex/views@preview
```

Previews are published under the `preview` dist-tag. Install the version that
matches your Runic SDK packages.

## Generated clients

Each generated module exports `connect<Name>()` for a root ViewModel and
`page<Kind>(id)` references for presented content, each a `PageReference` (the
counterpart of .NET `PageReference`, with the View `kind` and `connect()`). A connected `<Name>Client`
has a `snapshot`, `subscribe(listener)`, `dispose()` and one method per setter,
command and operation. As in any MVVM View, only a property with a public C#
setter gets a `set<Property>` method; a `private set` keeps it read-only state:

```ts
import { BridgeError } from "@runic-artifex/views";
import { connectCounter } from "./generated/counter.js";

const counter = await connectCounter();
const stop = counter.subscribe(state => render(state.count));
try {
  await counter.increment();
} catch (error) {
  if (error instanceof BridgeError && error.kind === "disconnected") showOffline();
  // `released`: navigation replaced this View; the new one arrives with the next state.
}
stop();
counter.dispose();
```

A command whose ViewModel runs it as an operation also gets
`start<Command>(input?, options?)`, which resolves to a `BridgeOperation` handle
once .NET admitted it, and `recover<Command>(requestId)`. `options.requestId`
(`BridgeOperationStartOptions`) names the operation so the page can recover it
after a reload; the default is a random UUID, and starting again with an admitted
request id returns that operation. A handle's statuses carry `delivery`, a
`BridgeOperationDeliveryFailure` (.NET `BridgeOperationDeliveryFailure`), when a
completed result could not be delivered; a stream page (`BridgeOperationStreamPage`,
.NET `BridgeOperationStreamRead`) has `cursor`, the cursor of the next page.

`subscribe` delivers the current state first. After `dispose()` the client
keeps its last `snapshot`, and `subscribe` delivers that state once and returns
a no-op, so framework stores can read a client during teardown. Calls reject
with a `BridgeError` whose `kind` is `rejected`, `cancelled`, `failed`,
`disconnected`, `released`, `timeout` or `unavailable`; see
[Errors and diagnostics](#errors-and-diagnostics).

### Notifications

The client applies each frame as soon as it arrives, but delivers the resulting
state to subscribers once per animation frame, so a burst of frames renders once
with the newest state. While the page is hidden, when the browser runs no
animation frames, a short timer delivers it instead. `snapshot` is always the
state subscribers last received. Nothing that a caller awaits waits for a frame:
a setter shows its value at once (so a controlled input keeps its caret), and a
call that resolves, such as `await counter.increment()` or a checked write,
first delivers every state that arrived before its reply. The mock Bridge
delivers each push at once (see [Testing](#testing)).

### Cancellation and `using`

`connect<Name>({ signal })`, `waitForBridge({ signal })` and an operation's
`wait({ signal })` and `outcome({ signal })` take an `AbortSignal`. When it
aborts first, the call rejects with `signal.reason`: an `AbortError`
`DOMException`, unless `abort(reason)` gave another value. An aborted signal
rejects at once and sends no request. An aborted connect releases what it had
connected, including the View's presentation (`open`), and reports no diagnostic. Aborting after
the client was returned has no effect, and every call removes its abort listener
when it settles. Aborting an operation wait abandons only that wait: unlike
`timeout`, it sends no cancellation request, and the operation and other waits
continue; call `cancel()` to stop it.

```ts
const controller = new AbortController();
const editor = await connectEditor({ signal: controller.signal });
const save = await editor.startSave();
const status = await save.wait({ signal: controller.signal });
```

A generated `connect<Name>()` resolves to the client with `[Symbol.dispose]()`,
and `createViewController`, `createCommandController`,
`createOperationController`, `createLatestOperationController` and
`createCollectionViewportController` return controllers with it, so a `using`
declaration disposes them at the end of its block. `[Symbol.dispose]()` calls
`dispose()`:

```ts
{
  using counter = await connectCounter();
  await counter.increment();
} // counter.dispose()
```

The member exists where the engine defines `Symbol.dispose`, or a polyfill did
before the client or controller was created; it is looked up then, not when the
package loads. On engines without it, such as older WebKitGTK and WKWebView, the
package loads unchanged and `dispose()` is the way to release. The interfaces
(`ViewClient`, `<Name>Client`, `ViewController` and the other controllers)
are unchanged, so objects that implement them need no new member. The package's
declarations reference TypeScript's `esnext.disposable` library, so `using`
type-checks with `lib: ["ES2022", "DOM"]` (TypeScript 5.2 or later); running
`using` still needs an engine or polyfill with `Symbol.dispose`.

All generated modules on a page share one runtime instance, even when several
bundles or copies of this package load: it is the Bridge's only push listener, a
route has one revision, and `instanceof BridgeError` holds for errors from any copy.

The framework packages `@runic-artifex/react`, `@runic-artifex/vue`,
`@runic-artifex/svelte` and `@runic-artifex/angular` connect and dispose
clients with the component lifecycle. They are thin bindings over the
controllers in [Framework bindings](#framework-bindings).

### Package entries

| Entry | For |
| --- | --- |
| `@runic-artifex/views` | Applications: errors, diagnostics, controllers and client types |
| `@runic-artifex/views/mock` | Development and tests without .NET: `createMockBridge`, `installMockBridge`, `installMockMenuBar`, `mockFailure` and the `Mock*` types of generated mocks |
| `@runic-artifex/views/bindings` | Framework bindings such as `@runic-artifex/react`: `viewSourceIdentity`, `isViewClient` |
| `@runic-artifex/views/generated` | Generated modules: `connectView`, `viewReferences`, `disposable`, `defineCollection(s)`, `defineInteractions`, `bridgeOperations`, `decodeBridgeValidation` |
| `@runic-artifex/views/generated/wire` | Generated modules and generated `*.mock.ts` files: the wire decoders, imported as `import * as bridgeWire` |
| `@runic-artifex/views/generated/mock` | Generated `*.mock.ts` files: `mockTypedView` and its spec types |

Every entry has `types`, `import` and `default` conditions; the package is ESM
only. Application code imports the root entry and `/mock`. The `/experimental`
entry of 0.9 was removed in 0.10: its APIs graduated to the root entry. The
`/bindings` and `/generated*` entries are versioned with the generator and the
.NET packages: code that a release generated keeps working with the runtime of
any later release of the same major, but their shape is not for hand-written
code. A framework binding depends on the exact same release. Use the .NET and
npm packages of one release together: a .NET host of another release is
reported as a diagnostic with code `version`, and a client does not connect to a
host of another Views protocol version (see
[Errors and diagnostics](#errors-and-diagnostics)). A generated module passes the interaction, operation and keyed
collection runtimes to `connectView` only when its ViewModel has them, and
imports the decoders as a namespace, so a bundler leaves out the protocol code
and decoders a View does not use. `bun run size` in this package prints the
minified and gzip size of the checked-in example clients.

## Incremental collections

A .NET ViewModel can mark a read-only collection of DTO rows with
`[RunicCollection(nameof(Row.Id))]`. Its generated client then receives indexed
add, remove, replace and move changes in frames instead of the whole field, applies them on
top of its current revision, and reads a fresh snapshot if a frame is missing
or invalid, retrying a failed read and keeping frames that arrive meanwhile.
Rows that a frame does not touch keep their object identity, so frameworks can
skip rendering them. Clients still see a complete array in `snapshot` and
`subscribe`. If the .NET collection has a null row or a null, empty or
duplicate key, the route keeps its last state and reports the failure through
`onBridgeDiagnostic` until the keys are valid again.

### `defineCollection(decode, key)`

```ts
function defineCollection<T>(decode: (wire: unknown) => T, key: (item: T) => string): BridgeCollectionDefinition;
```

Exported from `@runic-artifex/views/generated`. Describes one collection field
for the runtime: `decode` validates and converts a
wire row (throwing for an invalid one), and `key` returns the row's key, which
must be a nonempty string unique within the field. Generated modules call it for
each `[RunicCollection]` field, deriving `key` from the attributed property, and
pass the fields to `connectView` as `collections: defineCollections({ ... })`.
Application code does not call it. A hand-written or test client that connects
a collection route itself passes `defineCollections({ rows: defineCollection(...) })`
in the same way, and its key must match the .NET wire key: the string itself,
a lowercase GUID, or an `Int32` in decimal.
### `defineText(nullable?)`

```ts
function defineText(nullable?: boolean): BridgeCollectionDefinition;
```

Exported from `@runic-artifex/views/generated`. Describes a C#
`[RunicAppendOnlyText]` string field, such as a live log: its frames drop
characters from the start of the text the client has and append text, so a
long log does not cross whole on every change. Generated modules pass it with
the collections, as `defineCollections({ log: defineText() })`; `nullable`
accepts `null` for a `string?`. A change whose length does not match the
client's text is unusable, and the client reads a fresh snapshot.

### `collectionViewport(options)`

```ts
function collectionViewport(options: {
  totalCount: number; scrollTop: number; height: number; rowHeight: number; overscan?: number;
}): { start: number; size: number; offset: number; totalSize: number };
```

Computes which rows of a fixed-row-height list to request from .NET, for any web
framework. Pass the total row count published by the ViewModel, the scroll
container's `scrollTop` and visible `height`, the row height in pixels and an
optional `overscan` (default 5 rows before and after the visible range). It
returns the first row to request (`start`) and how many (`size`), the pixel
`offset` of the first returned row, and the `totalSize` of the scroll spacer.
Negative `scrollTop` is treated as 0 and the range is clamped to `totalCount`.
Invalid input (a negative or non-integer count or overscan, a non-finite value, a
negative height or a row height that is not positive) throws a `RangeError`.

The ViewModel owns the windowed collection; the page sends the requested range
through a command and renders the rows it receives:

```ts
import { collectionViewport } from "@runic-artifex/views";
import { connectRows } from "./generated/rows.js";

const view = await connectRows();
function requestViewport(scroll: HTMLElement) {
  const { start, size } = collectionViewport({
    totalCount: view.snapshot.totalCount, scrollTop: scroll.scrollTop,
    height: scroll.clientHeight, rowHeight: 32,
  });
  if (size !== 0) void view.setViewport({ start, size });
}
```

Avoid sending a request when `start` and `size` have not changed. The framework
packages wrap this in `useCollectionViewport` (`injectCollectionViewport` in
Angular), which follows the container's scroll position and size. The
[DynamicData example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/dynamicdata) shows a complete page. The frame
format, fallbacks and recovery rules are specified in
[State changes](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/application/state-changes.md).

## Framework bindings

The framework packages share framework-neutral controllers, so each
binding keeps only its own reactivity glue. Applications normally use the
bindings; a binding for another framework can use the controllers directly.
Each controller has a `current` value, replaced by a new object on every
change, and `subscribe(listener)`, which returns an unsubscribe function.

- `createViewController<TClient>({ release?, keepAlive? })` follows a `ViewSource`: a
  connector (`{ connect() }`, such as a generated page reference) that it
  connects and releases, or a connected client that it only observes.
  `setSource(source)` ignores a source with the same client or `connect`
  function and does not notify; read `current` after calling it. `current` has
  `source`, `client`, `state`, `error` and `pending`. `retry()` connects
  again, `release()` lets go of the client but keeps `current` (a binding's
  unmount or hidden phase), and `dispose()` lets go of it for good. `isViewClient`,
  `viewSourceIdentity` and `viewSourcePreview` from `@runic-artifex/views/bindings`
  tell clients from connectors, compare sources and read what a source shows
  before a controller follows it (see [Client lifetime](#client-lifetime)).
- `createCommandController(command)` has `run(...args)`, which never rejects
  and resolves to `undefined` after a failure, and `current` with `pending`,
  `error` and `failure`. For a command that resolves a `BridgeOutcome`,
  `failure` is the declared failure of the latest run; `error` stays the
  unexpected failure. Starting a run and `reset()` clear both, and a run
  superseded by a later run or by `reset()` sets neither.
- `createCollectionViewportController({ totalCount, rowHeight, overscan? })` has
  `attach(element)`, which follows the element's scroll position (once per
  animation frame) and size, and `update(options)`. `current` is the
  `collectionViewport` range and changes only when the range or sizes change.
- `createOperationController(start, options?)` observes generated `start<Command>()`
  handles through their public `wait()` and `outcome()`. Its state covers admission,
  pending work, terminal status/outcome, declared failure, unexpected error,
  cancellation feedback and a separate `cancelled` end state. `run(...args)` and
  `cancel()` never reject. React and Vue `useOperation`, Svelte `useOperation` and
  Angular `injectOperation` expose the same state. `reset()` clears
  feedback while retaining pending work and its cancellation identity; `dispose()`
  detaches feedback and settles UI observation promises without cancelling work.
- `createLatestOperationController<TResult, TFailure>()` serializes superseding read
  intent. React, Vue and Svelte `useLatestOperation` and Angular `injectLatestOperation`
  wrap it. See [Operation feedback and latest selection](#operation-feedback-and-latest-selection).

## Client lifetime

A connected client is a presentation of its route in .NET: connecting opens the route, and disposing the last
client of a route closes it. Components unmount and mount again more often than a user navigates: StrictMode's
simulated unmount, Fast Refresh and HMR, React's `<Activity>`, an `{#if}` or `v-if` that toggles, and A→B→A
navigation. So a binding does not dispose its client at once: the view controller keeps it connected for a grace
period, `keepAlive` (2000 ms by default), keyed by the connector's `connect` function, and the next binding that
follows the same connector takes it over with its state. The component shows its View again at once, without
"Connecting…", and the route is not opened again. A client that nothing takes over is disposed when the period ends.

- Kept clients are handed over, not shared: two components that show the same reference at the same time still
  have a client each.
- `keepAlive: 0` (`useView(page, { keepAlive: 0 })`, `injectView(page, { keepAlive: 0 })`) disposes at once, as
  before 0.14, for a test that checks that a route closes on unmount, or a View whose presentation must end with
  its component.
- While the same source connects again (after `retry()`, or once its grace period ended while a component was
  hidden), `state` stays the last state the controller showed, with `pending: true`; a different source shows
  nothing of the previous one.
- An observed client (`useView(client)`) is never kept or disposed by a binding; its owner disposes it.

`preloadView(source, { keepAlive? })` connects ahead of the component, as in a router's `load` function: it resolves
with `source` once the client connected (and rejects with the failure, so the router shows its error page), and the
first binding that follows `source` takes the client over:

```ts
// SvelteKit, src/routes/editor/+page.ts
import { preloadView } from "@runic-artifex/views";
import { connectEditor } from "$lib/generated/editor.js";

export const ssr = false;
export const load = async () => ({ editor: await preloadView({ connect: connectEditor }) });
```

## Text inputs

A text input that writes on every key sends many requests while the user types,
and their replies and frames arrive while newer keys are already in the field.
How a field stays correct depends on what it calls:

- **A setter** (`set<Property>(value)`) numbers each edit. Until the route reached
  the state that includes the latest edit, `snapshot` and `subscribe` show that
  edit's value, so an input bound to state (`value={state.name}` and
  `onInput={event => client.setName(event.currentTarget.value)}`) never shows an
  older value over newer keys. A failed setter rejects and the member shows the
  View's value again. An edit that reaches .NET after a newer edit of the same
  member is skipped (`superseded`), and its call still resolves. Because
  `snapshot` shows a pending value, await the setter's promise when the page needs
  to know that .NET accepted it, for example for a "Saved" indicator.
- **A command** called per key, such as `client.rename(value)`, has no such
  overlay: each reply's state still holds the text of its own request, so an input
  bound to state loses keys typed meanwhile. Bind text to a setter, or use the
  text field binding below with the command as `write`.

### Fields

The generator emits a descriptor for each settable member of a View, `{View}Fields`:

```ts
import { EditorFields } from "./generated/editor.js";
EditorFields.title; // { name: "title", kind: "text", read, write, checked, baseline, errors? }
EditorFields.step;  // { name: "step", kind: "number", integer: true, … }
```

`kind` is `text` (any TypeScript `string`: strings, `Guid`, dates, decimals), `number` (`integer` for integer
types), `bigint`, `boolean`, `choice` (an enum; `choices` lists its names) or `value` (objects, lists, unions), and
`nullable` marks nullable members. `read` and `write` are the member and its setter; `checked` and `baseline` its
checked write and `fieldBaseline`. A View with validation adds `errors(state)`: the member's messages from
`{name}Errors`, or from `validation` at the member's path, followed by the messages below it, such as those of an
object's members.

`createFieldController(field, options)` binds one input to a member, for any kind. Angular `injectField` and React,
Vue and Svelte `useField` wrap it with each framework's wiring; prefer those. Typing key by key never loses
characters: while its writes are in flight the field shows the user's draft, at most one write is in flight (text
typed meanwhile is written next), nothing is written during IME composition, and once the last write settled it
shows the View's value with any normalization.

```ts
const title = createFieldController(EditorFields.title, { commit: "pause" });
const stopState = editor.subscribe(state => { title.setSource({ state, client: editor }); render(title.current); });
const stopField = title.subscribe(() => render(title.current));
input.addEventListener("input", () => title.inputText(input.value));
input.addEventListener("compositionstart", () => title.compositionStart());
input.addEventListener("compositionend", () => title.compositionEnd(input.value));
input.addEventListener("blur", () => title.commit());
```

Without a framework, `bindField(element, client, field, options?)` does that wiring for an `<input>`, `<textarea>` or
`<select>` (a checkbox for a boolean field): it creates the controller, follows the client's state, writes on `input`
(`change` for a `<select>` or checkbox), nothing during IME composition, commits on blur and sets `aria-invalid`. It
returns the controller; `dispose()` unwires it. `attachField(element, controller)` wires an element to a controller
that the page feeds itself, and `commitFields(...fields)` commits fields and rejects with the first failed field's
error, so Save can follow:

```ts
const title = bindField(document.querySelector("#title")!, editor, EditorFields.title, { commit: "pause" });
const body = bindField(document.querySelector("#body")!, editor, EditorFields.body, { commit: "pause" });
save.onclick = async () => { await commitFields(title, body); await editor.save(); };
```

`current` has:

- `value`: the draft while one exists, otherwise the member's value in the View's state;
- `text`: what a text input shows: the text typed while it does not parse or still means `value` (`1.` while typing
  `1.5` into a number field), otherwise `value` formatted;
- `editing`: a draft exists. Keep the input enabled while it is true;
- `error`: why the latest write or text failed: a `BridgeError`, or a `FieldError` (below);
- `errors`: the member's validation messages (`[]` when valid);
- `conflict`: the producer's `{value, version}` while a checked write's conflict keeps the draft.

`input(value)` takes a value, `inputText(text)` the text of an input, parsed for the field's kind: `-` or `1e` in a
number field stay the draft with a `FieldError` of kind `invalid-text`, and nothing is written until the text parses.
An empty text is `null` for a nullable number, choice or boolean.

Options:

- `commit: "input"` (the default) writes each input, coalesced (`coalesce: false` writes every one, in order by
  their edit numbers). `commit: "pause"` writes the draft after `delay` milliseconds without input (700 by default)
  and at once on `commit()`, which the framework bindings call when the element loses focus; text that was not
  written yet is written when the field is disposed. Use it where each write is an undo step or starts work in .NET.
  `commit()` resolves with the field's state once its writes settled, so a command that depends on the field can
  follow them and skip a failed write: `if ((await title.commit()).error === undefined) await editor.save()`.
- `checked: true` writes with the member's checked write instead of its setter. The write is checked against the
  value and version the field showed when the draft started and then against each receipt, so a change by another
  writer meanwhile is a conflict instead of being overwritten. Checked writes are one at a time.
- `keepDraftOnError: false` shows the View's value after a failed, rejected or conflicting write instead of the
  draft.

Receipts of checked writes, also when `write` of a text field calls one:

| Receipt | Field |
| --- | --- |
| `applied` | Success. The receipt's `validation`, if any, is `errors` when the descriptor has no `errors`. |
| `rejected` | The draft stays; `error` is a `FieldError` `rejected` with the setter's message. The next input writes again. |
| `conflict` | The draft stays; `conflict` and `error.current` are the producer's field, `error` is a `FieldError` `conflict`. Nothing is written until `retry()` writes the draft over the producer's value or `reset()` takes it. A conflict whose incoming value is the value sent is a success. |
| `committed-with-error` | The value was committed and the draft is dropped; `error` is a `FieldError` `committed-with-error`. |

`createTextFieldController({ read, write, … })` is the string case for a hand-written `read` and `write`, such as a
command; `injectTextField` and `useTextField` wrap it.

### Long text: splices

A setter of a `string` member does not send the whole value on every key when the value is long. With the
`text-splice/1` feature, which `@runic-artifex/views` and `Runic.Application.Views` negotiate in `hello`, it sends
only what changed: the position, the number of characters it removed and the text it inserted, against the value the
page showed (the previous edit while it is on its way, or the View's state). .NET rebuilds the value and calls the C#
setter with it, exactly as if the page had sent the whole value. Frames change long `string` members the same way. A
keystroke in a 100,000-character editor sends about 130 bytes and receives about 160, instead of about 100 KB each
way.

Nothing changes in code: setters, checked writes, the field controllers and the framework field bindings send splices on
their own, and `snapshot` holds whole strings. A splice that keeps fewer than 64 characters of the old value, a change
from or to `null`, and any value when the host lists no features cross whole. When .NET no longer has the value a
splice was based on, such as after a reconnect or when another window changed the member, the setter sends the value
again in full; the call settles once. Positions count UTF-16 code units, as JavaScript strings do, and never split a
surrogate pair. A checked write (`write`) of a long text member sends a splice of `expectedValue` the same way, and
sends the write in full with the same `requestId` when .NET answers that the splice does not fit.

### Normalizing and busy state

- **Normalize on commit, not per key.** A setter that trims, reformats or rejects
  partial input on every key fights the user: the binding shows its result once
  the writes settled, which can drop a space the user just typed. Keep the per-key
  setter lossless and normalize when the user commits, in a command run on `blur`,
  `change` or Enter, or in the command that saves.
- **Don't disable a field on its own busy state.** A field's write makes
  `is<Command>Executing`, `can<Command>` or an application `IsBusy` change, and a
  field disabled for it loses focus and the keys typed meanwhile. Keep the input
  enabled while `editing` and disable the buttons that start other work instead.

During `vite dev`, `@runic-artifex/vite-plugin-runic` warns once per input
(diagnostic code `stale-input`) when script sets a focused input to a value it
had earlier in the current typing burst while a Bridge request is in flight: the
sign of a command per key or of an application-side copy of the state. See
[the plugin guide](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/vite-plugin-runic)
to turn it off. To test a field, type into it key by key against a slow mock
([Typing in tests](#typing-in-tests)).

## Operation feedback and latest selection

An operation Start receipt proves admission; it does not prove completion. A Cancel
response proves only that the cancellation request was observed. Keep the UI pending
until the public handle's terminal `wait()` completes. `wait()` follows the operation
through the producer's `watch` pushes, so polling its status is unnecessary.
A command that reports progress (`RunicOperationProgress.Report` in .NET) is followed
through `operation.subscribe`, and the controller exposes it as `progress`. Render
other domain state and recovery from the generated View snapshot.

```ts
import { createOperationController } from "@runic-artifex/views";

const push = createOperationController((remote: string) => workspace.startPush(remote));
const stop = push.subscribe(() => render(push.current));
void push.run("origin");
await push.cancel(); // pending stays true until terminal observation
// When the UI is released:
stop();
push.dispose();
```

`current` has `pending`, `admitting`, `operation`, `status`, `progress`, `outcome`, `failure`,
`error`, `cancelled`, `cancellationRequested`, `cancelling`, `cancellation` and
`cancelError`. `progress` is the latest run's progress from 0 to 1 while its operation
runs, and `undefined` while unknown and once the run ended. `pending` spans Start admission, terminal wait, any completion barrier
and outcome delivery. A declared failure is an outcome and `failure`; an unexpected
failure, including a `failed` or `timed-out` status, is `error`. A `cancelled` status,
whether this UI or .NET requested it, is a normal end: `cancelled` becomes true,
`run()` resolves to `undefined`, and `error`, `outcome` and `failure` stay undefined.
Only a direct `operation.outcome()` call still rejects with `BridgeError("cancelled")`.
Starting a run and `reset()` clear `cancelled`. Cancel failures are `cancelError` and do not release pending work. A Cancel
requested before a delayed Start receipt is sent after that captured run is admitted.
An absent client may return `undefined` from `start`, ending the run without error.
Overlapping runs are observed independently, and only the latest publishes feedback
or receives Cancel. Disable mutation buttons while pending to prevent accidental
duplicate work; a later run never automatically cancels an earlier mutation.

Cancellation of a generated invocation wrapper can finish before accepted model
work or recovery has drained. The controller cannot establish that application
boundary on its own. `options.cancel(operation)` may call a short model control
command instead of `operation.cancel()`; it may return void or a typed cancellation
receipt. `options.waitForCompletion(operation, terminalStatus)` can await the
application's accepted-work drain or command availability after the terminal wait.
The same typed callbacks are available per latest-selection intent. They must capture
the original client/session, and must reject when they cannot establish completion.
If a custom cancellation callback awaits before sending a global control, it must
recheck that captured session immediately before the side effect; replacement cannot
revoke a callback already entered.
Host shutdown still owns and awaits accepted work; UI disposal is not a drain helper.

Read selection often needs a different policy from mutation buttons: remember the
newest click, cancel the previous read, and await terminal admission before starting
the newest one. A queue can cover several generated read commands. In a component, use
the adapter's `useLatestOperation()` (React, Vue, Svelte) or `injectLatestOperation()`
(Angular), which disposes the controller with its owner:

```ts
import { createLatestOperationController } from "@runic-artifex/views";

const selections = createLatestOperationController<void>();
function selectCommit(oid: string) {
  const client = currentClient;
  const session = currentSession;
  if (!client || !session) return;
  return selections.run({
    start: () => client.startSelectCommit(oid),
    cancel: () => client.cancelRead(),
    isCurrent: () => currentClient === client && currentSession === session,
  });
}
// Explicit Cancel drops queued input and requests cancellation of the current read.
selections.clear();
// After the application establishes replacement admission/session and owns old drain:
selections.clear("replace");
```

Only the newest queued intent survives. Supersession before the Start receipt retains
cancellation until admission; the next Start waits for the previous terminal wait
and optional application completion barrier. A failed Cancel still waits for that
boundary. Any pending cancellation callback also settles before the next Start,
so a delayed global control cannot land on the successor. `isCurrent` is checked before admission, cancellation and publication,
so departed sessions cannot start queued reads or issue stale global controls.
`clear("replace")` immediately detaches the old observation, settles its caller and
suppresses delayed cancellation, allowing fresh intent to start without waiting for
an old connection. Use it only after the application establishes fresh admission;
it does not cancel or drain the old work. `dispose()` likewise settles active/queued
UI callers and consumes late errors without cancelling accepted work.

`current.blocked` signals that admission or completion is uncertain, with the reason
in `current.error`. A failed Start whose admission is not explicitly `rejected`, a
lost terminal observation, `unknown`, `expired`, `timed-out`, or a failed completion
barrier discards queued input and blocks new Starts. Do not retry
blindly. An explicit `clear("replace")` clears the block only because the application
has established a new boundary; ordinary Cancel does not. Both controllers consume
late promise rejections after detachment and isolate subscriber exceptions through
bridge diagnostics.

## Keyboard shortcuts

`matchesShortcut(event, "Primary+S")` tells whether a `keydown` event is a
shortcut, and `formatShortcut("Primary+S")` shows it as the platform does:
`⌘S` on Apple platforms, `Ctrl+S` elsewhere. `Primary` is the Command key on
Apple platforms and Ctrl elsewhere, `Alt` is Option, and `Control` is the
Control key on Apple platforms and the same Ctrl elsewhere; matching requires
exactly the shortcut's modifiers and ignores keys an input method is composing.
The text is the same as Runic.Platform's `DesktopShortcut`, which Runic
Desktop's macOS menu items use, and both are checked against the cases in
[specs/desktop/fixtures/shortcuts.json](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/desktop/fixtures/shortcuts.json).
Pass `{ apple: true }` or `{ apple: false }` to choose the platform rule
instead of detecting it from `navigator`. See the
[shortcut guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#keyboard-shortcuts-in-the-page)
for which keys to leave to text fields and the web view.

`shortcutFromEvent(event)` records a key press as that text, such as
`Primary+Shift+S`, for a settings page where the user chooses a key: send it
through an ordinary command, keep it in the application's settings and pass it
to `DesktopShortcut.Parse`, for example for a global shortcut
(`IDesktopGlobalShortcuts`). It gives `null` for a modifier pressed alone, a dead
key, input method composition and, outside Apple platforms, the Windows or Super
key. A punctuation character is recorded as typed, without Shift, and a digit as
its key on the top row or the keypad.

```ts
input.addEventListener("keydown", (event) => {
  const shortcut = shortcutFromEvent(event);
  if (shortcut === null) return;
  event.preventDefault();
  void settings.setQuickNoteShortcut(shortcut);
});
```

## In-page menu bar

A Runic Desktop window with `MenuBarPresentation = DesktopMenuBarPresentation.Page`
leaves its menu bar to the page, so the bar can match the application's look; a
CS-WebUI window, which has no native bar, always does with
`RunicCsWebUiHostOptions.MenuBar`. `createMenuBarController()` follows the
window's declared bar (`DesktopHost.MenuBar` or `RunicCsWebUiHost.MenuBar`)
as headless state; the page renders it with the menubar component of its own UI
library (Base UI, Radix, Reka UI, Bits UI, Angular CDK Menu or its own markup).
Runic ships no menubar component. The framework adapters wrap the controller as
reactive state: `useMenuBar` in `@runic-artifex/react`, `@runic-artifex/vue` and
`@runic-artifex/svelte`, and `injectMenuBar` in `@runic-artifex/angular`.

| Runic provides | The application provides |
| --- | --- |
| `current`: the menus in the platform's order, with keys, labels and access keys, separators, submenus, checked and radio state, `enabled` (the declaration and the command's `CanExecute`), roles and shortcuts (`shortcut`, `shortcutText`, `ariaKeyShortcuts`); `subscribe` for changes | The menubar component, markup, styling, icons and animation |
| `invoke(item)`: the native bar's command path, with the window's and then the application's commands | Calling `invoke` from the component's select or click handler |
| The items' shortcuts, matched by the host or by the controller; `shortcuts: "app"` and `handleKeyDown` for a page that matches them itself | Nothing by default |
| `onRequest`: `focus` for an Alt tap and F10, `open` for Alt plus a menu's access key (Windows and Linux); `accessKeyHints` while Alt is held; `placement` | Focusing the first menu or opening one; underlining the `splitAccessKey(entry)` parts while `accessKeyHints` is true |
| `ariaKeyShortcuts` and platform labels | The WAI-ARIA menubar roles, roving focus, arrow keys, typeahead and Escape |

```ts
import { createMenuBarController, splitAccessKey } from "@runic-artifex/views";

const bar = createMenuBarController();
bar.onRequest(request => request.kind === "open" ? openMenu(request.menu.key) : focusFirstMenu());
bar.subscribe(() => {
  const { placement, menus, accessKeyHints } = bar.current;
  // Render a bar only while placement is "page": on macOS the application menu bar keeps the menus ("global").
  render(placement === "page" ? menus : [], accessKeyHints);
});
// In the component's select handler:
await bar.invoke(item); // "executed", "disabled" or "stale"
```

- `status` is `connecting`, then `ready`; `unavailable` on a host that does not
  serve the in-page menu bar (the reserved route `runic.menu-bar`), and `failed`
  with `error` otherwise. `placement` is `page`, `window`, `global` or `none`.
- By default Runic keeps the items' shortcuts, so they work whichever component
  renders the bar: the host matches them after the page saw the key
  (`shortcuts` `host`), or, where the host has no key path such as an installed
  browser or CS-WebUI, the controller matches them in a bubbling `keydown` listener
  (`shortcuts` `page`). Either way a page that keeps a key calls
  `preventDefault()`. With `createMenuBarController({ shortcuts: "app" })` the
  page matches them itself, for example with `bar.handleKeyDown(event)`, and the
  host stops matching them for this page while the bar is in the page.
- `keyboard: false` turns off the Alt and F10 requests and `accessKeyHints`.
  macOS has neither.
- `dispose()`, or the `signal` option, stops the controller and returns the
  `"app"` lease. The controller follows the window across reconnects.

`installMockMenuBar(menus, options)` from `@runic-artifex/views/mock` serves
`runic.menu-bar` on the mock Bridge for tests and development without .NET; its
`update` changes the bar and `invocations` lists the invoked items. The wire is
specified in
[specs/desktop/menu-bar-page.md](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/desktop/menu-bar-page.md).

## Developing without .NET

`@runic-artifex/views/mock` provides an in-memory Bridge, a Views protocol 4
producer. Install it before the first `connect<Name>()` call, for example in a
Vite `mock` mode:

```ts
import { installMockBridge } from "@runic-artifex/views/mock";

if (import.meta.env.MODE === "mock") {
  const bridge = installMockBridge();
  const counter = bridge.view("counter", {
    state: { count: 0, canIncrement: true },
    commands: { increment: state => ({ count: Number(state.count) + 1 }) },
  });
  setInterval(() => counter.update(state => ({ ...state, count: Number(state.count) + 1 })), 5_000);
}
```

A mock View answers `open`, `close` and `snapshot`, and handles members by their
wire names, as requests name them:

- `set` applies the value to the state member. A handler in `set`, such as
  `set: { title: (state, value) => ({ title: String(value).trim() }) }`, runs
  first: its returned changes are merged after the value, and a throw rejects it.
- `commands` handlers receive the state and the command's wire input; a returned
  object is merged into the state, and a thrown error becomes a failed reply.
- `can` answers from a handler in `can`, otherwise `false` only when the state's
  `can<Command>` member is `false`.
- `checkedFields`, `collections`, `operations` and `streams` declare checked
  fields, keyed collections (edited with `view.collection(field)`) and operations.

Every change is published as a producer does: a frame at the window's next
revision with a `set` change for each member whose value changed. State uses the
wire representation: for example `Int64` values are strings.
`bridge.intercept(handler)` answers any request before the mock does,
`bridge.push(message)` and `view.push(message)` push raw messages, and
`disconnect()`/`reconnect()` exercise the reconnect path.

The wire protocol is specified in the
[Application Views specification](https://github.com/Runic-Artifex/runic-sdk/tree/main/specs/application).

## Testing

The generator writes a typed mock next to each client: `editor.mock.ts`
exports `mockEditor(bridge, definition)`. Its state, setter, command, operation
and interaction types come from the generated client, so a renamed or retyped
ViewModel member breaks the test at compile time. The mock encodes typed
values for the wire (for example `bigint` to an `Int64` string) and decodes
call arguments for the handlers.

```ts
import { beforeEach, test } from "node:test";
import assert from "node:assert/strict";
import { createMockBridge, installMockBridge, type MockBridge } from "@runic-artifex/views/mock";
import { connectEditor } from "../src/generated/editor.js";
import { mockEditor } from "../src/generated/editor.mock.js";

let bridge: MockBridge;
// Each test installs a new Bridge; generated clients then start from a new page runtime.
beforeEach(() => { bridge = installMockBridge(createMockBridge()); });

test("saving clears the dirty flag", async () => {
  mockEditor(bridge, {
    state: { title: "Groceries", body: "", isDirty: true, savedMessage: "" },
    commands: {
      save: async state => {
        await bridge.sleep(250); // virtual time
        return { isDirty: false, savedMessage: `Saved ${state.title}` };
      },
    },
  });
  const editor = await connectEditor();
  const saved = editor.save(); // declares SaveFailure, so it resolves a BridgeOutcome
  await bridge.advance(250);
  const outcome = await saved;
  assert.equal(outcome.ok && outcome.value.savedMessage, "Saved Groceries");
});
```

A definition takes:

- `state`: the client state. Command availability (`can<Command>`) and
  execution (`is<Command>Executing`) default to available and idle, and
  content is a reference `{ kind, id }`: use another mock's `reference`.
- `id`: presents the mock as content on route `content<id>` instead of the
  root route.
- `setters`, `commands`, `canExecute`: handlers by client method. A handler
  receives the decoded state and arguments and returns state changes; a thrown
  error becomes the client's `BridgeError`, with its `kind` when it has one. A
  setter's value is applied first; checked writes (`write<Property>`) use the
  same handler and answer `conflict` for a stale baseline.
  A command whose `CanExecute` is false rejects with reason `unavailable`, as
  .NET does.
- `resolveKey`: for a command with `[RunicCommandKey]` (since 0.11), whether
  .NET would find an object for the key, by client method:
  `resolveKey: { deleteItem: (state, id) => state.items.some(item => item.id === id) }`.
  It runs before `canExecute`; `false` rejects the command with reason
  `unresolved-key` and makes `can<Command>(key)` false. Absent, every key
  resolves.
- `operations`: runs `start<Command>()`. Without a handler the command handler
  runs and the operation succeeds. `"manual"` keeps each operation running
  until the test calls `succeed(result)`, `fail(message)`, `failWith(failure)`
  (the declared failure), `cancel()` or, for a stream, `emit(...items)` on
  `mock.operations.<command>[n]`. A command or operation handler that throws
  `mockFailure(failure)` (from `@runic-artifex/views/mock`) fails with its
  declared failure, as `throw new RunicFailureException(...)` does in .NET. Started
  operations set `is<Command>Executing`. Cancellation is cooperative, as in
  .NET: the client's `cancel()` aborts the operation's `signal`, and the
  operation ends when its handler stops (a throw after the request ends it
  `cancelled`) or the test settles it. A request id names one operation: a
  repeated start returns it, and reuse for another command or input is rejected.
  An operation's `kind` is one of the statuses .NET sends: `running`,
  `succeeded`, `domain-failed`, `failed`, `cancelled`, `expired` and `unknown`
  (an open set, as in .NET). `expire()` drops a finished operation's outcome,
  as .NET does when its retention runs out, so its status, wait and cancel
  answer `expired` and repeating its start answers `expired`. `forget()` makes
  the window forget the request id, as after a restart: it answers `unknown`,
  and a start with the id begins a new operation.

The returned mock has `state`, `update(changes)` (pushes a full state like a
.NET publication), `calls`, `failNext(method, { kind, message, detail, reason })` or
`failNext(method, { kind: "domain-failed", failure })` for a method's declared failure,
`pushFailure({ message })` (a failure notice: the client keeps its last state
and reports the error), `collections.<field>` with `add`, `remove`, `replace`
and `move` (each pushes a frame; `batch(edit)` combines edits into one),
and `interactions.<name>.request(input)`, which resolves with the answer of the
open presentation's handler, or `unhandled` when none is registered.

`createMockBridge({ scheduling: "manual" })` queues every reply and pushed
frame until the test calls `flush()`, `flushUntil(promise)` or `advance(ms)`,
so the test decides when the client observes each one. `sleep(ms)` resolves in
virtual time, which only `advance` moves, in both scheduling modes. Unlike a
real host, whose frames reach subscribers once per animation frame
([Notifications](#notifications)), the mock delivers the state of each push to
subscribers at once, and `flush()` delivers the rest, so a test reads the
result of a push right after it.
`bridge.failNext({ route, kind, member? }, { kind: "transport" })` rejects the
request itself, like a dropped connection, and `disconnect()`/`reconnect()` exercise the reconnect
path. `failNext` takes no `disconnected` kind, because .NET never sends it: use
`disconnect()` for a lost connection.

The mock follows the .NET producer where a client can observe it, and its
replies and pushes are checked against the
[conformance corpus](https://github.com/Runic-Artifex/runic-sdk/tree/main/specs/application/conformance):

- `hello` answers protocol 4 and the implementation `@runic-artifex/views/mock`;
  a client of another protocol gets `protocol-mismatch`.
- Replies name the revision that includes their effects, and the frame of that
  revision is pushed before the reply. Collection frames match the .NET producer
  for the same edits, and a unit of more than 4,096 changes sets the collection
  fields whole.
- Edits are numbered per member: a `set` older than one already applied answers
  `superseded` and changes nothing.
- A checked write retried with the same request id returns its first receipt, and
  reusing the id for another write is a `conflict`. A setter handler that throws an
  error with `kind: "committed-with-error"` keeps the value and returns that receipt.
- A declared failure replies `domain-failed` with `failure` and the revision after
  the call. Inputs and request ids are compared in the RFC 8785 canonical form.
- Requests that change state (`set`, `write`, `command`, `start`) run one at a time,
  as a window's turns do.
- `disconnect()` closes the connection's presentations and watches, and a later
  `hello` with its session answers `released`. After `reconnect()` the client sends
  a new `hello` and opens its Views again. Both are reported to
  `onConnectionChange` listeners, so `onBridgeConnection` sees `reconnecting` and
  `connected`.

It does not model the .NET delivery queue (its 64-frame and 1 MiB bounds and the
recovery they trigger) or the producer's key checks: an edit that would leave an
empty or duplicate key throws a `RangeError` in the test instead of sending a
failure notice. Use `pushFailure` and `push(frame)` for those client paths.

The [CommunityToolkit Notes example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first/Frontend/test)
tests its frontend this way with `bun test`; any runner that loads ES modules,
such as Vitest, works the same.

### Typing in tests

With the default mock, replies arrive before the next key, so a test that types
never sees the race a real host has. Give the mock latency:

- `createMockBridge({ latency: "slow" })` delays every request, reply and push by
  30 to 120 ms each; `latency` also takes a number of milliseconds or
  `{ min, max }`. Replies and pushes keep their order, as the transports
  guarantee. With `immediate` scheduling the delays run on real timers, with
  `manual` on the virtual clock that `advance(ms)` moves.
- `reorderRequests: true` admits requests out of order, newest first among those
  due together, as a producer that does not keep a connection's order may. Stale
  edits then answer `superseded`.

Type key by key, as a user does: Playwright `locator.pressSequentially(text)`,
Testing Library `await user.type(input, text)`. `fill()` and setting `value` write
the whole text at once and hide the race. A page for a browser test can bind its
field to a mock View:

```ts
// test/browser/person.ts, bundled for the browser test
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";

const bridge = installMockBridge(createMockBridge({ latency: "slow", reorderRequests: true }));
bridge.view("person", { state: { name: "" } });
// ...connect the generated client and render the input with the framework's text field binding
```

```ts
// test/person.browser.test.ts
await page.getByRole("textbox", { name: "Name" }).pressSequentially("Ada Lovelace");
await expect(page.getByRole("textbox", { name: "Name" })).toHaveValue("Ada Lovelace");
```

Expect every character in the field and in the mock's state (`view.state`), and
check that no value the page set on the field was older than what was already
typed. The framework packages' `text-field.browser.test.ts` tests do this for each
binding.

## Declared failures

A .NET command that declares its failure type (`[RunicFailure(typeof(SaveFailure))]`)
resolves a `BridgeOutcome` instead of rejecting when it fails as declared:
`{ ok: true, value }` or `{ ok: false, failure }`. Unexpected failures still
reject with `BridgeError`. Handle a `$case` union with `matchCase`, which
requires a handler for every case, or with a `switch` and a `never` check:

```ts
import { matchCase } from "@runic-artifex/views";

const outcome = await editor.save();
if (!outcome.ok) {
  showError(matchCase(outcome.failure, {
    titleRequired: () => "A note needs a title.",
    titleTaken: failure => `"${failure.existingTitle}" already exists.`,
  }));
  return;
}
navigate();
```

Declaring a failure changes control flow for callers that ignore the result:
`await save(); navigate()` used to stop at the rejection and now continues after
a declared failure. Check `outcome.ok` at every call site.

An operation's `outcome(options?)` waits like `wait()` and resolves the same
way; `wait()` and `completion` still return the status, a union discriminated by
`kind` (`BridgeOperationStatus<TResult, TFailure>`):

| Terminal status | `outcome()` |
| --- | --- |
| `succeeded` | resolves `{ ok: true, value: result }`; a `void` or stream operation resolves `value: undefined` |
| `succeeded` with `delivery` | a value operation rejects `BridgeError("failed")` with the status as `cause`; a `void` or stream operation resolves |
| `domain-failed` | resolves `{ ok: false, failure }`; a failure that could not be delivered or decoded rejects `BridgeError("failed")` |
| `failed` | rejects `BridgeError("failed")` with `detail` in development |
| `cancelled` | rejects `BridgeError("cancelled")` |
| `timed-out` | rejects `BridgeError("timeout")` with the status, including `cancellation`, as `cause` |
| `expired`, `unknown` | rejects `BridgeOperationUncertainError`: the outcome is unknown, so do not retry blindly |

`bridgeSuccess(value)`, `bridgeFailure(failure)` and `isBridgeOutcome(value)`
create and recognize outcomes, for example in tests and mocks. An outcome is
recognized by a non-enumerable brand, so a spread or JSON copy is plain data.

The framework command helpers (`useCommand`, `injectCommand` and
`createCommandController`) keep the declared failure of the latest run as
`failure`, apart from `error`. In `@runic-artifex/views-effect`, a declared
failure is the `ViewDomainFailure` error, and `catchCase` handles its cases. In
tests, a mock handler throws `mockFailure(failure)`, and a generated mock's
`failNext(method, { kind: "domain-failed", failure })` and `failWith(failure)`
take the method's typed failure.

## Errors and diagnostics

`describeBridgeError(error)` turns anything a binding reports as `error` into one English sentence for a status line
or an alert, instead of `String(error)` ("BridgeError: …"): a `rejected` or `failed` error shows the producer's
message, such as a setter's validation text; `disconnected`, `released`, `timeout` and `unavailable` name the
condition; a `FieldError` shows its message. Development `detail` is never included. `isBridgeError(error, kind?)`
tells a `BridgeError`, of one kind or a list of kinds, and narrows `error.kind`:

```tsx
{error !== undefined && <p role="alert">{describeBridgeError(error)}</p>}
if (isBridgeError(error, "released")) return <p>This note was closed.</p>;
```

A `BridgeError` names what failed and why:

- `kind` is `rejected`, `cancelled`, `failed`, `disconnected` (the Bridge is
  not connected to .NET: the host ended or its connection dropped), `released`
  (the Bridge is connected, but this View was released, by the page's
  `dispose()` or by .NET, for example because navigation replaced it), `timeout`
  (a Bridge is installed but did not connect in time) or `unavailable` (no
  host installed `window.__runicBridge`, for example a frontend opened from a
  plain Vite server; the message says how to fix it). `disconnected` is decided
  by the client; .NET never sends it. A reply kind the client does not know,
  including a declared failure of a command whose client does not declare it, is
  `failed`.
- `reason` refines `kind` when .NET sent one (`BridgeErrorReason`, open): a
  `rejected` command whose `CanExecute` is false has `unavailable`, a setter
  value or command argument that does not decode or is rejected has
  `invalid-input`, and a `[RunicCommandKey]` command whose key names no object
  has `unresolved-key` (since 0.11). An operation start rejected as
  `unavailable` or `unresolved-key` carries the same reason (since 0.11), with
  the token as its message.
- A connect to a .NET host that speaks another Views protocol version, for example
  a page of this release with `Runic.Application.Views` of another one, rejects
  with `failed` and a message that names both versions. Use the .NET and npm
  packages of one release, rebuild so the generated clients are regenerated, and
  reload the page. A snapshot read after a reconnect to such a host fails the
  same way.
- A command that replaces or leaves its own page in a navigation region ran,
  so its call resolves, with the last state the client accepted, although .NET
  released the page's View before the reply (`released: true`). A later request
  of the released View, including its `snapshot` and `open`, rejects with `released`.
- `route` is the View route of the request that failed, such as `counter`.
- `cause` is the underlying error, such as the transport rejection or the
  decoder error of an invalid state.
- `detail` is `{type, message, stack}` of the .NET exception. .NET sends it
  only in development: when the host environment is `Development`, which
  `dotnet runic dev` sets, or when the window's
  `RunicWindowOptions.IncludeFailureDetail` is `true`. Production replies carry
  only the bounded message, such as `"Save failed."`.

A generated `connect<Name>()` waits up to five seconds (5000 ms) for the host
Bridge, then rejects with a `timeout` `BridgeError`. Pass `timeout` to wait
longer, for example while a slow machine starts the .NET host:

```ts
const client = await connectWorkspace({ timeout: 30_000 });
```

A framework hook takes the same option through a connect function declared
once, outside the component: `const connectSlowly = () => connectWorkspace({ timeout: 30_000 });`
then `useView({ connect: connectSlowly })`.
`waitForBridge({ timeout })` waits for the Bridge without connecting a client.

An operation's `wait({ timeout })` bounds how long the client waits. When the
timeout passes, the client asks .NET to cancel the operation and resolves to a
status with `kind: "timed-out"` and `cancellation` set to .NET's answer
(`cancellation-requested`, `unknown`, `expired`) or `unobserved` when the
request failed. An operation that finished just before the cancellation keeps
its real terminal status. `timed-out` is decided by the client; `completion`
still reports the status .NET settles on. The cancellation applies to the
operation itself, so every other observer of it, including another tab or a
later `recover<Command>(requestId)`, sees it cancelled. Use a timeout only
when the caller owns the operation.

```ts
const save = await editor.startSave();
const status = await save.wait({ timeout: 10_000 });
if (status.kind === "timed-out") showRetry();
```

`BridgeOperationUncertainError` is the other error class: the client could not
observe whether .NET admitted or finished an operation, or .NET no longer knows
its outcome. It has the View's `contract`, the operation's `requestId` and, when
known, the View `route`; its constructor takes `(message, { contract, requestId,
route?, cause? })`. It is deliberately not a `BridgeError`: a `BridgeError` is a
failure a caller may retry, while an uncertain operation may have run, so start it
again only with the same request id. `BridgeError` and
`BridgeOperationUncertainError` have public constructors for tests and mocks.

String unions are open or closed. `BridgeErrorKind`, `BridgeErrorReason`, `BridgeDiagnosticCode`,
`BridgeOperationDeliveryFailureKind` and `BridgeOperationCancelKind` are open: a
minor release may add a member, so a `switch` over them needs a `default`.
`BridgeOperationStatusKind` and `BridgeConnectionState` are closed until the next
major. Multi-word values are kebab-case, such as `domain-failed` and `timed-out`.

`onBridgeDiagnostic(listener)` observes runtime failures. Each diagnostic has a
`code` (`BridgeDiagnosticCode`): a `BridgeErrorKind` other than `released` for a
failed call or a missing or unconnected Bridge, `uncertain` for an uncertain
operation, `timed-out` for an operation `wait({ timeout })` that passed its
deadline, `reported` for errors the runtime caught from listeners, invalid pushes
and reconnect work (those are also passed to `reportError`), `protocol` for a
.NET host that speaks another Views protocol version (the only diagnostic of the
reads it fails) and `version` for a .NET
host whose `Runic.Application.Views` is of another release than this package
(each once per Bridge; no other producer is compared), `revision-timeout` for a
reply whose revision the route did not reach within five seconds (a producer bug;
the client then reads a snapshot), and `stale-input`, which only
`@runic-artifex/vite-plugin-runic` reports in development
([Text inputs](#text-inputs)). Each failure is delivered once. `@runic-artifex/vite-plugin-runic`
forwards them to its DevTools dock during development. A call of a released
View is expected around every navigation, so its `released` rejection reaches
the caller but is not reported as a diagnostic; `onBridgeConnection` reports a
lost host.

## Connection state

`onBridgeConnection(listener, options?)` reports the page's connection to its
.NET host as it changes, so a page can show "the connection to the application
was lost" without waiting for its next call. It calls `listener` with the
current state at once, then after each change, and returns a function that
stops observing:

```ts
import { onBridgeConnection } from "@runic-artifex/views";

const stop = onBridgeConnection(state => {
  banner.hidden = state !== "lost";
}, { lostAfter: 5_000 });
```

| State | Meaning |
| --- | --- |
| `connecting` | The Bridge has not connected yet. |
| `connected` | The Bridge is connected. After a reconnect, generated clients send a new `hello` and open their Views again. |
| `reconnecting` | The connection dropped; the host transport is restoring it. |
| `lost` | No connection for `lostAfter` milliseconds (default 5000; `0` makes a drop `lost` at once), or none since the page loaded. Not terminal: it becomes `connected` when the host comes back. |

Runic Desktop and CS-WebUI report a dropped WebSocket within about 250 ms, for
example when the host process ends or is killed. Neither sends a heartbeat, so a
connection that silently stops passing data (a remote CS-WebUI browser behind a
broken network path) is seen only when the transport notices it. While the page
unloads (`pagehide`), the state is not reported, so a reload or a navigation to
another page does not show `reconnecting`.

Each subscription follows the host from the moment it starts, so one that starts
while the connection is down reports `connecting`, not `reconnecting`. The
framework packages wrap it for components: `useBridgeConnection` in
`@runic-artifex/react`, `@runic-artifex/vue` and `@runic-artifex/svelte`, and
`injectBridgeConnection` in `@runic-artifex/angular`. During `vite dev`,
`@runic-artifex/vite-plugin-runic` shows the page's state on its DevTools dock from
page load.
