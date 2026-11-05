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

### Scoped commands

A command marked `[RunicPresentationScoped]` in .NET (a command property or a
CommunityToolkit `[RelayCommand]` method) runs only for the presentation it was
issued for: a wizard's **Next** clicked twice before the next page rendered
advances once. The generated client lists such commands in `scopedCommands`,
and with a .NET host of 0.15 or later (the `hello` feature
`presentation-epoch/1`) each call sends the epoch of the state its client's
subscribers last received, which is the page on screen, not a newer state that
waits for the next animation frame. When the application renewed the
presentation meanwhile (`RunicPresentation.Renew`, or content presented again),
.NET runs nothing and answers `superseded`. Such a call resolves like one that
ran: once the route reached the reply's revision, with the current state
(`commandOutcome` resolves `bridgeSuccess(state)`). It reports no diagnostic,
and `useCommand`, `injectCommand` and `createCommandController` show no error.
A caller that must know compares the state it got. In development, the state
inspector of `@runic-artifex/vite-plugin-runic` lists it. Other commands, setters,
operation starts and interactions are not scoped. With an earlier host, scoped
commands run as plain commands.

### Presented states and window close

With a .NET host of 0.15 or later (the `hello` feature `presented/1`), .NET can
wait until the page shows a state: `RunicPresentation.WaitForPresentedAsync`
asks each page that presents the ViewModel's route, and the client answers once
the route reached the asked revision, the state was delivered to its
subscribers and one animation frame passed, so the browser rendered it. A route
whose clients have no subscriber, such as a kept client in its grace period or
a `preloadView`, is not on screen: it answers once a binding subscribes. A
hidden page answers after the timer that delivers its states. Applications do
nothing for this.

