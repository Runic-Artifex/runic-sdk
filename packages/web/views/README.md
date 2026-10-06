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
`disconnected` or `timeout`.

All generated modules on a page share one runtime instance, even when several
bundles or copies of this package load: a route has one push callback and one
revision, and `instanceof BridgeError` holds for errors from any copy.

The framework packages `@runic-artifex/react`, `@runic-artifex/vue`,
`@runic-artifex/svelte` and `@runic-artifex/angular` connect and dispose
clients with the component lifecycle.

## Incremental collections

A .NET ViewModel can mark a read-only collection of DTO rows with
`[RunicCollection(nameof(Row.Id))]`. Its generated client then receives indexed
add, remove, replace and move frames instead of the whole state, applies them on
top of its current revision, and reads one fresh snapshot if a frame is missing
or invalid. Rows that a frame does not touch keep their object identity, so
frameworks can skip rendering them. Clients still see a complete array in
`snapshot` and `subscribe`.

### `defineCollection(decode, key)`

```ts
function defineCollection<T>(decode: (wire: unknown) => T, key: (item: T) => string): BridgeCollectionDefinition;
```

Describes one collection field for the runtime: `decode` validates and converts a
wire row (throwing for an invalid one), and `key` returns the row's key, which
must be a nonempty string unique within the field. Generated modules call it for
each `[RunicCollection]` field, deriving `key` from the attributed property, and
pass the result to the runtime. Applications normally do not call it. If you write
it by hand, the key must match the .NET wire key: the string itself, a lowercase
GUID, or an `Int32` in decimal.

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

Avoid sending a request when `start` and `size` have not changed. The
[DynamicData example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/dynamicdata) shows a complete page. The frame
format, fallbacks and recovery rules are specified in
[Collection delta frames](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/application/collection-deltas.md).

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
