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

All generated modules on a page share one runtime instance, even when several
bundles or copies of this package load: a route has one push callback and one
revision, and `instanceof BridgeError` holds for errors from any copy.

The framework packages `@runic-artifex/react`, `@runic-artifex/vue`,
`@runic-artifex/svelte` and `@runic-artifex/angular` connect and dispose
clients with the component lifecycle. They are thin bindings over the
controllers in [Framework bindings](#framework-bindings).

### Package entries

| Entry | For |
| --- | --- |
| `@runic-artifex/views` | Applications: errors, diagnostics, controllers and client types |
| `@runic-artifex/views/mock` | Development and tests without .NET: `createMockBridge`, `installMockBridge`, `mockFailure` and the `Mock*` types of generated mocks |
| `@runic-artifex/views/bindings` | Framework bindings such as `@runic-artifex/react`: `viewSourceIdentity`, `isViewClient` |
| `@runic-artifex/views/generated` | Generated modules: `connectView`, `viewReferences`, `defineCollection(s)`, `defineInteractions`, `bridgeOperations`, `decodeBridgeValidation` |
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
add, remove, replace and move frames instead of the whole state, applies them on
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
[Collection delta frames](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/application/collection-deltas.md).

## Framework bindings

The framework packages share framework-neutral controllers, so each
binding keeps only its own reactivity glue. Applications normally use the
bindings; a binding for another framework can use the controllers directly.
Each controller has a `current` value, replaced by a new object on every
change, and `subscribe(listener)`, which returns an unsubscribe function.

- `createViewController<TClient>({ release? })` follows a `ViewSource`: a
  connector (`{ connect() }`, such as a generated page reference) that it
  connects and releases, or a connected client that it only observes.
  `setSource(source)` ignores a source with the same client or `connect`
  function and does not notify; read `current` after calling it. `current` has
  `source`, `client`, `state`, `error` and `pending`. `retry()` connects
  again and `dispose()` releases the client. `isViewClient` and
  `viewSourceIdentity` from `@runic-artifex/views/bindings` tell clients from
  connectors and compare sources.
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

## Operation feedback and latest selection

An operation Start receipt proves admission; it does not prove completion. A Cancel
response proves only that the cancellation request was observed. Keep the UI pending
until the public handle's terminal `wait()` completes. The desktop transport admits
control callbacks while this wait is active, so short status polling is unnecessary.
Render domain progress and recovery from the generated View snapshot: the operation
handle supplies protocol status and outcome, not application progress.

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

`current` has `pending`, `admitting`, `operation`, `status`, `outcome`, `failure`,
`error`, `cancelled`, `cancellationRequested`, `cancelling`, `cancellation` and
`cancelError`. `pending` spans Start admission, terminal wait, any completion barrier
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

## Developing without .NET

`@runic-artifex/views/mock` provides an in-memory Bridge. Install it before the
first `connect<Name>()` call, for example in a Vite `mock` mode:

```ts
import { installMockBridge } from "@runic-artifex/views/mock";

if (import.meta.env.MODE === "mock") {
  const bridge = installMockBridge();
  const counter = bridge.view("counter", {
    state: { count: 0, canIncrement: true },
    routes: { Increment: state => ({ count: Number(state.count) + 1 }) },
  });
  setInterval(() => counter.update(state => ({ ...state, count: Number(state.count) + 1 })), 5_000);
}
```

A mock View answers `Snapshot`, `Mount`, `Unmount`, `Set<Property>` (storing
the argument) and `Can<Command>` (`true`) by default. Route handlers receive
the state and call arguments; a returned object is merged into the state, a
boolean answers an availability query and a thrown error becomes a failed
reply. State uses the wire representation: for example `Int64` values are
strings. `bridge.route(name, handler)` serves any other route, such as
operation status, and `disconnect()`/`reconnect()` exercise the reconnect path.

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
.NET publication), `calls`, `failNext(method, { kind, message, detail })` or
`failNext(method, { kind: "domain-failed", failure })` for a method's declared failure,
`pushFailure({ message })` (a failure notice: the client keeps its last state
and reports the error), `collections.<field>` with `add`, `remove`, `replace`
and `move` (each pushes a delta frame; `batch(edit)` combines edits into one),
and `interactions.<name>.request(input)`, which resolves with the mounted
client handler's answer, or `unhandled` when none is registered.

