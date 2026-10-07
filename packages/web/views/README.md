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
`page<Kind>(id)` references for presented content. A connected `<Name>Client`
has a `snapshot`, `subscribe(listener)`, `dispose()` and one method per setter,
command and operation:

```ts
import { BridgeError } from "@runic-artifex/views";
import { connectCounter } from "./generated/counter.js";

const counter = await connectCounter();
const stop = counter.subscribe(state => render(state.count));
try {
  await counter.increment();
} catch (error) {
  if (error instanceof BridgeError && error.kind === "disconnected") showOffline();
}
stop();
counter.dispose();
```

`subscribe` delivers the current state first. After `dispose()` the client
keeps its last `snapshot`, and `subscribe` delivers that state once and returns
a no-op, so framework stores can read a client during teardown. Calls reject
with a `BridgeError` whose `kind` is `rejected`, `cancelled`, `failed`,
`disconnected`, `timeout` or `unavailable`; see
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
| `@runic-artifex/views` | Applications: errors, diagnostics, controllers, viewport helpers and client types |
| `@runic-artifex/views/mock` | Development and tests without .NET |
| `@runic-artifex/views/generated` | Generated modules and hand-written test clients: `connectView`, `viewReferences`, `defineCollection(s)`, `defineInteractions`, `bridgeOperations`, `decodeBridgeValidation` |
| `@runic-artifex/views/generated/wire` | Generated modules, generated `*.mock.ts` files and hand-written test clients: the wire decoders, imported as `import * as bridgeWire` |

The generated entries follow the generator and may change between releases.
Application code that uses generated clients imports only the root entry.
Hand-written clients, such as a test client that connects a route without
generated code, may import the generated entries; regenerate or update them
with each SDK upgrade. A generated module passes the interaction, operation and keyed
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

The framework packages share three framework-neutral controllers, so each
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
  `viewSourceIdentity` tell clients from connectors and compare sources.
- `createCommandController(command)` has `run(...args)`, which never rejects
  and resolves to `undefined` after a failure, and `current` with `pending`,
  `error` and `failure`. For a command that resolves a `BridgeOutcome`,
  `failure` is the declared failure of the latest run; `error` stays the
  unexpected failure. Starting a run and `reset()` clear both, and a run
  superseded by a later run or by `reset()` sets neither.
- `createCollectionViewportController({ totalCount, rowHeight, overscan? })`
  has `attach(element)`, which follows the element's scroll position (once per
  animation frame) and size, and `update(options)`. `current` is the
  `collectionViewport` range and changes only when the range or sizes change.

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
path.

The mock follows the .NET protocol where a client can observe it:

- Collection frames match the .NET producer for the same edits (checked against
  the shared collection delta fixtures). Each change advances the revision, and a
  frame of more than 4,096 changes is sent as a full state.
- With manual scheduling, a full state or failure notice replaces the frames of its
  route that the client has not received yet, as .NET delivery does.
- A checked write retried with the same request id returns its first receipt, and
  reusing the id for another write is a `conflict`. A setter handler that throws an
  error with `kind: "committed-with-error"` keeps the value and returns that receipt.
- Only snapshot replies carry `protocol`, and a declared failure replies
  `domain-failed` with `failure` and the state after the call.

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
| `timedOut` | rejects `BridgeError("timeout")` with the status, including `cancellation`, as `cause` |
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

- `kind` is `rejected`, `cancelled`, `failed`, `disconnected`, `timeout`
  (a Bridge is installed but did not connect in time) or `unavailable` (no
  host installed `window.__runicBridge`, for example a frontend opened from a
  plain Vite server; the message says how to fix it). A reply kind the client
  does not know, including a declared failure of a command whose client does not
  declare it, is `failed`.
- `route` is the Bridge route that failed, such as `counterIncrement`.
- `cause` is the underlying error, such as the transport rejection or the
  decoder error of an invalid state.
- `detail` is `{type, message, stack}` of the .NET exception. .NET sends it
  only in development: when the host environment is `Development`, which
  `dotnet runic dev` sets, or when the application sets
  `BridgeDiagnostics.IncludeFailureDetail = true`. Production replies carry
  only the bounded message, such as `"Save failed."`.

Generated clients wait up to five seconds for the host Bridge. To wait longer,
call `waitForBridge` before the first `connect<Name>()`:

```ts
import { waitForBridge } from "@runic-artifex/views";

await waitForBridge({ timeout: 30_000 }); // waitForBridge(30_000) also works
```

An operation's `wait({ timeout })` bounds how long the client waits. When the
timeout passes, the client asks .NET to cancel the operation and resolves to a
status with `kind: "timedOut"` and `cancellation` set to .NET's answer
(`cancellation-requested`, `unknown`, `expired`) or `unobserved` when the
request failed. An operation that finished just before the cancellation keeps
its real terminal status. `timedOut` is decided by the client; `completion`
still reports the status .NET settles on. The cancellation applies to the
operation itself, so every other observer of it, including another tab or a
later `recover<Command>WithRequestId`, sees it cancelled. Use a timeout only
when the caller owns the operation.

```ts
const save = await editor.startSave();
const status = await save.wait({ timeout: 10_000 });
if (status.kind === "timedOut") showRetry();
```

`onBridgeDiagnostic(listener)` observes runtime failures: failed calls, a
missing or unconnected Bridge, operation timeouts, and errors the runtime caught
from listeners, invalid pushes and reconnect work (those are also passed to
`reportError`), and a .NET host that speaks another Views protocol version
(`code: "protocol"`, once per Bridge). Each failure is delivered once. `@runic-artifex/vite-plugin-runic`
forwards them to its DevTools dock during development.