When the application closes the window, .NET tells each page first. The client
delivers its pending states, lets one frame render them, acknowledges, and
reports the connection state `closed` ([Connection state](#connection-state)),
so a page, such as a CS-WebUI browser tab that outlives the application, can
say "The application closed this window." instead of showing a lost connection.

### View requests

A ViewModel can ask its View for a presentation action that needs no answer,
such as focusing a field, scrolling a row into view or showing a toast: a
`ViewRequest<T>` property in .NET, raised with `Raise(input)`. The generated
client has `client.requests.<name>` for each one. Register the page's handler
with `handle((input, { signal }) => …)`, which returns the function that
unregisters it:

```ts
const stop = editor.requests.focus.handle(({ member }) => {
  document.querySelector<HTMLElement>(`[data-member="${member}"]`)?.focus();
});
// …
stop();
```

A client has one handler per request; `handle` again replaces it. The handler
runs once the page painted the state the request was raised with: the route
reached the request's revision, the state was delivered to subscribers and one
animation frame passed, so the elements of that state are rendered (as for
[presented states](#presented-states-and-window-close)). Its return value is
ignored, and a throw or rejection is reported with `reportError`. `signal`
aborts when the handler is replaced or unregistered, the client is disposed or
the connection is lost, so an asynchronous handler, such as an animated scroll,
can stop. A request is never replayed: one raised while no handler is
registered, while the page is disconnected, or before a reload is not delivered
later; .NET decides with the request's delivery rule whether it waits for the
next View that registers it (`ViewRequestDelivery.NextMounted`). The client
registers its handlers with a .NET host of 0.16 or later (the `hello` feature
`view-requests/1`); with an earlier host `handle` still works and receives
nothing. In tests, a generated mock's `requests.<name>.raise(input)` raises a
request ([Testing](#testing)).

### Region collections

A .NET `[RunicRegionCollection]` property, such as document tabs that each keep
their own back stack, is a `RegionCollection<R>` in the state: `regions`, each
`{ key, content }`, and `selected`, the index of the selected region or `null`
without regions. `key` identifies a region for as long as it exists, so key a
tab's outlet by it; `content` is the region's current page, or `null`. With a
.NET host of 0.16 or later (the `hello` feature `region-collections/1`), the
generated `select<Name>(key)` and `close<Name>(key)` resolve with a
`RegionOutcome`, the .NET `NavigationOutcome`: `{ kind, rejection, arrival }`.
A departure guard that keeps the tab resolves the call with `kind: "Rejected"`
and `rejection: "Guard"`; a key of a closed region rejects with reason
`unresolved-key`. `selected` changes only when a switch commits, so a tab bar
that highlights it shows the kept tab after a veto.

`regionEntries(collection)` lists the regions with their `index` and whether
each is `selected`, and `selectedRegion(collection)` returns the selected one.
The framework adapters' `RegionOutlets` renders one outlet per region. See
[Region collections](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#region-collections)
for the .NET side.

### Back and the URL

With a .NET host of 0.16 or later (the `hello` feature `navigation-path/1`),
the page can go back in its window and keep its URL in step with the window's
route path:

```ts
import { installBackNavigation, installNavigationPath } from "@runic-artifex/views";

installBackNavigation();  // the mouse's Back button, Alt+Left, Cmd+[ on macOS and Browser Back
installNavigationPath();  // #/customers/42 follows the .NET path region
```

- `installBackNavigation()` sends the window's Back for the mouse's button 3,
  Alt+Left, Cmd+[ (on macOS only) and the Browser Back key: .NET goes back in
  the innermost presented region that can, through its departure guards. It
  leaves Alt+Left and Cmd+[ to text fields, and events the page prevented to the
  page. Keys and clicks inside an iframe go to the iframe's document and don't
  reach it. While `installNavigationPath()` is active, the same inputs go back in
  the page's history instead, and its `popstate` opens the previous path, so a
  Back never runs twice; at the first history entry they send the window's Back.
- `installNavigationPath({ prefix })` keeps the URL's fragment (`#/` and the
  path by default) and the history in step with the region the .NET window syncs
  (`RunicPresentation.SyncNavigationPath`). A push adds a history entry and a
  Back moves back. Each entry keeps its index and path in `history.state`, so
  a reload keeps them. The browser's Back and Forward and an edited address bar
  open their paths in .NET; going back to an entry without a path, such as a
  home page without a route, sends the window's Back. A refused move, or one no
  route matches, puts the URL back; of several quick moves only the last one
  does. A path in the URL when the page loads opens as a deep link once the
  window reports its path; a reload or reconnect shows the window's path. A
  fragment that doesn't start with the prefix, such as an in-page anchor, is
  left alone.
- Several pages of one window, such as two CS-WebUI browsers, follow the same
  path region: each history follows every change, and the last change wins.
- `navigateBack()` and `openNavigationPath(path)` send the same requests from
  code and resolve with `{ outcome, path }`: the .NET `NavigationOutcome`, or
  `null` when no route matched, and the window's path after it.
  `onNavigationPath(listener)` reports the path when the connection starts and
  after each change, with `operation` `"push"`, `"back"` or `"replace"`, for a
  router of the page's own, such as SvelteKit's.
- A host without the feature rejects the calls with a `failed` BridgeError. Both
  installs return a function that removes them; each takes `{ window }` for tests.
- The mock bridge answers `navigation` requests with
  `createMockBridge({ navigation: { path, handle(request) } })`, and
  `mock.pushNavigationPath(path, operation)` stands in for a change in .NET.

See [Back and the URL](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#back-and-the-url)
for the .NET side.

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
| `@runic-artifex/views/generated` | Generated modules: `connectView`, `viewReferences`, `disposable`, `defineCollection(s)`, `defineInteractions`, `defineRequests`, `bridgeOperations`, `decodeBridgeValidation` |
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
[Errors and diagnostics](#errors-and-diagnostics)). A generated module passes the interaction, view request, operation and keyed
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

## Query collections

A .NET `RunicQueryCollection<TRow, TKey>` property is a list whose rows stay in .NET, such as a database table
(`query-collections/1`). Its member in `snapshot` is a `QueryCollectionState`, `{ query, total, version }`, and the
generated client has one `QueryCollection<TRow>` per collection in `client.queries`. Each one loads the rows of its own
range, so two pages that scroll the same list don't move each other's rows.

```ts
const catalog = await connectCatalog();
const rows = catalog.queries.rows;           // QueryCollection<CatalogRow>
rows.setRange(500, 40);                      // placeholders now, rows when they arrive
rows.subscribe(() => render(rows.current));  // { query, total, version, start, count, rows, loading, error }
await rows.setQuery({ sort: [{ field: "label", descending: true }], filters: [{ field: "label", op: "contains", value: "9" }] });
const index = await rows.indexOf("4711");    // the key's position in the current query, or undefined
```

- `current.rows` has one `QueryRow` per position of the range: `status` `loaded` with `key` and `row`, or a placeholder
  (`loading`, `error`). A row of an older version stays visible, `stale: true`, until its new version arrives.
  `loading` and `error` summarize the range; `retry()` loads it again after a failure.
- The collection sends at most one `query-rows` at a time, for the positions of the latest range it has not loaded at
  the current version (at most 1,000 rows), so fast scrolling sends few requests. It keeps the rows near its range
  (at least 2,000) and drops the others. A range it scrolled past while its request was in flight is not cancelled (the
  protocol has no request for that); it costs at most one range. A request .NET answers `busy`, because another
  presentation on the connection has as many in flight as .NET admits, is sent again after a short delay.
- Rows loaded under a query that has since changed are dropped, and rows loaded at a version the state has not reached
  yet stay placeholders until it does, so a page never shows a row of another query.
- `setQuery(query)` changes ViewModel state that every presentation shares, and resolves once the state includes it.
  One query is in flight at a time: a query set meanwhile replaces the one waiting, and the calls it replaced settle with
  it, so a search box can call it on every keystroke. A query .NET cannot run, or an unknown filter `op`, rejects with a `BridgeError` of kind `rejected` and reason `invalid-input`. A filter is
  `equals` (`value`), `contains` (a string `value`), `range` (`min?`, `max?`, inclusive) or `custom` (a filter .NET
  defines by name, with an optional `value`). Values are wire values: an `Int64` as a decimal string.
- When the version increases, after a query change or a change of rows in .NET, the collection loads its range again;
  a new query discards every row it had.

### `createQueryViewportController(collection, options)`

Lays a query collection out in a scroll container with **variable row heights**: rows are placed by their measured
height, and `estimateSize` (a number, or a function of the index) for rows not measured yet. Sizes live in a Fenwick
tree, so a list of 500,000 rows finds the row at a scroll offset in O(log n). The controller calls the collection's
`setRange` for the rows the container shows plus `overscan` (5), and its `current` is a `QueryViewport`: `items` (each
row of the range with its `top` and `size`), `totalSize` for the spacer, and `total`, `query`, `loading` and `error`.

- `attach(element)` follows the container's scroll position (once per animation frame) and size.
- `observe(rowElement, index)` measures a rendered row with a `ResizeObserver`; a row above the visible ones that changes
  height moves `scrollTop` by the difference, so what the user looks at stays in place.
- `scrollToKey(key, align?)` asks .NET for the key's position (`query-index`, with `locate(key)`) and scrolls to it; a
  position found under a query that changed meanwhile is searched again. It resolves `false` when no row of the current
  query has the key. Until the total is known, the list stays long enough to reach the position. `scrollToIndex(index, align?)` scrolls to a position.
- A new query forgets the measured heights. `createRowSizes(count, estimate)` is the size tree alone, for any list.

The framework packages wrap it as `useQueryCollection` (`injectQueryCollection` in Angular; `queryViewportChanges` in
`@runic-artifex/views-effect`):

```tsx
const list = useQueryCollection(catalog.queries.rows, { estimateSize: 32 });
<div ref={list.ref} style={{ height: 480, overflow: "auto" }}>
  <div style={{ height: list.totalSize, position: "relative" }}>
    {list.items.map(item => (
      <div key={item.key ?? `p${item.index}`} ref={list.measure(item.index)} style={{ position: "absolute", top: item.top, width: "100%" }}>
        {item.row ? item.row.label : "…"}
      </div>
    ))}
  </div>
</div>
```

A one-way view request from .NET (`view-requests/1`) can trigger `scrollToKey` once that feature ships, for example to
bring a search result into view: the page handles the request by calling `list.scrollToKey(key)`.

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

`kind` selects the field's text form:

| Kind | C# types | Value | Text |
| --- | --- | --- | --- |
| `text` | `string`, `Guid`, `TimeSpan` | `string` | the value |
| `number` | `int`, `double`, … (`integer` for integer types) | `number` | `1.5`, or in the `locale` option's form |
| `decimal` | `decimal` | `string`, exact (`"1234.50"`) | as `number`; at most 28 digits after the point |
| `bigint` | `long`, `ulong`, `BigInteger` | `bigint` | as `number` |
| `boolean` | `bool` | `boolean` | `true` or `false` (bind a checkbox) |
| `date` | `DateOnly` | `string` `YYYY-MM-DD` | the same, as `<input type="date">`, or in the locale's form (`03.11.2026`) |
| `time` | `TimeOnly` | `string` `hh:mm:ss[.f]` | `hh:mm` when the seconds are zero, as `<input type="time">`, or in the locale's form (`9:30 PM`) |
| `datetime` | `DateTime`, `DateTimeOffset` (`offset: true`) | `string` (`2026-11-03T09:30:00+01:00`) | the page's local date and time, as `<input type="datetime-local">`, or in the locale's form |
| `choice` | enums (`choices` lists the names) | `string` | the name |
| `value` | objects, lists, unions | the value | none |

`nullable` marks nullable members. Each C# enum also has a value list beside its type, in declaration order and
without aliases, which `choices` repeats: `export const LayoutValues = ["List", "Grid"] as const`, for a `<select>`.

`constraints` carries the member's DataAnnotations, so a page can mirror them on its input; .NET still validates:

| Attribute | `constraints` |
| --- | --- |
| `[Required]` | `required: true` |
| `[MinLength]`, `[MaxLength]`, `[StringLength]` | `minLength`, `maxLength` |
| `[Range]` | `min`, `max` (numbers, or the invariant text of other bounds such as decimals), `minExclusive`, `maxExclusive` |
| `[RegularExpression]` | `pattern`, for the patterns a browser's `pattern` attribute (which compiles with the `v` flag) reads as .NET does: no inline options, named groups, lookbehind, `\A`/`\Z`/`\z`/`\G`, `\p`, `\d`/`\w`/`\b` (Unicode in .NET, ASCII in JavaScript), and in a character class no unescaped `( ) [ { } / \|`, lone `-` or doubled punctuators such as `&&`. Other patterns are left out; .NET still validates them |

```tsx
const { maxLength, required } = EditorFields.title.constraints ?? {};
<input {...title.inputProps} maxLength={maxLength} required={required} />
```

The validation errors that .NET reports for these attributes carry .NET's message. To show them in the window's
language, either give them a text reference in .NET (`RunicValidationOptions.TextFor`, see
[Translated text](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#translated-text)),
or check the constraints on the page and show your own message before the value is written:

```ts
import { m } from "virtual:runic-translations/app/messages";

function constraintMessage(field: { readonly constraints?: BridgeFieldConstraints }, text: string): string | undefined {
  const { required, minLength, maxLength } = field.constraints ?? {};
  if (required && text.trim() === "") return m.field_required({});
  if (maxLength !== undefined && text.length > maxLength) return m.field_too_long({ max: maxLength });
  if (minLength !== undefined && text.length > 0 && text.length < minLength) return m.field_too_short({ min: minLength });
  return undefined;
}
```

A page check is a hint: .NET still validates, and its errors still arrive in `errors`. For a member checked on the
page, show the page's message in place of `errors`, so the same problem does not show twice.

`read` and `write` are the member and its setter; `checked` and `baseline` its
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
- `validating`: the member's validation is in progress, such as an asynchronous rule that is still checking: show
  "checking…" (`validation-pending/1`, a .NET ViewModel with `IRunicPendingValidation`). Generated descriptors of a
  View with validation have `validating(state)`, which reads `validation.pending`.

`input(value)` takes a value, `inputText(text)` the text of an input, parsed for the field's kind: `-` or `1e` in a
number field stay the draft with a `FieldError` of kind `invalid-text`, and nothing is written until the text parses.
An empty text is `null` for a nullable field of any kind but `text`.

A `datetime` text without an offset is a local date and time: a `DateTimeOffset` gets the page's offset at that time,
a `DateTime` in UTC stays in UTC, and other `DateTime` values stay local. An offset typed (`…T09:30+02:00`) is kept.
Text that still shows the field's value is that value, with its offset and fraction, and in a DST overlap (a local
time that occurs twice) an edit keeps the offset of the value it replaces. A `DateTime`'s kind (UTC or local) is a
convention of the application that the generator cannot see, so it is taken from the value the field replaces: a
nullable UTC `DateTime` that starts as `null` would be written as a local date and time, which .NET reads as
`Unspecified`. Give such a field a `parse` that writes UTC:

```ts
const due = createFieldController(TaskFields.dueUtc, {
  parse: text => text.trim() === "" ? null : new Date(text).toISOString().replace(/\.000Z$/, "Z"),
});
```

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
- `locale` (such as `"de-DE"` or `navigator.languages`) shows `number`, `decimal` and `bigint` text with the locale's
  decimal separator and digits (`1,5`) and reads them, with group separators between thousands (`1.234,5`; `1.5` is
  not 15 in German). Text is never grouped while it is edited. It shows `date`, `time` and `datetime` text in the
  locale's numeric form, in the Gregorian calendar with a four-digit year and two-digit days and months
  (`03.11.2026` in `de`, `11/03/2026` in `en-US`, `2026/11/03` in `ja`), and times in its hour cycle
  (`21:30`, `9:30 PM`), with seconds when they are not zero. It reads that form in the locale's order of day, month
  and year, with any of `. / -` or the locale's own separators, a day period (`PM`, or `am`/`pm`) and the locale's
  digits, and always the ISO form too (`2026-11-03`, `21:30`, `2026-11-03T21:30`). A two-digit year, a time with
  a day period and an hour above 12, and a date that does not exist are `invalid-text`. A time without a day period
  is read as 0–23 in every locale.
- Without `locale`, these fields follow the window's locale when the application sets one (`IRunicWindowLocale`,
  [Translated text](#translated-text)), and otherwise use the invariant form (`1.5`) and the ISO forms.
  `<input type="number">`, `"date"`, `"time"` and `"datetime-local"` take only those forms and show them in the
  browser's language: give a field bound to one of them `locale: null`, which keeps those forms whatever the window's
  locale (`bindField` does that for such an input), and bind a field that follows the locale to a text input
  (`<input inputmode="decimal">` for numbers).
- `scale` limits the digits after the decimal separator of a `number` or `decimal` value: of the number the text
  means, so `1.50e2` has none and `1e-7` has 7. A `decimal` keeps its trailing zeros (`1.50` has 2); a `number` has
  none.
- `parse(text, previous)` and `format(value)` replace the kind's text form, such as for a percentage or a date with
  the month's name. A `parse` that throws keeps the text as the draft with an `invalid-text` `FieldError` carrying
  the thrown message. The controller's `format(value)` returns the text of a value as the field shows it.

```ts
const price = createFieldController(OrderFields.price, { locale: navigator.languages, scale: 2 });
price.inputText("1.234,50"); // in de-DE writes "1234.50"
```

Receipts of checked writes, also when `write` of a text field calls one:

| Receipt | Field |
| --- | --- |
| `applied` | Success. The receipt's `validation`, if any, is `errors` when the descriptor has no `errors`. |
| `rejected` | The draft stays; `error` is a `FieldError` `rejected` with the setter's message. The next input writes again. |
| `conflict` | The draft stays; `conflict` and `error.current` are the producer's field, `error` is a `FieldError` `conflict`. Nothing is written until `retry()` writes the draft over the producer's value or `reset()` takes it. A conflict whose incoming value is the value sent is a success. |
| `committed-with-error` | The value was committed and the draft is dropped; `error` is a `FieldError` `committed-with-error`. |

#### Translating field errors

`FieldError.message` is English. An `invalid-text` error of the kinds' parsers also has a `reason` and `arguments`,
so an application can show its own text (`FieldErrorReason`, open):

| `reason` | English `message` | `arguments` |
| --- | --- | --- |
| `required` | `Enter a number.` (`a whole number`, `a date`, `a time`, `a date and time`) | `expected`: `number`, `whole-number`, `date`, `time` or `date-time` |
| `not-a-number` | `1e is not a number.` | `text` |
| `not-a-whole-number` | `1.5 is not a whole number.` | `text` |
| `too-many-fraction-digits` | `1.255 has more than 2 digits after the decimal separator.` | `text`, `scale` |
| `out-of-range` | `79228162514264337593543950336 is too large.` (a `decimal` of 2⁹⁶ or more) | `text` |
| `not-a-boolean` | `yes is not true or false.` | `text` |
| `not-a-choice` | `Large is not one of Small, Medium.` | `text`, `choices` |
| `not-a-date` | `soon is not a date (DD.MM.YYYY or YYYY-MM-DD).`, `2026-02-31 is not a date.` | `text`, `patterns` (form errors only) |
| `not-a-time` | `soon is not a time (hh:mm or hh:mm:ss).`, `25:00 is not a time.` | `text`, `patterns` (form errors only) |
| `not-a-date-time` | `soon is not a date and time (YYYY-MM-DDThh:mm).` | `text`, `patterns` |
| `offset-out-of-range` | `2026-11-04T10:00+15:00 has an offset beyond ±14:00.` | `text` |
| `no-text-form` | `This field has no text form.` | none |

`text` is the trimmed text the user typed, and `patterns` the forms a text may take, the locale's first. A `number`
field with `integer` (an `int` member) reports `required` with `expected: whole-number`, so a translated text can say
"a whole number"; its English `message` stays `Enter a number.`. A field's own `parse` that throws, and the `rejected`,
`conflict` and `committed-with-error` receipts, have no `reason`: `kind` says what happened, and `message` is the parse
error's or the producer's text. A `FieldError` of an older copy of this package has no `arguments` and may have no
`reason` either; the hook then receives empty `arguments`, and its `undefined` `reason` falls back to the message.

Give the page your text once with `configureFieldErrorText`. It receives the error and the locale text references are
shown in (`textLocale()`, the window's locale when the application sets one) and returns the text, or `undefined` for
the English message. `resolveText(error)`, the framework packages' `useText` (`useText(name.error)` in React,
`useText(() => name.error)` in Vue and Svelte), `injectText` and `{{ name.error() | runicText }}` (Angular), `texts`
(Effect) and `describeBridgeError` then show it, and show it again when the language changes. With the generated ESM
of Runic Translations, with one message per reason in your catalog:

```ts
import { configureFieldErrorText } from "@runic-artifex/views";
import { m } from "virtual:runic-translations/app/messages";

configureFieldErrorText((error, locale) => {
  // `error.arguments` is always an object; `error.reason` can be undefined (an error of an older copy, or a kind
  // without a reason), and the recipe then returns undefined for the English message.
  const { text = "", scale = 0, expected = "number", patterns = [] } = error.arguments;
  const options = locale === undefined ? {} : { locale };
  switch (error.reason) {
    case "required": return m.field_required({ expected }, options);           // .match {$expected :string}
    case "not-a-number": return m.field_not_a_number({ text }, options);
    case "too-many-fraction-digits": return m.field_too_many_fraction_digits({ text, scale }, options);
    case "not-a-date": return m.field_not_a_date({ text, patterns }, options);              // a list argument
    case undefined: return error.kind === "conflict" ? m.field_conflict({}, options) : undefined;
    default: return undefined;                                                 // the English message
  }
});
```

A later call replaces an earlier one, and the returned function removes it. The hook is page-wide, like
`configureTranslatedText`; a hook that throws is reported once per error and locale (again after a new hook is
configured), and the message is shown. `fieldErrorText(error)` returns
the text without a framework.

#### Fields below a member and row cells

With a 0.16 host (`field-paths/1`), `{View}Fields` also describes the members of an object member and the cells of
the rows of a collection that .NET makes editable (`[RunicRowEditor]`):

```ts
InvoiceFields.address.street;          // a field of address's street, beside InvoiceFields.address itself
InvoiceFields.lines.at(line.id).quantity; // a cell: the quantity of the row with that key
```

They are fields like the others, with every binding: `useField(invoice, InvoiceFields.lines.at(line.id).quantity)`,
`injectField`, `bindField`, `createFieldController` and forms. `lines.at(key)` returns the same descriptors for the
same key, so a binding keeps its field across renders, also when the row moves. Each such field:

- has no setter (`checkedOnly`): it always writes with a checked write, based on the state it showed when the draft
  started, so `checked: true` is not needed;
- conflicts only when another writer changed the same cell or member meanwhile, not another cell of the row;
- reads `undefined` once its row is gone, and its write is `rejected`;
- has the `errors` and `validating` of its validation path, such as `["lines", 2, "quantity"]` for the row's current
  index, and of the values below it. `fieldErrorsAt(validation, path)` and `fieldValidatingAt(validation, path)` read
  them for any path.

Against a host without the feature, their writes reject with a `failed` `BridgeError` and send nothing. The mock
bridge accepts them when a View lists its `fieldPaths` (generated mocks do), and replaces the row unless a `rows`
editor handles it.

### Forms

`createFormController(fields, options?)` combines field controllers into one state, and its `submit` writes every
field before it runs a command, so Save never runs before the last keystroke reached .NET:

```ts
const form = createFormController({ title, body }, { dirty: () => editor.snapshot.isDirty });
form.current; // { dirty, editing, validating, pending, valid, submitting }
save.onclick = async () => {
  const result = await form.submit(() => editor.save());
  if (result.status === "field-failed") show(result.error);
};
```

- `dirty`: the `dirty` option reports unsaved changes (such as the View's `isDirty`, which a .NET `RunicEditSession`
  computes), or a field holds a draft that is not written yet, such as a `commit: "pause"` draft.
- `editing`: a field holds a draft. `validating`: a field's validation is in progress. `pending`: either, or a
  `submit` runs. `valid`: no field has `errors` or an `error`.
- `submit(command, options?)`: writes a draft that a transport failure kept again (`retry`), commits every field,
  waits for their writes and runs `command`. It does not run the command when a field's value did not reach the View
  (`{ status: "field-failed", error }`): a text that does not parse, a `rejected` or `conflict` checked write, or a
  transport failure of this submit's writes. A `committed-with-error` write reached the View and does not block, and
  neither does an earlier failure that left no draft. It resolves `{ status: "submitted", result }`; a second
  `submit` while one runs resolves `{ status: "busy" }`. A command that throws rejects.
- `waitForValidation: true` (or a number of milliseconds; `true` is 10 seconds) waits until no field is `validating`
  before the command runs, and resolves `{ status: "validating" }` without running it when a check still runs then.
  By default `submit` does not wait; .NET validates in any case.
- `commit()` writes every field; `reset()` resets every field.

React, Vue and Svelte have `useForm(fields, options?)` and Angular `injectForm(fields, options?)` over their field
handles. `commitAllFields()` writes every draft of the page that has not been sent, such as a `commit: "pause"` draft.

A departure guard in .NET reads the ViewModel, which does not include a draft the page has not written. The page
writes its drafts before each kind of departure:

| Departure | Drafts are written |
| --- | --- |
| A command the page sends through `submit` | before the command (`submit` commits every field first) |
| A navigation or close that .NET starts (a menu, a shortcut, the model, `RequestCloseAsync`, the close button of a window with `GuardClose`) | before the guards are asked: the host pushes `flush` (`field-flush/1`, a 0.16 host and page) and waits up to 1 second for the page's `flush-reply` |
| A key with Ctrl, Cmd or Alt that types no character (not AltGr or macOS Option typing `@`, `€`, `{`), and the window losing focus (a native menu opens) | at once, also with hosts and pages without `field-flush/1` |
| The application closes the window | before `closing-reply` (`presented/1`), within 2 seconds; this is after the close was decided, so it reaches the model, not a close guard |

A page that starts a departure with a plain command (not `submit`) calls `await commitAllFields()` first. Fields
are held weakly: a controller that a page drops without `dispose()` stops being written once it is collected.

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

## Blobs and uploads

A .NET `RunicBlob` reaches the page as a `RunicBlob`, `{url, type, size, name}`:
binary content that the window's host serves at `url` (Views feature
`blobs/1`). Use the URL as it is; the bytes never cross the Bridge.

```tsx
{state.avatar && <img src={state.avatar.url} alt="" />}
<a href={report.url} download={report.name ?? undefined}>Download</a>
```

The URL works on this window only and stops working when the ViewModel replaces
the blob, when the View is released or when the window closes.

A file goes the other way as an upload. `upload(file, { signal, onProgress })`
streams it over HTTP to the window's host and resolves with a `BlobRef`, which
a command whose .NET parameter is a `RunicUpload` takes. A generated client
whose contract takes one has the same method as `client.upload`:

```ts
const ref = await client.upload(input.files![0]!, {
  signal: controller.signal,
  onProgress: ({ loaded, total }) => (progress.value = loaded / total),
});
await client.attach(ref);
```

It rejects with a `BridgeError`: `unavailable` when the host takes no uploads,
`rejected` with reason `too-large` or `capacity` at the window's limits, and
`failed` when the request fails; and with `signal.reason` when it aborts. A
reference is valid on the connection that uploaded it until a command claims
it, for at most 10 minutes. Uploads use `XMLHttpRequest`, which reports upload
progress, or `fetch` where it is missing. On CS-WebUI the host's endpoint is
another origin than the page, so a page `Content-Security-Policy` must allow
`http://localhost:*` for `img-src`, `media-src` and `connect-src`.

In tests, the mock Bridge grants uploads and keeps them: `bridge.uploads`,
`bridge.uploadContent(ref)` and `bridge.failNextUpload({ status: 413 })`;
`createMockBridge({ uploads: false })` takes none. `mockBlob(content, { type,
name })` from `@runic-artifex/views/mock` makes a `RunicBlob` for a mock's
state or result whose URL loads in a test.

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

A View with a query collection takes its rows in `queries`, which the mock filters, sorts and serves by range, with
optional latency (virtual time with `manual` scheduling). `queries.{name}` of the mock shows what the page loaded:

```ts
const catalog = mockCatalog(bridge, {
  state: { title: "catalog" },
  queries: { rows: { rows: rows(100_000), latency: 30, custom: { even: row => row.id % 2 === 0 } } },
});
// … scroll the page …
assert.ok(catalog.queries.rows.loads.every(load => load.count <= 60));
catalog.queries.rows.setRows(changed); // as Refresh() in .NET: a new version, which the page loads again
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
- `regions`: for a region collection (since 0.16), a handler by wire name that
  stands in for the departure guards. It receives the decoded state and
  `{ action, key, index }` and returns `{ outcome, state }`, both optional.
  Without a handler, or with a `Committed` outcome, the mock applies the select
  or close as .NET does; another outcome, such as
  `{ kind: "Rejected", rejection: "Guard", arrival: "None" }`, changes only
  `state`. A key that no region has rejects with `unresolved-key`, and a close
  of a collection with `PageCanClose = false` with `unavailable`. In `state`, a
  region collection is `{ regions: [{ key, content }], selected }` with each
  `content` another mock's `reference` or `null`.

The returned mock has `state`, `update(changes)` (pushes a full state like a
.NET publication), `renew()` and `waitPresented(timeout?)` (below), `calls`, `failNext(method, { kind, message, detail, reason })` or
`failNext(method, { kind: "domain-failed", failure })` for a method's declared failure,
`pushFailure({ message })` (a failure notice: the client keeps its last state
and reports the error), `collections.<field>` with `add`, `remove`, `replace`
and `move` (each pushes a frame; `batch(edit)` combines edits into one),
`interactions.<name>.request(input)`, which resolves with the answer of the
open presentation's handler, or `unhandled` when none is registered, and
`requests.<name>.raise(input)`, which raises a [view request](#view-requests)
as the ViewModel's `Raise(input)` does and returns how many of the View's open
presentations it reached (0 when none handles it; it is not kept), with
`requests.<name>.handled`. Raised inside a command handler or `batch`, the
request follows their state changes. The mock runs no delivery rule: every
presentation of the route that registered the request receives it.

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

Presentation epochs and window close have test counterparts of their .NET API:

- `mock.renew()` (on a typed mock, or `raw.renew()`) renews the route as
  `RunicPresentation.Renew` does: the next publication is a full state with a
  new epoch, also when nothing changed, and a scoped command the page sent for
  the earlier epoch answers `superseded`. A command handler that calls it
  publishes the renewal with its own changes; `mock.raw.epoch` is the current
  epoch.
- `mock.waitPresented(timeout?)` asks the page to report that it painted the
  route, as `RunicPresentation.WaitForPresentedAsync` does, and resolves `true`
  once it did, or `false` after `timeout` milliseconds (default 5000, virtual
  time with `scheduling: "manual"`).
- `bridge.closeWindow(timeout?)` closes the window as the application does:
  the page acknowledges and reports `closed`. It resolves with whether the page
  acknowledged within `timeout` (default 5000), and keeps the connection open:
  `disconnect()` then ends it as a host does.

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
- With `presentation-epoch/1`, `open` and `snapshot` replies and `state` pushes
  carry the route's epoch, and every `command` that carries an epoch is checked,
  whatever the contract marks scoped; a stale one answers `superseded` before
  availability is checked.
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

## Translated text

Text that a ViewModel produces as a Runic Translations text reference, such as a
status line (a `TextReference` member) or a validation error's `text`, shows in
the window's language (`translation-text/1`). Connect the page's Translations
runtime once; the window's locale, which the application sets with
`IRunicApplicationLocale` for every window or `IRunicWindowLocale` for one, is
then passed to its `setLocale`:

```ts
import { configureTranslatedText, resolveText, subscribeText, syncDocumentLocale } from "@runic-artifex/views";
import { m } from "virtual:runic-translations/app/messages";
import { configureLocaleResolver, createLocaleSource, localeDirection } from "virtual:runic-translations/app/runtime";
import { resolveTextReference } from "virtual:runic-translations/app/transport";

const locale = createLocaleSource({ initialLocale: navigator.language });
configureLocaleResolver(locale.getLocale);
configureTranslatedText({
  ...locale,
  resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
  direction: localeDirection,
});
syncDocumentLocale();

status.textContent = resolveText(editor.snapshot.status);
subscribeText(() => { status.textContent = resolveText(editor.snapshot.status); });
```

`resolveText(value, fallback)` formats a reference in the window's language, or
shows its `fallbackText`, then `fallback`, then its key; a string shows as it
is. For a validation error: `resolveText(error.text, error.message)`; for a
field's `FieldError`, `resolveText(error)` with the text of
`configureFieldErrorText` ([Translating field errors](#translating-field-errors)). Field
errors already show translated messages and update on a language switch,
and number, date and time fields without their own `locale` read and show their
text in the window's locale, except while the user edits a draft; the wire value
stays the invariant or ISO form. Bind such a field to a text input, or give it
`locale: null` for `<input type="number">` or `type="date"`. A language switch on the
page calls the application's command; a locale the page sets itself is set back
by the next `locale` push. A window locale the runtime refuses, such as `de-CH`
for a catalog with only `de` under the `Exact` policy, sets its closest parent
(`de`) before an error is reported; a parent in another script is skipped, so
`zh-Hant-TW` never falls back to `zh` (Simplified) nor `sr-Latn` to `sr`
(Cyrillic). Under server rendering, configure the runtime in the
browser only, or resolve with the request's locale.
`windowLocale()` and `onWindowLocale` read the window's locale; `textLocale()`
is the locale text is shown in, and `textDirection()` its writing direction
(the runtime's `direction`, else the locale's script). `syncDocumentLocale()`
sets `lang` and `dir` of `<html>` to them, now and after each switch, and leaves
the document's own `lang` while there is no locale yet. The framework
packages wrap this as `useText` (React, Vue, Svelte), `injectText` and the
`runicText` pipe (Angular) and `texts` (Effect). A template that calls the
page's own `m.*()` renders again on a switch when the locale resolver reads
the framework's text locale: `useTextLocale` (React, Vue, Svelte),
`textLocaleSignal` (Angular) or the `textLocales()` stream (Effect). The mock bridge's
`setWindowLocale(locale)` pushes a locale in page tests. Every `hello` also
reports the browser's `navigator.languages` (`client-languages/1`, a .NET host
of 0.17 or later), so the application can choose the window's language from
`RunicConnection.Languages`; the mock bridge's `clientLanguages` shows what the
page reported, in BCP 47 case as a producer keeps it. The list is personal data
that can help fingerprint the browser, and a remote CS-WebUI server receives it
too: to keep it on the page, call `configureClientLanguages({ send: false })` in
`main.ts` before the page connects, and every `hello` sends `languages: []`. An
application negotiates the tags against the locales it ships; it never assigns
one directly. See
[Translated text](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#translated-text) for the .NET side.

## Appearance

`appearance()` is the page's appearance, and `onAppearance(listener)` calls the
listener with it each time it changes, until the returned function is called:

| Member | From |
| --- | --- |
| `colorScheme` | The engine's `prefers-color-scheme`, which follows a window's own scheme; without it, the host's |
| `contrast` | `"no-preference"`, `"more"` or `"forced-colors"`: the latest change of the engine's `forced-colors`/`prefers-contrast` and the host's (below) |
| `reducedMotion` | `true` for less motion: the latest change of the engine's `prefers-reduced-motion` and the host's |
| `textScale` | The host: the desktop's text size as a factor (1.5 for half as large again) |
| `accentColor` | The host: the desktop's accent colour, `#rrggbb` |
| `systemColors` | The host: while forced colours are on (a Windows contrast theme), the palette, named after the CSS system colours (`canvas`, `canvasText`, `linkText`, `grayText`, `highlight`, `highlightText`, `buttonFace`, `buttonText`) |
| `engine` | The engine's media queries alone, `{ colorScheme, contrast, reducedMotion }`: what the page's CSS sees |
| `host` | What the host pushed, or `null` |

A member is `null` when neither source knows it, and every member is `null`
under server rendering (`unknownAppearance`). The host's values come from the
`appearance/1` feature: `RunicDesktopHost` reads the desktop's settings
(`IDesktopSettings`) and pushes them to the pages of every window that only
this computer can reach, also when they change. A serve-only window, a host on
every network interface (`DesktopHostOptions.NetworkExposure`) and CS-WebUI
push no values, so `host` stays `null` or has every member `null`.

`contrast` and `reducedMotion` start from the engine's value, which is what the
page's CSS sees. When the host pushes a change of one of them, the host's value
applies until the engine's media query changes, so the store follows the
desktop also where the engine does not while the page is open. Read `engine` to
style what the CSS sees, for example to skip your own high-contrast styles while
`engine.contrast` is `"forced-colors"`, and `host` for what the desktop asks for:

| Engine | The engine's values while the page is open | `contrast` and `reducedMotion` |
| --- | --- | --- |
| Windows, WebView2 | Read when the window opens; an open page may not follow "Animation effects" | Follow the host's changes |
| macOS, WKWebView | Follow "Increase contrast" and "Reduce motion" | The engine's, also after the host's push of the same change |
| Linux, WebKitGTK 4.1 (GTK 3) | Follow GTK's settings | The engine's, also after the host's push of the same change |
| Linux, WebKitGTK 6.0 (GTK 4) | Read when the process's first web view starts | Follow the host's changes |
| Installed browsers, serve-only | The browser's own | The engine's; the host pushes no values |

See [what each engine reports](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md#contrast-forced-colors-and-reduced-motion)
for the media queries themselves. The page runtime keeps only the host's raw
push; a page bundles the store only when it imports `appearance`,
`onAppearance` or a framework hook.

```ts
import { onAppearance } from "@runic-artifex/views";

onAppearance(look => {
  document.documentElement.style.fontSize = `${look.textScale ?? 1}rem`;
  if (look.accentColor) document.documentElement.style.setProperty("--accent", look.accentColor);
});
```

The framework packages wrap it as `useAppearance` (React, Vue, Svelte),
`injectAppearance` (Angular) and the `appearances()` stream (Effect). The mock
bridge's `setAppearance({ textScale: 1.5, contrast: "more" })` pushes an
appearance in page tests, as `WindowContentSession.Appearance` does in .NET
tests.

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
| `closed` | The application closed this window ([Presented states and window close](#presented-states-and-window-close); a .NET host of 0.15 or later). The connection drop that follows keeps it `closed`, not `reconnecting` or `lost`; a new connection reports `connected` again. Added in 0.15: an exhaustive `switch` or `Record<BridgeConnectionState, …>` needs the new case. |

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