`createMockBridge({ scheduling: "manual" })` queues every reply and pushed
frame until the test calls `flush()`, `flushUntil(promise)` or `advance(ms)`,
so the test decides when the client observes each one. `sleep(ms)` resolves in
virtual time, which only `advance` moves, in both scheduling modes.
`failNext(route, { kind: "transport" })` rejects the call itself, like a
dropped connection, and `disconnect()`/`reconnect()` exercise the reconnect
path. `failNext` takes no `disconnected` kind, because .NET never sends it: use
`disconnect()` for a lost connection.

The mock follows the .NET protocol where a client can observe it:

- Collection frames match the .NET producer for the same edits (checked against
  the shared collection delta fixtures). Each change advances the revision, and a
  frame of more than 4,096 changes is sent as a full state.
- With manual scheduling, a full state or failure notice replaces the frames of its
  route that the client has not received yet, as .NET delivery does.
- A checked write retried with the same request id returns its first receipt, and
  reusing the id for another write is a `conflict`. A setter handler that throws an
  error with `kind: "committed-with-error"` keeps the value and returns that receipt.
- Only snapshot replies carry `protocol` (3), and a declared failure replies
  `domain-failed` with `failure` and the state after the call.
- `disconnect()` ends the mounted presentations, and a later `Mount` in their
  browser session answers `released`, as .NET does when a CS-WebUI reconnect
  keeps the connection key. After `reconnect()` the client mounts its Views again
  in a new browser session. Both are reported to `onConnectionChange` listeners,
  so `onBridgeConnection` sees `reconnecting` and `connected`.

It does not model the .NET delivery queue (its 64-frame and 1 MiB bounds and the
recovery they trigger) or the producer's key checks: an edit that would leave an
empty or duplicate key throws a `RangeError` in the test instead of sending a
failure notice. Use `pushFailure` and `push(frame)` for those client paths.

The [CommunityToolkit Notes example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first/Frontend/test)
tests its frontend this way with `bun test`; any runner that loads ES modules,
such as Vitest, works the same.

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
  `rejected` command whose `CanExecute` is false has `unavailable`, and a setter
  value or command argument that does not decode or is rejected has
  `invalid-input`. `unresolved-key` is reserved for a later release.
- A connect to a .NET host that speaks another Views protocol version, for example
  a page of this release with `Runic.Application.Views` of another one, rejects
  with `failed` and a message that names both versions. Use the .NET and npm
  packages of one release, rebuild so the generated clients are regenerated, and
  reload the page. A snapshot read after a reconnect to such a host fails the
  same way.
- A command that replaces or leaves its own page in a navigation region ran,
  so its call resolves, with the last state the client accepted, although .NET
  released the page's View before the reply. A later call of the released View,
  including its snapshot read and its `Mount`, rejects with `released`.
- `route` is the Bridge route that failed, such as `counterIncrement`.
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
(each once per Bridge). Each failure is delivered once. `@runic-artifex/vite-plugin-runic`
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
| `connected` | The Bridge is connected. Generated clients re-read their state after a reconnect. |
| `reconnecting` | The connection dropped; the host transport is restoring it. |
| `lost` | No connection for `lostAfter` milliseconds (default 5000; `0` makes a drop `lost` at once), or none since the page loaded. Not terminal: it becomes `connected` when the host comes back. |

Runic Desktop and CS-WebUI report a dropped WebSocket within about 250 ms, for
example when the host process ends or is killed. Neither sends a heartbeat, so a
connection that silently stops passing data (a remote CS-WebUI browser behind a
broken network path) is seen only when the transport notices it. While the page
unloads (`pagehide`), the state is not reported, so a reload or a navigation to
another page does not show `reconnecting`. A host script without
`onConnectionChange`, from a release before it, reports the first connection
and each reconnect but not a loss.
