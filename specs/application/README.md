# Runic Application Views protocol

This is the Runic Application Views protocol, version 4, between a **producer** (the backend that owns a window's
Views and their state) and a **client** (the page runtime that shows them). It is a message protocol: the client
sends requests and receives one reply per request, and the producer pushes messages to the client.

Runic ships and supports one producer, the generated .NET bridges of `Runic.Application.Views`, and one client,
`@runic-artifex/views` with the generated TypeScript clients. The protocol is specified so that other implementations
are possible, in any language; this document does not claim that any exist. The specification is normative, and the
[conformance corpus](conformance/README.md) lets an implementation check itself. How Runic's implementations map the
protocol to .NET and TypeScript is in the [language mappings](language-mappings.md).

| Document | Content |
| --- | --- |
| This document | Conventions, transport, host bridge, handshake and features, requests and replies, state and revisions, publication, edits, checked writes, commands and presentation epochs, content and region collections, query collections, presentations, presented states and window close, operations, interactions, view requests, hosted regions, versions |
| [state-changes.md](state-changes.md) | Frames and their changes (`set`, keyed collections, text, text splices), producing and applying them, delivery bounds and recovery |
| [values.md](values.md) | The JSON profile, wire value types and the canonical form (RFC 8785) |
| [language-mappings.md](language-mappings.md) | The .NET producer and the TypeScript client |
| [conformance/](conformance/README.md) | Vectors and scenarios |
| [collection-deltas.md](collection-deltas.md) | Protocol 3's collection delta frames, kept with their fixtures while the .NET state tracker's tests read them |

The key words MUST, MUST NOT, SHOULD and MAY are used as in RFC 2119. The independent MessageFormat 2 specification is
maintained by [Runic Translations](https://github.com/Runic-Artifex/runic-translations-sdk).

## Conventions

- **Messages.** Every message is one JSON object of the [JSON profile](values.md#json-profile): I-JSON, UTF-8,
  nesting depth at most 64. Requests and pushes have a `kind`. Replies have `ok`.
- **Integers.** Revisions, versions, edit numbers, cursors, sequences, generations and epochs are non-negative safe
  integers, from 0 to 2^53−1, unless stated otherwise. A message with an integer outside its range is invalid.
- **Tokens.** `kind`, `reason` and other enumerated values are kebab-case tokens.
- **Identifiers.** Sessions, presentation ids, operation ids and request ids are strings of 1 to 128 characters from
  U+0021 to U+007E. Clients generate them; a random UUID is a good choice.
- **Input limit.** Each window has an input limit, counted in UTF-8 bytes of a request: 1 MiB (1,048,576 bytes) by
  default, configurable from 1 KiB to 64 MiB. A larger request is answered with `rejected` and the reason
  `invalid-input`, and no part of it reaches the model.
- **Unknown members.** Both sides ignore members of a message that they do not know. See
  [Compatibility](#versions-and-compatibility) for unknown kinds.
- **Window.** A window is the unit that owns a set of routes and one revision counter, and that serializes the work
  that changes its state. Each connection belongs to one window.

## Transport

A transport carries the protocol between one window of the producer and the pages connected to it. Each page has one
connection at a time. A transport MUST provide:

1. **Request and reply** on each connection. A request is one message; its reply is one message.
2. **Pushes** to every connection of the window (broadcast) and to one connection (targeted).
3. **Order.** The replies and pushes of one connection arrive in the order the producer sent them.
4. **Trusted identities.** The transport supplies the identity of the client (the browser or process) and of the
   connection. A producer MUST take them from the transport, never from a message.
5. **Admission order.** A producer SHOULD admit the requests of one connection in the order they were sent. The
   protocol does not depend on it: edits carry numbers ([Edits](#edits)), and other requests are independent.

A transport that cannot push to one connection outside a request offers a mailbox instead: the producer queues the
connection's targeted pushes, and the host script drains them with `receive` requests
([receive](#receive)). Broadcast pushes still travel on the broadcast channel, and the order of broadcast and mailbox
pushes relative to each other is not guaranteed. No rule of the protocol depends on it: operation and interaction
messages carry their own revision or identity.

A connection ends when the transport reports it closed. Its session, presentations, watches and edit numbers end with
it. Operations do not: they belong to the window.

### Host bridge (page)

In a web page, the host's script installs the transport as `globalThis.__runicBridge`, an object with this shape:

```ts
interface RunicBridgeClient {
  isConnected(): boolean;
  request(message: string): Promise<string>;
  onMessage(listener: (message: string) => void): () => void;
  onConnectionChange(listener: (connected: boolean) => void): () => void;
}
```

| Member | Contract |
| --- | --- |
| `isConnected()` | Whether the host connection is open and authenticated. |
| `request(message)` | Sends one request as JSON text and resolves with the reply as JSON text. It rejects when the transport fails before a reply arrives. |
| `onMessage(listener)` | Calls `listener` with the JSON text of each push, in order, until the returned function is called. Pushes received while no listener exists are dropped. |
| `onConnectionChange(listener)` | Calls `listener(connected)` at once and after each change, until the returned function is called. A host reports `false` before the `true` of a new connection, so each `true` after `false` is a new connection, which needs its own `hello`. It must not have the side effects that `isConnected()` may have (CS-WebUI's allows navigation). |

- Messages cross the host bridge as UTF-8 JSON text. The host script does not parse them, except that a mailbox host
  script reads the `messages` of a `receive` reply.
- The host resolves replies and calls push listeners in the order the messages arrived. Because a promise resolves in
  a later microtask, a client MUST NOT assume that it handles a reply before the pushes that followed it; the
  [revision rule](#state-and-revisions) makes the order irrelevant.
- A reply that is not a JSON object of the profile, including an empty string, is an invalid response: the client
  rejects the call with `failed`.
- A protocol-4 host script also keeps a `call(name)` member for pages of protocol 3. It answers a name that ends in
  `Snapshot` with `{"ok":true,"state":{"revision":0},"error":null,"protocol":4}` and every other name with
  `{"ok":false,"state":null,"error":{"kind":"released","message":"This View was released."}}`, so a cached protocol-3
  page reports its protocol diagnostic. The first `call` of a page SHOULD also write one warning to the page console
  that names the call and points to `request`, since code written for protocol 3 otherwise sees only released Views.
- A client that finds a `__runicBridge` without `request` reports that the host implements protocol 3 or earlier
  (see [Handshake](#handshake)).
- Each `onMessage` and `onConnectionChange` registration is its own: a function registered twice is called twice, and
  each returned function removes only its own registration. A listener never receives the same connection state twice
  in a row.

#### Mailbox host scripts

A host script whose transport has a [mailbox](#receive) runs the connection's `receive` loop itself:

- While at least one `onMessage` listener exists and the connection is open, it keeps exactly one `receive` pending on
  the connection. It delivers the `messages` of each reply to the listeners in order, as the JSON text of each
  message, and then sends the next `receive` at once. Pushes of a reply that arrives after its connection was replaced,
  and pushes while no listener exists, are dropped.
- The loop starts when the first `onMessage` listener registers on an open connection, when a new connection opens,
  and when a request of the page on the connection settles. A request does not start it when it is sent, so the loop
  does not race the connection's `hello`.
- A `receive` that fails, because the connection has not completed its `hello` (`no-hello`), because of another failure
  or because its reply is not a `receive` reply, pauses the loop until a request of the page on the same connection
  settles after that `receive` was sent; then the loop resumes. A loop therefore never spins on failures.
- A `no-hello` after the connection's loop already received a successful reply means that the transport replaced the
  connection without the host script seeing it close. The host script reports the connection lost (`false`, and
  rejects the connection's unsettled requests) and then the new connection (`true`), whose loop starts paused.
- On a connection that uses [`request-session/1`](#request-sessions), its `receive` names the session of the `hello`
  the page sent on the connection, which the host script reads from that request, and names none before it. A new
  page that got the key of a connection whose close the producer has not learned yet therefore never receives that
  connection's pushes.

#### Shared globals

One page can load several copies of `@runic-artifex/views`, for example from two bundles, and the development client
of `@runic-artifex/vite-plugin-runic` reads its diagnostics. They find each other through `__runicBridge` and through
these registered symbols (`Symbol.for`), so the keys and their shapes are part of the page contract of the TypeScript
client. Every key starts with `runic.views.`. A release may add a key; renaming one or changing its shape is breaking.
Another client implementation for the web SHOULD use the same keys.

| Key | Set on | Contract |
| --- | --- | --- |
| `runic.views.generated-client-runtime` | `globalThis` | The page's runtime of generated clients for the current `__runicBridge`: its connection, routes, presentations and operations. It is the only `onMessage` listener and dispatches pushes by `route`. |
| `runic.views.diagnostics` | `globalThis` | A `Set` of listener functions. Each is called with a `BridgeDiagnostic`: `kind` (`error` or `operation`), `code`, `message`, and optionally `route`, `detail`, `member`, `requestId` and, for local observers only, `error`. `code` is an open set; a listener ignores codes and fields it does not know. |
| `runic.views.connection` | `globalThis` | `{ listeners, state, requested, start, closed, closedListeners }`: a `Set` of listener functions called with the page's `BridgeConnectionState` on each change, and the latest state. `closed` is `true` from a [`closing`](#window-close) push until the next connection, and `closedListeners` is a `Set` of functions called with each change of it, through which every copy reports the state `closed`. The first View that connects starts it. A copy of `@runic-artifex/views` sets `start`, a function that starts it, when it loads; a reader that wants the state before a View connects (the Vite development client) sets `requested` to `true`, which a copy that loads later honours, and calls `start` if it is set. |
| `runic.views.inspector` | `globalThis` | `{ listeners, requested }`: the development state inspector. A reader (the Vite development client) adds a listener function to the `Set` and sets `requested` to `true`; only then does each copy of `@runic-artifex/views` call the listeners, with `{ kind: "state", route, revision, epoch, presentations, state }` when the state its presentations receive changes, `{ kind: "closed", route }` when a route closes, and `{ kind: "superseded", route, member, epoch, revision }` when the producer answered a `command` `superseded`. `state` is the exposed state; a listener must not change it. A listener ignores kinds and fields it does not know. Since 0.15. |
| `runic.views.diagnosticReported` | An error | `true` once a diagnostic reported the error, so each error is reported once. |
| `runic.views.unavailableReported` | `globalThis` | `true` once the page reported that it has no `__runicBridge`. |
| `runic.views.BridgeError`, `runic.views.BridgeOperationUncertainError` | An error | `true` on errors of that class, so `instanceof` holds across copies. |
| `runic.views.BridgeOutcome` | An outcome | A non-enumerable `true` that `isBridgeOutcome` checks. |
| `runic.views.mockFailure` | An error | The declared failure of an error from `mockFailure()` (`@runic-artifex/views/mock`). |

## Handshake

The first request on each connection is `hello`:

```json
{"kind":"hello","protocol":4,"session":"6f1c2a9e-0d4b-4c51-9a83-5d2f7e1b0c44","client":{"name":"@runic-artifex/views","version":"0.15.0"},"features":["text-splice/1","presentation-epoch/1","presented/1"]}
{"ok":true,"protocol":4,"implementation":{"name":"Runic.Application.Views","version":"0.15.0"},"features":["text-splice/1","presentation-epoch/1","presented/1"]}
```

| Member | Contract |
| --- | --- |
| `protocol` | The protocol version the sender implements: `4`. |
| `session` | An identifier the client chooses for this connection. A client MUST send a new session for each connection. |
| `client`, `implementation` | `{name, version}`: the name of the client or producer implementation (1 to 128 characters) and its version (1 to 64 characters, SemVer without build metadata recommended). They are informational. |
| `features` | Optional: the [features](#features) the sender implements, in the request and in a successful reply. Absent means none. |
| `languages` | Optional, in the request, with [`client-languages/1`](#client-languages): the page's preferred languages. |

Producer rules:

- Every reply to a `hello`, successful or not, carries the producer's `protocol` and `implementation`.
- A `hello` whose `protocol` is not 4 is answered
  `{"ok":false,"protocol":4,"implementation":{…},"error":{"kind":"failed","reason":"protocol-mismatch","message":"…"}}`.
  The producer logs this once per connection, naming both versions.
- A `hello` without a valid `protocol`, `session` or `client`, or whose `features` is present but not an array of at
  most 64 strings of at most 64 characters each, is `rejected` with `invalid-request`. So is a second `hello` on a
  connection that already completed one, except a `hello` with another session on a connection that uses
  [`request-session/1`](#request-sessions), which replaces the connection.
- A `hello` whose session a closed connection used is `released`.
- A `hello` whose session another open connection uses is `rejected` with `invalid-request`.
- A successful reply lists every feature the producer implements, whether or not the request listed it.
- **Requests before the `hello`.** A request other than `hello` and `receive` that arrives on a connection whose
  `hello` has not succeeded is held. When a `hello` of the connection succeeds, the producer admits the held requests in
  the order they arrived, before any request that arrives after the `hello`. A held request is answered `rejected` with
  the reason `no-hello` when a `hello` of the connection fails, when the connection closes, or 5 seconds after it arrived
  without a successful `hello`. At most 64 requests are held per connection; another one is `rejected` with `capacity` at
  once. A transport that does not keep the order of a connection's requests can deliver a request sent after the
  `hello` before it; this rule makes that order irrelevant.
- A `receive` before a successful `hello` is answered `no-hello` at once and is never held: mailbox host scripts read
  that reply as a replaced connection ([Mailbox host scripts](#mailbox-host-scripts)).
- Until a `hello` succeeds, the connection receives no pushes.

Client rules:

- It sends `hello` before any other request on a connection, and again after each new connection. It MAY send other
  requests right after the `hello`, without waiting for its reply, as `@runic-artifex/views` does with the `open` of
  each route it shows.
- A request answered `no-hello` while the connection's `hello` succeeded, before or after that reply arrived, did not
  run: the client sends it once more after the `hello`. A producer that does not hold requests, or one whose hold ended,
  answers so. A `no-hello` to a request sent after the `hello`'s successful reply arrived means that the producer lost
  the connection's `hello`.
- It refuses a producer whose reply has a `protocol` other than its own, or a successful reply without `protocol`:
  every pending and later call of that connection, including the requests sent with the `hello`, rejects with one
  `failed` error whose message names both versions and the fix (use the producer and client of one release, rebuild so
  the generated clients are regenerated, and reload). It reports this with one diagnostic with code `protocol`, once per
  page, and with no other diagnostic, and sends no request after the reply.
- A client MAY compare `implementation.version` with its own release when `implementation.name` names the producer it
  is released with. `@runic-artifex/views` reports a diagnostic with code `version` when `implementation.name` is
  `Runic.Application.Views` and the version differs from its own, once per page, and keeps working. It reports no
  version diagnostic for other producers.

## Features

A feature is an addition to the protocol that a client and a producer use only when both implement it. Each side lists
the features it implements in `hello`; a feature is **in use** on a connection when the request and the successful
reply both list the same string. Features are negotiated per connection, and again on each new connection.

- **Names.** A feature is named `{name}/{version}`: `name` is a kebab-case token of 1 to 48 characters and `version` a
  positive integer without leading zeros, such as `text-splice/1`. `text-splice/1` and `text-splice/2` are different
  features; a feature's specification says which version is used when both sides list several.
- **Unknown features.** A side ignores a string it does not implement, including one that is not a feature name.
- **Messages.** A side sends the members, change kinds, request members and message kinds of a feature only on a
  connection that uses it. A side that receives them on another connection applies the base rules: unknown members are
  ignored, a client recovers from an unknown change kind and ignores an unknown push kind, and a request of a kind the
  connection's features do not define, or that misses the members the base protocol requires, is `invalid-request`.
- **Members of broadcasts.** One exception: a member that a feature adds to a push sent to every connection, and that
  a receiver without the feature ignores as an unknown member, is sent while at least one connection of the window
  that completed its `hello` uses the feature. It is then sent to every connection, since a broadcast has one
  encoding. `epoch` in `state` ([presentation epochs](#presentation-epochs)) is the only such member. A second
  exception: a client that implements [`request-session/1`](#request-sessions) sends `session` with every request,
  also before the `hello`'s reply tells it whether the feature is in use; a producer without the feature ignores it.
- **Members of state values.** A member that a feature adds to a value of the route's state is part of that value
  whatever the connections' features: the state is one value per route, which the producer compares across
  publications. A reader without the feature ignores it as a member its type does not define
  ([values](values.md#composite-values)). `pending` and the errors' `text` in [`validation`](#validation) are the only
  such members.
- A feature never changes the meaning of a message of the base protocol, so a client and a producer of one protocol
  version always connect, whatever features they implement.

| Feature | Specification | Since |
| --- | --- | --- |
| `text-splice/1` | Text members change with splices: [`set` with a splice](#text-splices-in-set), [`write` with a splice](#text-splices-in-write) and [`splice` changes](state-changes.md#text-splices) | 0.14 |
| `presentation-epoch/1` | A command runs only on the presentation it was issued for: [presentation epochs](#presentation-epochs) | 0.15 |
| `presented/1` | The page reports that it painted a state, and acknowledges a window close: [presented states and window close](#presented-states-and-window-close) | 0.15 |
| `view-requests/1` | The producer sends a presentation one-way requests, such as to focus a field: [view requests](#view-requests) | 0.16 |
| `request-session/1` | Every request names the session of its connection's `hello`, so another page that got the connection's transport key never acts for it: [request sessions](#request-sessions) | 0.16 |
| `region-collections/1` | The page selects and closes the regions of a region collection by key: [region collections](#region-collections) | 0.16 |
| `client-errors/1` | The page reports its uncaught errors, which the producer logs: [page errors](#page-errors) | 0.16 |
| `query-collections/1` | Each presentation reads the rows of a query collection for its own range, changes its query and finds a key: [query collections](#query-collections) | 0.16 |
| `blobs/1` | Binary content at host URLs, and files the page uploads over HTTP: [blobs and uploads](#blobs-and-uploads) | 0.16 |
| `validation-pending/1` | `validation` reports the paths whose validation is in progress: [`pending`](#pending) | 0.16 |
| `field-flush/1` | The producer asks the pages to write their field drafts before a departure is decided: [field flush](#field-flush) | 0.16 |
| `field-paths/1` | A checked write names a path below its member, such as a member of an object or a cell of a keyed collection's row: [field paths](#field-paths) | 0.16 |
| `translation-text/1` | The window's locale, and the [`text`](#text) of validation errors: [translated text](#translated-text) | 0.16 |
| `navigation-path/1` | The window's Back, and its route path kept in the page's URL: [navigation paths](#navigation-paths) | 0.16 |
| `appearance/1` | The desktop's appearance preferences that a page's engine may not expose, such as the text scale, the accent colour and the forced-colours palette: [appearance](#appearance) | 0.17 |
| `client-languages/1` | The page reports its browser's preferred languages in `hello`: [client languages](#client-languages) | 0.17 |
| `hosted-regions/1` | The page declares regions where the host shows a guest application's Window, and reports their size, visibility and focus: [hosted regions](#hosted-regions) | 0.18 |

### Request sessions

With the feature `request-session/1`, a request acts for its connection only when it names the connection's session.
The feature exists for transports that give a connection's key to a new page before they report the old connection
closed: CS-WebUI keys a connection by WebUI's client id and connection slot, WebUI frees a slot when its socket closes
and reports the close later, on another thread, and without WebUI's cookie every client id is 0. A new page, from the
same browser or another one, can therefore send requests with the key of a connection the producer still holds open,
including a `receive` that would drain that connection's mailbox.

- **Client.** It sends `session`, the session of the connection's `hello`, as a member of every other request of the
  connection, also of the requests it sends before the `hello`'s reply. The session is random for each connection
  (`@runic-artifex/views` uses `crypto.randomUUID`, or 122 random bits from `crypto.getRandomValues`), and the
  producer never sends it to any page, so another page cannot name it. A [mailbox host script](#mailbox-host-scripts)
  reads the session from the `hello` the page sends and names it in its `receive`; before that `hello`, its `receive`
  names none.
- **Producer.** On a connection whose `hello` listed the feature, a request other than `hello` that names another
  session, or none, is answered `rejected` with the reason `no-hello` and does not act for the connection: it reads
  and changes nothing, its mailbox included. A request held before the `hello` is checked when it is admitted.
- **Replacement.** A `hello` with another session on such a connection, after its `hello` succeeded, comes from a new
  page. The producer first checks it as it checks every `hello` (protocol, session, client, features, and a session
  that no open or closed connection used); a `hello` that fails is answered as a second `hello` of the old connection
  and replaces nothing. A valid one closes the connection as if the transport had reported it closed (its
  presentations, operation watches, pending interactions and mailbox are released) and is handled on a new connection
  with a new identity. The transport's next report that the key closed is the old connection's, and the producer
  ignores it. A transport whose keys never repeat, such as Runic Desktop's, gives every new page a new key, so there a
  second `hello` is `invalid-request` and nothing is replaced.
- **Without the feature** nothing changes: a second `hello` is `invalid-request`, and a request acts for the
  connection whatever it names. A page and a producer of 0.15 keep that behaviour, so on CS-WebUI a new page can still
  drain the mailbox of a 0.15 page whose close is not reported yet; both sides need the feature.
- The `no-hello` answer makes the new page's client send the request again after its own `hello` ([Handshake](#handshake)).

## Requests and replies

A request names its `kind`, and most kinds a `route` and a `member`:

```json
{"kind":"set","route":"editor","member":"title","value":"Draft","edit":7}
{"ok":true,"revision":42}
```

A successful reply is `{"ok":true, …}`. A failed reply is `{"ok":false,"error":{…}, …}`. The members of each reply are
listed with its request below and in the [catalogue](#message-catalogue).

### Errors

`error` is an object:

| Member | Contract |
| --- | --- |
| `kind` | `rejected` (the request was refused and nothing ran), `cancelled`, `failed`, `domain-failed` (a command's declared failure) or `released` (the route, presentation, session or window was released). |
| `message` | A human-readable message. Its text is the producer's, except where this document fixes it. |
| `reason` | Optional. A token that refines `kind` (table below). The vocabulary is open: a client keeps a reason it does not know and does not fail on it. |
| `failure` | `domain-failed` only: the declared failure value, encoded as the command's failure type. |
| `detail` | Development only: [failure detail](#failure-detail). |

A client reports a `kind` it does not know, including `disconnected`, as `failed`. `disconnected` is client-only: a
client reports a call that fails while the transport is not connected as `disconnected`, and a producer never sends
it.

| `reason` | `kind` | Sent when |
| --- | --- | --- |
| `invalid-request` | `rejected` | The request is not a request of this protocol: an unknown `kind`, including the kind of a feature the connection does not use, a missing member or a member of the wrong JSON type, or a `member` that the route does not have for that kind. |
| `no-hello` | `rejected` | The connection has not completed a `hello` ([Handshake](#handshake)). |
| `splice-base` | `rejected` | A `set` with a splice names a base the producer does not have, or the splice does not fit it ([Text splices in `set`](#text-splices-in-set)); a `write` with a splice does not fit the field's value at `expectedVersion`, or that value is not a string ([Text splices in `write`](#text-splices-in-write)). |
| `protocol-mismatch` | `failed` | A `hello` named another protocol version. |
| `invalid-input` | `rejected` | A value or input does not decode as its type ([values](values.md#wire-value-types)), the setter or command rejects it as invalid input, or the request is larger than the input limit. |
| `unavailable` | `rejected` | A command is not available ([Commands](#commands-and-availability)), or a region collection does not let the page close a region. |
| `unresolved-key` | `rejected` | The key of a key command resolves to nothing, or a `region` request's key names no region of the collection. |
| `busy` | `rejected` | A connection already has as many query collection requests of that kind in flight as the producer admits ([query collections](#query-collections)). Nothing ran; it ends when one completes. |
| `capacity` | `rejected` | A limit of the window or connection is reached: 64 awaited commands and `region` requests, 64 presentations per route and connection, 64 watches per connection, or 64 requests held before a connection's `hello`. |
| `state-withheld` | any | The request ran, but the producer cannot publish a state that includes its effects, because a keyed collection has invalid keys ([state changes](state-changes.md#invalid-keys)). |

A request that names a route the producer does not serve, because it was never bound or because it was released, is
answered `{"ok":false,"error":{"kind":"released","message":"This View was released."}}`. A producer cannot tell those
cases apart, and neither can the client. A client reports a `released` reply on every request kind as `released`: the
producer answered, so the transport is connected and only the route is gone.

### Revisions in replies

Every reply to a request that changes state carries `revision`: `set`, `write`, `command`, `region` and `start`, whether they
succeed or fail, as long as the request ran. The rules are in [State and revisions](#state-and-revisions). Two
exceptions:

- **Released by the call.** A request can release its own route while it runs, as a command that navigates away from
  its page does. Its reply has `released: true` and no `revision`. A call that succeeded stays `ok: true`; a failed
  call keeps its `error`. A client resolves a successful reply with `released: true` with the last state it accepted.
- **State withheld.** When the request ran but no state including its effects can be published, the reply has no
  `revision`. A call that succeeded replies `{"ok":false,"error":{"kind":"failed","reason":"state-withheld",…}}`; a
  call that failed keeps its `kind` (and `failure`) and has the reason `state-withheld`. A client MUST NOT retry such a
  call as if it had not run.

### Failure detail

In development, a producer MAY add `detail` to an error caused by an unexpected failure: `{type?, message, stack?}`.
`type` is the producer's name for the error's type, if it has one; `message` is the failure's own message; `stack` is
text that describes where it came from, such as a stack trace and its causes. A producer MUST NOT send `detail` in
production, and a `domain-failed` error never has it. When a producer is in development is its own configuration
([language mappings](language-mappings.md#net-producer)).

### Routes and members

- **Route names** are chosen by the producer. A root View's route name is a string of 1 to 128 characters from
  `[A-Za-z0-9_-]` that the generated contract names. A content route is `content{id}`, where `id` is the content's id,
  32 lowercase hexadecimal digits. Route names that start with `runic.` are reserved for host services that share the
  channel, such as the [menu bar page protocol](../desktop/menu-bar-page.md); their request kinds and pushes are
  defined by those services.
- **Member names** are the wire names of the contract: the name the member has in state (`title`), the command's wire
  name (`save`) or the checked field's name. `kind` keeps setters, commands and availability queries apart, so a
  member name can never collide with another kind of request.
- **Values** are JSON values embedded in the request (`value`, `input`), never JSON text inside a string.

## State and revisions

A route's **state** is a JSON object with one member per state member of its View, named by its wire name
([values](values.md#composite-values)):

- value members, encoded as their type;
- content slots: a content reference `{kind, id}` or `null`; content collections: arrays of content references;
  region collections: `{regions, selected}` ([region collections](#region-collections));
- keyed collections: arrays of rows; append-only text fields: strings ([state changes](state-changes.md));
- the availability members `can{Name}` and `is{Name}Executing` of commands
  ([Commands](#commands-and-availability)).

Checked fields have a **version** each, a non-negative safe integer that increases whenever the producer observes a
change of the field. States carry them beside the state as `fields: {<member>: {version}}`, and frames carry them in
`set` changes. `fields` is present when the View has checked fields.

### Revisions

- Each window has one revision counter. Each published change of any route of the window takes the next value, so a
  route's revisions only increase, including when the route is attached to a new View, but they are not contiguous.
- A **published change** is one unit of change that the producer publishes together: one `state` or `frame` push. The
  producer decides what a unit contains ([publication units](state-changes.md#publication-units)).
- **Inclusion rule.** When a reply carries `revision` R for a route, every state or frame that the producer publishes
  on that route with a revision of R or more includes the request's effects, and none below R does. A revision
  therefore identifies content: two states of a route at one revision are equal.
- Before it replies to a request that changes state, the producer publishes everything pending on the request's route,
  comparing members as for any publication ([state changes](state-changes.md#producing-frames)). A change that no
  notification announced therefore becomes a frame at a new revision. When nothing changed, `revision` is the route's
  last published revision.
- A client accepts a state or frame only when its revision is greater than the route's current revision. An equal
  revision is the same content and is ignored.

### Reply order

The reply to a request that changes state is sent after every push that the producer had to publish, before the reply
was ready, on any route of the window. A command often changes another route as well, such as its shell, so a client
cannot know which routes to wait for. The wait is bounded to 5 seconds; after that the producer sends the reply, and the
pushes follow. Replies to `open`, `snapshot`, `close`, `can`, `status`, `watch`, `unwatch`, `cancel`, `stream`,
`interactions`, `interaction-reply`, `presented`, `closing-reply`, `query-rows`, `query-index` and `client-error` do not wait.

A reply is not ordered with the pushes of requests that the producer admitted after it, nor with their replies. When a
client sends a request before the reply to its previous one arrived, as two keystrokes in a row do, the frame of the
second request, and even its reply, can arrive before the reply to the first. This holds on a transport that keeps the
order of what the producer sent too: the producer hands a reply to the transport when the reply is ready, not at a
fixed place among the connection's pushes. A client relies on revisions only, so it handles every such order
([client resolution](#client-resolution), [client overlay](#client-overlay)).

### Client resolution

A client resolves a call whose reply carries `revision` R once the call's route has accepted a revision of R or more.
Normally that has already happened when the reply arrives.

- If a recovery read of the route is in flight, the call resolves after it.
- If the route is not open, the call resolves at once.
- If the route still has not reached R 5 seconds after the reply, the client reads a `snapshot` of the route, resolves
  the call after it, and reports a diagnostic with code `revision-timeout`, because a correct producer cannot cause it.

## Publication

The producer pushes the state of its routes to every connection that completed `hello`. A client ignores pushes for
routes it has not opened, and pushes of a kind it does not know. A route's changes are published from its first state
on, which the producer takes when it starts serving the route, whether or not a client has read it.

| Push | Members | Meaning |
| --- | --- | --- |
| `state` | `route`, `revision`, `state`, `fields?`, `epoch?` | A full state. `epoch`: [presentation epochs](#presentation-epochs). |
| `frame` | `route`, `baseRevision`, `revision`, `changes` | Changes on top of the state at `baseRevision`. See [state changes](state-changes.md). |
| `failure` | `route`, `revision`, `error` | A publication was withheld because a keyed collection has invalid keys ([state changes](state-changes.md#invalid-keys)). `revision` is the producer's current revision; it does not change the client's. |

**Order of pushes.** On each connection, the producer sends the `state`, `frame` and `failure` pushes that a window
turn published before the `operation` and `interaction` pushes that the same turn caused. So the terminal status of an
operation follows the state or frame at its `revision`, and an interaction request follows the frame that shows its
command executing. A transport with a [mailbox](#receive) does not order the two channels relative to each other.

A full state is sent only when a route needs one: as the first state of a route on a connection (the `open` and
`snapshot` replies), after the delivery queue overflowed, for a [renewal](#presentation-epochs), for content whose
presentations [resume](#presentations), or when the producer chooses one, which is always allowed. Every other change
is a frame. The delivery bounds and the recovery rules are in
[state changes](state-changes.md#delivery-and-recovery).

## Edits

A setter request carries an edit number:

```json
{"kind":"set","route":"editor","member":"title","value":"Draft","edit":7}
```

- **Numbers.** `edit` is a positive safe integer that increases with each `set` the page sends for that route and
  member. A client numbers them 1, 2, 3 and so on for the life of the page, across connections.
- **Producer.** For each connection, route and member, the producer keeps the highest edit number it has admitted. A
  `set` whose `edit` is not higher is not applied: it answers `{"ok":true,"revision":R,"superseded":true}`, with R the
  route's last published revision. A `set` that is admitted raises the number whether or not the setter succeeds.
  This makes the order of keystrokes safe on any transport, at the cost of one integer per settable member and
  connection.
- **Values.** `value` is encoded as the member's type. A value that does not decode, or that the setter rejects as
  invalid input, is `rejected` with `invalid-input`. The reply of a successful `set` is `{"ok":true,"revision":R}`.
- On a connection that uses `text-splice/1`, a `set` of a [text member](state-changes.md#text-members) may carry
  `splice` instead of `value` (below). A `set` carries exactly one of them.

### Text splices in `set`

With the feature `text-splice/1`, a setter of a long text member sends what changed instead of the whole value:

```json
{"kind":"set","route":"editor","member":"body","edit":8,"splice":{"baseEdit":7,"length":100000,"index":5120,"delete":0,"text":"x"}}
{"ok":true,"revision":43}
```

| Member of `splice` | Contract |
| --- | --- |
| `baseEdit` or `baseRevision` | Exactly one: the value the splice applies to. `baseEdit` names the member's edit of that number on this connection: the value that edit carried. `baseRevision` names the member's value in the route's state at that revision. |
| `length` | The length of that value, in UTF-16 code units. |
| `index`, `delete`, `text` | The [splice](state-changes.md#text-splices): replace `delete` code units at `index` with `text`. |

**Meaning.** A `set` with a splice is exactly the `set` whose `value` is the base with the splice applied. The producer
rebuilds that value and runs the setter with it, so the reply, `revision`, `invalid-input` and every other rule of a
`set` are unchanged; the setter's last writer wins, as with a whole value. The producer's current value of the member
plays no part, only whether the producer still has the base.

**Producer.** In this order:

1. A `set` with `splice` on a connection that does not use `text-splice/1`, or for a member that is not a settable text
   member, or with a `splice` that is not an object of the members above, is `rejected` with `invalid-request`.
2. Edit numbers come first: a `set` whose `edit` is not higher than one admitted before is `superseded`, as any `set`.
3. The base: for `baseEdit`, the producer has it when it is the highest edit of the member admitted on this connection
   and that edit's value was a string; for `baseRevision`, when no publication of the route after `baseRevision`
   changed the member and `baseRevision` is not beyond the route's last publication, and the member's value then was a
   string. The splice must fit the base as a frame's splice must ([applying a splice](state-changes.md#text-splices)).
4. When the producer does not have the base, or the splice does not fit it, the `set` is `rejected` with the reason
   `splice-base` and is **not admitted**: the edit number is not raised, so an earlier edit that arrives later still
   applies.
5. Otherwise the edit is admitted, and the producer keeps the rebuilt value as the value of that edit.

A producer keeps, for each connection that uses the feature, route and text member, the value of the highest admitted
edit (one string), and for each route and text member its last published value and the revision of the publication
that last changed it.

**Client.** A client that uses `text-splice/1` sends a splice for a settable text member when the new value and the
base are strings and the [single splice](state-changes.md#producing-splices) from the base to the new value keeps at
least 64 code units of the base. The base is the value the client exposes before the edit: while the member shows an
overlay, the latest edit's value (`baseEdit`, when that edit was sent on the current connection); otherwise the
member's value in the route's state (`baseRevision`, the route's current revision, when the state was read on the
current connection). In every other case it sends `value`. When a splice is answered `splice-base`, the client sends
the same value as `value` with the member's next edit number if that edit is still the member's latest, and the
setter's call settles with that request; otherwise the call settles as a superseded one, because a later edit carries
a newer value.

### Client overlay

A client that exposes state to a user interface MUST apply this rule; a client without one MAY.

- While a member has an edit whose reply has not arrived, or whose reply revision the route has not reached yet, the
  client exposes the member's latest edit value instead of the state's.
- It exposes the state's value again once the route has reached the reply revision of the member's latest edit, or
  when the latest edit failed. A failed edit also rejects the setter's call.
- A reply marked `superseded` does not end the overlay: the latest edit is still on its way.
- Only the latest edit's reply ends it. The reply to an earlier edit resolves that edit's call and leaves the overlay in
  place, whether it arrives before or after the frame of a later edit ([reply order](#reply-order)).
- Subscribers are notified whenever the exposed value changes, including when the overlay starts or ends.
- The overlay applies to setters only. Checked writes keep their baseline semantics, and commands are not tied to a
  member.
- The overlay covers only the member that was set. Members derived from it, such as `validation` and the member's
  `{name}Errors` ([validation](#validation)), are never overlaid: they show the state's value, which describes the
  producer's value of the member until the frame of the edit arrives.
- An edit sent as a splice is overlaid like any other: the overlay exposes the value the splice produces from its base,
  which is the value the client set. Frames splice the producer's state underneath the overlay; pending splices are not
  rebased onto them.

## Checked writes

A checked field accepts writes that are checked against the value and version the page last saw:

```json
{"kind":"write","route":"editor","member":"title","requestId":"w-1","expectedVersion":3,"expectedValue":"Draft","value":"Final"}
{"ok":true,"revision":44,"receipt":{"kind":"applied","snapshot":{"value":"Final","version":4}}}
```

- The producer applies `value` only when the field's version is `expectedVersion` and its current value is
  [canonically equal](values.md#canonical-equality) to `expectedValue`.
- `receipt` is one of:
  - `{kind:"applied", snapshot:{value, version}, validation?}`: the write was applied; `snapshot` is the field after
    it, including any normalization by the setter, and `validation` is present when the field's member is invalid
    after the write, which validation does not undo: the member's messages ([validation](#validation)), separated by
    line feeds (U+000A);
  - `{kind:"committed-with-error", snapshot, message}`: the setter changed the field and then failed;
  - `{kind:"conflict", incoming:{value, version}, message}`: the field's version or value differed; `incoming` is the
    current field;
  - `{kind:"rejected", message}`: the setter refused the value, and the field is unchanged.
- A retry with the same `requestId` that repeats `expectedVersion` and either `expectedValue` and `value`, canonically
  equal to the original's, or the original's `splice` receives the original receipt and does not apply the write
  again. A retry repeats the original's form: a write with `expectedValue` and `value` does not repeat a write with a
  splice, nor the reverse. Reusing a `requestId` for another write answers `conflict`. A producer retains receipts for
  a bounded number of request ids.
- The reply carries `revision` like a setter's. A value that does not decode is `rejected` with `invalid-input`.
- On a connection that uses `text-splice/1`, a `write` of a checked field that is a
  [text member](state-changes.md#text-members) may carry `splice` instead of `expectedValue` and `value` (below).
- On a connection that uses `field-paths/1`, a `write` may carry `path`: it writes the value at that path below the
  member ([field paths](#field-paths)).

### Text splices in `write`

With the feature `text-splice/1`, a checked write of a long text member sends what changed instead of the whole value
twice:

```json
{"kind":"write","route":"editor","member":"body","requestId":"w-9","expectedVersion":7,"splice":{"length":100000,"index":5120,"delete":0,"text":"x"}}
{"ok":true,"revision":44,"receipt":{"kind":"applied","snapshot":{"value":"…","version":8}}}
```

| Member of `splice` | Contract |
| --- | --- |
| `length` | The length of the field's value at `expectedVersion`, in UTF-16 code units. |
| `index`, `delete`, `text` | The [splice](state-changes.md#text-splices): replace `delete` code units at `index` with `text`. |

A `write` carries exactly one of `value` and `splice`, and `expectedValue` only with `value`. `splice` has no
`baseEdit` or `baseRevision`: its base is the field's value at `expectedVersion`.

**Meaning.** A `write` with a splice is exactly the `write` whose `expectedValue` is the field's value at
`expectedVersion` and whose `value` is that value with the splice applied. Its receipts are those of that write:
`snapshot` and `incoming` carry the field's whole value.

**Producer.** In this order:

1. A `write` with both or neither of `value` and `splice`, with `expectedValue` and `splice`, or with a `splice` that is
   not an object of the members above, is `rejected` with `invalid-request`.
2. A `write` with `splice` on a connection that does not use `text-splice/1`, or for a checked field that is not a text
   member, is `rejected` with `invalid-request`.
3. A retained `requestId` answers as above: the original receipt for a retry of the same splice and `expectedVersion`,
   `conflict` otherwise.
4. When the field's version is not `expectedVersion`, the receipt is `conflict` with `incoming`, as for any `write`.
5. The base is the field's current value. When it is not a string, or the splice does not fit it as a frame's splice
   must ([applying a splice](state-changes.md#text-splices)), the `write` is `rejected` with the reason `splice-base`:
   nothing is written and no receipt is retained for the `requestId`.
6. Otherwise the producer writes the rebuilt value as the full `write` would, and retains its receipt for a retry of
   the splice.

**Client.** A client that uses `text-splice/1` sends a splice for a checked write when the member is a checked text
member, `expectedValue` and `value` are strings and the [single splice](state-changes.md#producing-splices) from
`expectedValue` to `value` keeps at least 64 code units of `expectedValue`; `length` is the length of `expectedValue`.
In every other case it sends `expectedValue` and `value`. When a splice is answered `splice-base`, the client sends the
same write with `expectedValue` and `value` and the same `requestId`, once; the write's call settles with that request.
A retry of a write repeats the form the client sent last.

### Field paths

With the feature `field-paths/1`, a checked write may name a **path** below its member: a member of an object that a
settable member holds, or a cell of a row of a keyed collection ([state changes](state-changes.md#keyed-collections)).
Each such path is a **field path** of the contract, which the generated code declares; a client knows them from its
generated code.

```json
{"kind":"write","route":"invoice","member":"lines","path":["7","quantity"],"requestId":"w-3","expectedVersion":41,"expectedValue":2,"value":3}
{"ok":true,"revision":44,"receipt":{"kind":"applied","snapshot":{"value":3,"version":44}}}
{"kind":"write","route":"invoice","member":"address","path":["street"],"requestId":"w-4","expectedVersion":44,"expectedValue":"","value":"Main Street 1"}
```

| Member | Contract |
| --- | --- |
| `path` | 1 to 64 nonempty strings of at most 2,048 code units. Below a keyed collection: the row's [key](state-changes.md#keyed-collections) (its normal form), then the member names of the cell in the row. Below another member: the member names in its object. |
| `expectedVersion` | The version of the field path that the write is based on (below). |
| `expectedValue`, `value` | The value at the path that the write is based on, and the new value, encoded as that value's type. |

**Versions.** A field path's version is the revision of the route's last publication that changed the value at the
path, or 0 when no publication changed it since the route's first state. A path's version is therefore never above
the route's revision, and a client that shows the route's state at revision R bases a write of any field path on
version R: the path did not change after R exactly when its version is at most R. A cell has its own version, so a
change of one cell of a row does not conflict with a write of another cell. A row that is removed and added again,
also within one publication, and an object member that becomes `null` and an object again, start at the revision
that added them. A move changes no cell.

**Row conflicts.** The application may check the rows of a collection as a whole instead (the .NET
`[RunicRowEditor(Conflicts = RunicRowConflicts.Row)]`): a write of any cell of a row is then based on the row's
version, the revision of the last publication that changed anything in the row, so a cell that another writer left
alone conflicts too when they changed the row meanwhile. The client sends the same request either way.

**Meaning.** A write with `path` is the checked write of the value at the path. When the field path's version is at
most `expectedVersion` and its current value is [canonically equal](values.md#canonical-equality) to `expectedValue`,
the producer copies the member's value (the object, or the row) with the value at the path replaced by `value`,
decoded as that value's type, and applies the copy: an object member through its setter, a row through the View's
row editor, which replaces the row with the same key. The copy keeps everything the producer holds that the wire
does not carry, such as a member the page does not see; a producer that cannot copy a value exactly declares no field
paths below it. The receipts are those of a checked write, with these differences:

- `snapshot` and `incoming` carry the value at the path, and their `version` is the reply's `revision`, at which the
  route's state shows that value; it is the version of the client's next write of the path.
- `applied` carries no `validation`: the page reads the path's messages from `validation`, whose paths name rows by
  their index ([validation](#validation)).
- When no row has the key, or an object on the path is `null`, the receipt is `rejected`. So it is when the row editor
  removed the row, or the setter cleared the object: there is no value at the path to report.
- When the setter or row editor fails, the receipt is `rejected` when the object or row is unchanged, and
  `committed-with-error`, with the value at the path, when it changed before the failure.
- A reused or expired `requestId` of a write whose row is gone is `rejected`, since `incoming` would have no value.

**Producer.** In this order:

1. A `write` with `path` and `splice`, or with a `path` that is not an array of the segments above, is `rejected` with
   `invalid-request`.
2. A `write` with `path` on a connection that does not use `field-paths/1`, or whose `path` is not a field path of the
   member, is `rejected` with `invalid-request`. A row's key member is not a field path.
3. A retained `requestId` answers with the original receipt when the retry repeats `path`, `expectedVersion`,
   `expectedValue` and `value`, canonically equal to the original's, and with `conflict` otherwise, as any `write`
   (`rejected` when the row is gone).
4. No row has the key, or an object on the path is `null`: `rejected`.
5. The field path's version (the row's, with row conflicts) is above `expectedVersion`, or its value is not
   canonically equal to `expectedValue`: `conflict`, with the value at the path in `incoming`.
6. A `value` with which the member's value does not decode is `rejected` with `invalid-input`, and no receipt is
   retained.
7. Otherwise the producer applies the value and retains the receipt.

A producer keeps, for each route and field path whose member or row it publishes, the revision of the last publication
that changed it, and a digest of the value it published, to compare the next publication with. A frame's indexed
changes carry the rows that changed, so a publication compares only those; a `set` of the collection and a full state
compare every row.

**Client.** A client that uses `field-paths/1` writes a field path with `path`, `expectedVersion` the route's revision
when the draft started (or the version of the last receipt), and `expectedValue` the value at the path in that state.
It finds a cell's row by its key, also after the row moved. Without the feature, its field paths have no write: a call
rejects without a request.

## Validation

A View whose model validates has the state member `validation`. It is derived: the producer writes it from the
model's validation at each publication and compares it like any other member
([state changes](state-changes.md#members)). A change of validation is therefore published as a `set` of
`validation` in the frame of the unit that changed it, together with the members whose change caused it. A
producer also publishes a change of validation that changes no other member and that the model announces only to
its validation, such as a rule that names no member. A View whose model reports errors per member also has, for
each value member `name`, the state member `{name}Errors`.

### `validation`

```json
{"hasErrors":true,"truncated":false,"errors":[
  {"path":[],"message":"Approval is required."},
  {"path":["profile","postal-code"],"message":"Postal code required","code":"required","severity":"error"},
  {"path":["items",0,"label"],"message":"Item label required"}]}
```

- **`errors`.** Each error has a `path`, a `message` and, when the model supplies them, a `code` and a `severity`.
  `code` and `severity` are the application's strings, open sets that the protocol does not interpret.
- **`message`.** A non-empty string. An error whose model gives no text, or an empty or blank one, has the message
  `Invalid value.`.
- **Paths.** A path is an array of segments that leads from the View's state to the value the error concerns: a
  member's wire name (a string), a list index (an integer from 0) or a map key (a string). `[]` is the View's own
  object; `["profile","postal-code"]` the member `postal-code` of the object in `profile`; `["items",0,"label"]` the
  member `label` of the first element of `items`; `["lookup","primary","label"]` that of the map entry `primary`. A
  list nested in a list adds only its index (`["groups",0,1,"label"]`). An error that the model reports for a member
  path the producer cannot resolve to a member or element of its state reports at the path of its object. An object
  that the state reaches on several paths reports its errors at each of them.
- **Each message once.** An error that the model reports for a member appears once, at the member's path, also when
  the model lists it among its object's errors as well. An error of the object, including one that names only
  members that are not part of the state, reports at the object's path. Messages are not deduplicated by their
  text: an object's error whose text equals a member's error stays.
- **Order.** The producer visits the View's object, then each member in state order and, depth first, the objects
  they reach, list elements in order. Each object's own errors come before those of its members.
- **`hasErrors`.** True when the model reports an error anywhere in the View's graph, also one that `errors` leaves
  out because of the limits below.
- **Limits.** A producer visits at most 4,096 objects and enumeration entries, writes at most 256 errors, at most 64
  segments per path and at most 2,048 UTF-16 code units per message, code, severity and string segment, cutting the
  value at that length. When it leaves anything out or cuts anything, `truncated` is true. An object reached again
  inside its own path (a cycle) is not visited again and does not set `truncated`.

### `pending`

With the feature `validation-pending/1`, `validation` may have a member `pending`: the paths whose validation is in
progress, such as an asynchronous rule that asks a server whether a name is taken. A page shows them as "checking…".

```json
{"hasErrors":false,"truncated":false,"errors":[],"pending":[["user-name"],["items",0,"label"]]}
```

- **Presence.** `pending` is present exactly when at least one path is pending, so a View with no validation in
  progress has the same `validation` as without the feature. It is part of the `validation` value on every
  connection ([members of state values](#features)), and a change of it is published as a `set` of `validation`,
  like a change of `errors`.
- **Paths.** Paths follow the rules of error paths above: a model reports a pending member path relative to its
  object, and an unresolvable one reports at the object's path. Each path appears once.
- **Order.** Paths are sorted segment by segment: a list index before a string segment, indexes by value, strings by
  UTF-16 code units, and a path before the paths that continue it. So a publication in which the same checks run
  writes the same `pending`, whatever order the model reports them in.
- **Limits.** At most 256 paths, 64 segments per path and 2,048 UTF-16 code units per string segment. When the
  producer leaves out or cuts a path, `validation` has `"pendingTruncated": true`, which is otherwise absent;
  `truncated` describes `errors` only.
- **Independent of errors.** A pending path does not set `hasErrors` and is not an error. A member can be pending
  and have errors at once, such as the result of the previous check while the next one runs.
- **Client.** A client that uses the feature reads `pending` as a list of paths; a member's field is validating
  while a path starts with the member's wire name. A client without it ignores the member.

### `text`

With the feature `translation-text/1`, an error may have a member `text`: its message as a
[text reference](values.md#text-reference) of Runic Translations, which the page shows in the window's language
([translated text](#translated-text)). `message` stays the error's text and is the fallback where the page cannot
translate the reference.

```json
{"path":["title"],"message":"Title must be at most 80 characters.","code":"title-too-long",
 "text":{"version":2,"catalog":"notes","contractFingerprint":"sha256:…","key":"title_too_long",
 "arguments":{"max":{"type":"int64","value":"80"}},"fallbackText":"Title must be at most 80 characters."}}
```

- **Presence.** `text` is present when the model gives the error a reference, and absent otherwise. It is part of the
  `validation` value on every connection ([members of state values](#features)); a client without the feature
  ignores it.
- **Limits.** A reference of more than 16 KiB of UTF-8 is left out of its error, which keeps its message; the
  producer logs it. `truncated` is not set: the error is complete.
- **Client.** A client that reads an error with a `text` that is not a text reference ignores the member and shows
  the message.
- `{name}Errors` keeps the messages.

### `{name}Errors`

An array of the messages the model reports for the member `name` of the View's own object, in the model's order, or
`[]`. Its messages follow the `message` rule above. The member's errors also appear in `validation`, at the member's
path.

## Commands and availability

| Request | Members | Reply |
| --- | --- | --- |
| `command` | `route`, `member`, `input?`, `epoch?` | `{ok:true, revision, superseded?}` after the command completed |
| `can` | `route`, `member`, `input?` | `{ok:true, value}` with `value` a boolean |

- `input` is present exactly when the command takes an input, encoded as the command's input type. A missing or
  extra `input` is `invalid-request`; an input that does not decode is `invalid-input`.
- A command that is not available is `rejected` with `unavailable` and runs nothing. `can` answers its availability
  for the given input without running it.
- A command that fails with its **declared failure** replies `domain-failed` with `failure` (the encoded value) and
  `revision`, and never `detail`. A declared failure that cannot be encoded, or whose encoding is larger than 4 KiB of
  UTF-8, is `failed` instead. Any other failure is `failed`, with `revision` when the command had already run.
- An asynchronous command keeps the request open until it completes. A window runs at most 64 such calls at once;
  another one is `rejected` with `capacity` and runs nothing.
- **Key commands.** A command may take a key that the producer resolves to an object before it runs. A key that
  resolves to nothing is `rejected` with `unresolved-key` and runs nothing; `can` answers `false` for it; `start`
  rejects it with the reason `unresolved-key`. The operation identity uses the key as the input.

**Availability members.** For each command without an input, the state has a member `can{Name}`, the command's
availability as a boolean. For each asynchronous command, it has `is{Name}Executing`, true while an execution of the
command runs. `{Name}` is the command's wire name with its first character in uppercase. When the View already has a
boolean member of that name that is the command's availability source, that member is the availability member and
appears once. These members change like any other member, so clients render availability from state rather than by
asking `can`.

### Presentation epochs

With the feature `presentation-epoch/1`, a command can be scoped to the presentation it was issued on. A wizard's Next,
clicked twice on page 1, then moves to page 2 once: the second click was made on page 1, which the first one ended.

```json
{"kind":"open","route":"wizard","presentation":"p-1"}
{"ok":true,"revision":40,"epoch":40,"state":{"page":1,…},"presentation":"ok"}

{"kind":"command","route":"wizard","member":"next","epoch":40}
{"kind":"state","route":"wizard","revision":41,"epoch":41,"state":{"page":2,…}}
{"ok":true,"revision":41}

{"kind":"command","route":"wizard","member":"next","epoch":40}
{"ok":true,"revision":41,"superseded":true}
```

**Epochs.** Each route has a current **epoch**, a non-negative safe integer that the producer assigns. Each epoch of a
route differs from every earlier epoch of that route in the window, including those of earlier attachments of the same
content route. A client treats epochs as opaque and compares them only for equality.

**When an epoch begins.** A new epoch begins with:

- the first state of a route ([Publication](#publication)): each attachment of a root View or of content, so content
  that is presented again after it was suspended has a new epoch;
- a **renewal** that the application requests for a route, as a wizard does when it shows its next page.

Nothing else begins one. A change of a content slot does not renew the epoch of the slot's route.

**Where it travels.**

- On a connection that uses the feature, the `open` and `snapshot` replies carry the route's current epoch as `epoch`,
  beside `revision`.
- A `state` push carries `epoch` while at least one connection of the window uses the feature, on every connection
  ([members of broadcasts](#features)). A client without the feature ignores it.
- Frames never carry an epoch and never change it. The epoch of a route's state is that of the last full state the
  client accepted for the route: a `state` push, or an `open` or `snapshot` reply.

**Renewals are full states.** A renewal publishes the route: its next publication is a `state` push at a new revision,
with the new epoch and the changes pending with it, also when no member changed and whether or not a connection uses
the feature. A client therefore receives the epoch in order with the state it belongs to. Frames that follow build on
that state.

**Scoped commands.** On a connection that uses the feature, a `command` MAY carry `epoch`, the epoch of the state the
command was issued on. The producer runs such a command only when `epoch` is the route's current epoch. A renewal that
was requested but not yet published counts as a new epoch: the producer checks the epoch in the command's
[turn](#window-turns), so a command that crosses a renewal requested by another request, a timer or a completed
operation is caught before the renewal's `state` push. Otherwise the command is **superseded**:

- Nothing runs: the input is not decoded, the key is not resolved and availability is not checked.
- The reply is `{"ok":true,"revision":R,"superseded":true}`, as for a superseded `set`, with R the route's last
  published revision after the producer published what was pending ([revisions](#revisions)). It is not an error.

A `command` without `epoch` is a base command, and the producer checks every `command` that carries `epoch`, whichever
commands the contract scopes. On a connection without the feature, `epoch` is an unknown member and is ignored.

**Order of checks.** A producer checks a `command` in this order, and answers the first that fails:

1. the envelope: `invalid-request`, including an `epoch` that is not a non-negative safe integer on a connection that
   uses the feature;
2. the route: `released` when the producer does not serve it;
3. the epoch: superseded;
4. the input: `invalid-input`;
5. the key: `unresolved-key`;
6. availability: `unavailable`;
7. the window's command capacity: `capacity`.

**Scope.** `presentation-epoch/1` scopes `command` only. `start`, `can`, `set`, `write`, `region` and interaction
replies carry no epoch: operations have their own identity and duplicate detection, setters are last-writer-wins by
design, and a `region` request names its region by key ([region collections](#region-collections)).

**Client.** A client that uses the feature:

- keeps, for each route, the epoch of the last full state it accepted, taking `epoch` only when it is a non-negative
  safe integer; a state without one leaves the route without an epoch;
- sends `epoch` with each call of a command that the route's contract marks **scoped**, and with no other command. The
  epoch is that of the state the presentation's subscribers last received, not of the latest state the client applied:
  a client that delivers states to its subscribers later than it applies them
  ([state changes](state-changes.md#applying-frames)) sends the epoch of the state still on screen. Each presentation
  therefore records the epoch of the state it was delivered; a renewal that changed no member updates that epoch
  without notifying subscribers. Without an epoch it sends none;
- resolves a superseded call like a successful one: once the route reached R ([client resolution](#client-resolution)),
  with the state it exposes then, and without a diagnostic.

## Content

A View can present other Views in content slots. Each presented object and kind has a **content reference**
`{kind, id}` with an `id` of 32 lowercase hexadecimal digits that stays the same for the window, including while the
object is suspended. Its route is `content{id}`.

Content is attached while it is presented, that is while a slot of its parent's state refers to it. That follows the
parent's state, not a page: the content of a route's first state is attached before any client opens the route, and a
change of a slot attaches its new content without waiting for a client. Replacing, clearing or pruning it, or its
parent's release, suspends it when no other slot presents it. A suspended object releases its route, its Views and its
checked fields, so its requests answer `released`; only its identity remains. The producer MAY forget the identity
later, and a forgotten object gets a new id when it is presented again.

### Region collections

A **region collection** is a state member that presents a set of navigation regions that changes at run time, such as
document tabs that each keep their own back stack. Its value is an object:

```json
{"regions":[{"key":"7","content":{"kind":"document","id":"0123456789abcdef0123456789abcdef"}},{"key":"9","content":null}],"selected":1}
```

| Member | Contract |
| --- | --- |
| `regions` | The regions in order. Each is `{key, content}`. |
| `regions[].key` | A string of 1 to 128 printable ASCII characters that identifies the region in the collection. It stays the same while the region is in the collection, differs from the key of every other region of the collection, and is never given to another region of the collection later. Two collections can use the same key. A client treats it as opaque and compares it only for equality. |
| `regions[].content` | The region's current content reference, or `null` while the region is empty. It is presented like an item of a content collection: content that no region presents any more is pruned ([Content](#content)). |
| `selected` | The index in `regions` of the selected region, a non-negative integer less than the number of regions, or `null` exactly when `regions` is empty. |

The member changes like any other member: a frame carries it as a `set` change of the whole value. Regions are added
by the producer only. Which region is selected, and which regions the collection has, change only when the producer
commits the change, so a client that shows `selected` shows the region that is still selected after a request that a
departure guard refused.

**Region requests.** With the feature `region-collections/1`, the client selects or closes a region by its key:

```json
{"kind":"region","route":"editor","member":"documents","action":"close","key":"7"}
{"ok":true,"revision":44,"outcome":{"kind":"Committed","rejection":null,"arrival":"None"}}
```

| Request | Members | Reply |
| --- | --- | --- |
| `region` | `route`, `member`, `action`, `key` | `{ok:true, revision, outcome}` after the request settled |

- `action` is `select` (show the region) or `close` (remove the region from the collection and end it). `key` is a
  string. A `region` request on a connection that does not use the feature, an `action` other than these two, or a
  `member` that is not a region collection of the route, is `invalid-request`.
- A `key` that names no region of the collection when the producer admits the request, such as the key of a region
  that closed meanwhile, is `rejected` with `unresolved-key`, and nothing runs.
- A `close` of a collection whose contract does not let the page close regions is `rejected` with `unavailable`.
- A region request counts toward the window's 64 awaited calls ([Commands](#commands-and-availability)); another one
  is `rejected` with `capacity` and runs nothing.
- **Order of checks.** A producer answers the first that fails: the envelope and the feature (`invalid-request`), the
  route (`released`), the member (`invalid-request`), a `close` the collection does not allow (`unavailable`), the
  window's capacity (`capacity`), then the key (`unresolved-key`).
- The producer decides whether the request commits, for example by asking the departure guards of the region left or
  closed. The request keeps its reply until it settled. `outcome` says how it ended; a refused or superseded request
  is not an error:

| `outcome` member | Contract |
| --- | --- |
| `kind` | `Committed`, `Rejected`, `Superseded` or `Failed`. The set is open: a client keeps a kind it does not know. |
| `rejection` | Why a `Rejected` request was refused, such as `Guard` (a departure guard refused), `Busy`, `Cancelled` or `Closed`; `null` for the other kinds. The set is open. |
| `arrival` | For `Committed`, how the work that the producer runs when content becomes current ended: `None`, `Completed`, `Failed` or `Cancelled`; `None` for the other kinds. The set is open. |

- A committed `select` changes `selected`. A committed `close` removes the region from `regions`; when it was the
  selected region, the next region becomes selected, or the previous one when it was the last, or none when it was the
  only one.
- A request that fails in the producer for another reason, an unexpected exception, is `failed` and has `revision`.
- A `close` can release the route that sent it, as a command that navigates away from its page does. Its reply then has
  `released: true` instead of `revision`, and keeps `outcome` ([released by the call](#revisions-in-replies)).
- A client resolves the call once the route has reached the reply's revision ([client resolution](#client-resolution)),
  with `outcome`. A client sends a `region` request only once it knows that the connection uses the feature, so a call
  made while a new connection's `hello` is on its way waits for that `hello`'s reply.
- **Concurrent requests.** A `close` of a region whose `close` is still pending, from any connection, joins it: the
  producer does not decide again, and every joined request gets the pending request's `outcome`, each with its own
  `revision`. A `close` that is joined still counts toward the capacity. Any other request of the collection, such as
  a `select`, is admitted as a later request: it supersedes the pending requests of the collection, which end with the
  `outcome` kind `Superseded`, and the producer abandons their decisions. A request whose key names the region that a
  committed `close` selected commits like any other.
- **Not scoped to a presentation.** `region` requests carry no `epoch` ([presentation epochs](#presentation-epochs)):
  they name the region by its key, which identifies the region whatever state the page showed, and a key of a region
  that is gone is `unresolved-key`.

`region` is a request that changes state, so its reply follows the pushes the producer published before it
([reply order](#reply-order)).

## Query collections

With the feature `query-collections/1`, a View can have a **query collection**: a list too large to send, such as
500,000 database rows, whose rows stay in the producer. The route's state holds only the collection's query, the number
of rows that match it, and a version; each presentation reads the rows of the range it shows with `query-rows`, so two
pages that scroll the same list never receive, or move, each other's rows.

**State member.** A query collection's member in state is an object:

```json
{"query":{"sort":[{"field":"label","descending":true}],"filters":[{"field":"label","op":"contains","value":"9"}]},"total":1200,"version":4}
```

| Member | Contract |
| --- | --- |
| `query` | The collection's sort and filter, in the form below. It is state of the View: every presentation shares it, and a `query-set` of one presentation changes it for all. |
| `total` | The number of rows that match `query`, or `null` while the producer has not counted them: before the first count, and after a change of `query` until the new query is counted, since the old total belongs to the old query. After rows were added or removed under the same query, the producer MAY keep the old total until its new count. |
| `version` | A non-negative safe integer that increases whenever rows a page loaded may be out of date: after the query changed, after rows were added or removed, and after a row changed in place. A page loads its range again when it increases. |

The member changes like any other value member: in a `state`, or a `set` change in a frame. A `total` that becomes known
or changes after a count, without a new `version`, is such a change too.

**Query.** `sort` is an array of at most 8 sort keys `{field, descending}`, which order the rows in turn; `filters` an
array of at most 32 filters, which all apply. `field` is a string of 1 to 128 characters, normally a row member's wire
name; the producer decides which fields exist. A filter has `field` and `op`:

| `op` | Members | Keeps a row when |
| --- | --- | --- |
| `equals` | `value` | the field equals `value`, encoded as the field's wire type |
| `contains` | `value`, a string | the text field contains `value`; whether case matters is the producer's |
| `range` | `min?`, `max?` | the field is at least `min` and at most `max`; an absent bound is open |
| `custom` | `value?` | the producer's own filter named `field` passes with `value` |

An `op` the producer does not know, like a field or a value it cannot use, makes the query `invalid-input`: a later
release can add operators without a new version of the feature, and a client that sends one to an older producer gets
`invalid-input`. A producer checks a query, including the values of its `custom` filters, before it takes it, so a
query it cannot run never becomes state.

A producer writes the query in this canonical form: both arrays present, `descending` present, and `value`, `min` and
`max` only when they have a value, in the order listed. Two queries are equal when their canonical forms are.
The producer orders rows by the sort keys and then by row key, so that ranges are stable.

**Requests.** On a connection that uses the feature:

| `kind` | Members | Success reply |
| --- | --- | --- |
| `query-rows` | `route`, `member`, `start`, `count` | `version`, `total`, `start`, `keys`, `rows` |
| `query-set` | `route`, `member`, `query` | `revision` |
| `query-index` | `route`, `member`, `key` | `version`, `index` |

- **`query-rows`** reads `count` rows from position `start` (both non-negative safe integers; `count` at most 1,000,
  else `rejected` with `invalid-input`). Once the producer has counted the current query, a range at or past `total`
  answers no rows without reading the source. The reply's `rows` are the rows from `start` in the query's order, encoded as
  the row type, at most `count` and fewer at the end of the list; `keys` are their keys, which follow the
  [keyed collection rules](state-changes.md#keyed-collections) (nonempty strings, unique, a lowercase GUID or an `Int32`
  in decimal). `version` is the version the rows were loaded at, and `total` the count of the rows that match, or
  `null` when the producer does not know it. A `query-rows` changes nothing, so its reply carries no `revision` and does
  not wait for pushes. Only the connection that sent it receives the rows.
- **`query-set`** changes the query. It is a request that changes state: it runs in a window turn, its reply carries
  `revision`, and the state at that revision has the new query and a new version. A query the producer cannot run, such
  as one that names a field it does not have, is `rejected` with `invalid-input` and changes nothing. A query equal to
  the current one changes nothing, and the reply carries the route's last revision.
- **`query-index`** answers the position of the row whose wire key is `key` in the current query's order, or `-1` when
  no row that matches the query has it, at `version`. A page uses it to scroll a row into view.
- A `member` that is not a query collection of the route is `rejected` with `invalid-request`. On a connection that does
  not use the feature, the three kinds are `invalid-request`. A failure to load is `failed`. A collection that was
  released while a request ran answers `released`.
- **Limits per connection.** A producer admits, per connection and collection, at most 2 `query-rows`, 1 `query-set` and
  1 `query-index` in flight. Another one is `rejected` with the reason `busy` at once and ran nothing; the client sends
  it again after the one in flight completed. `busy` differs from `capacity`: it ends as soon as a request completes.

**Producer.** A producer may serve rows from a cache, coalesce requests for the same rows, and bound how many loads run
at once, as long as every reply's rows are the rows of the query in force at its `version`. It bounds the source work a
page can cause: loads, counts and key searches share a fixed number of slots; a change of the query cancels the count
and the key searches of the old one; and a load stops once no request waits for it, such as when the request's
connection closes. The protocol has no request to cancel a `query-rows`: a client that scrolls past a range in flight
lets it complete, which costs at most one range because it has one in flight at a time.

**Client.** A client that uses the feature:

- keeps, per presentation and collection, the rows it loaded with their version, and shows a placeholder for a position
  of its range it has not loaded, or whose row has a version that its state has not reached yet (it may belong to a
  query the page does not show yet);
- records the version at which the current query took effect, the first state with that query, and discards the rows
  of any reply with an older version, since they are rows of another query; on a change of `query` it keeps only the
  rows loaded at that version or later;
- sends at most one `query-rows` of a collection at a time, for the positions of its latest range that it has not loaded
  at the current version, at most 1,000;
- loads its range again when `version` increases, and discards every row when `query` changes; it shows rows of an older
  version until their new version arrives;
- does not send `query-rows` for positions at or after `total` once `total` is known;
- sends one `query-set` of a collection at a time; a query set while one is in flight replaces the one waiting to be
  sent, and the calls it replaced settle with it;
- sends one `query-index` of a collection at a time, and uses a position whose `version` is older than its state's
  version only after searching again, since the query changed meanwhile;
- sends a request answered `busy` again after a short delay.

Scenarios: [`query-collections`](conformance/scenarios/query-collections).

## Presentations

A client registers each route it shows with `open`, and ends the registration with `close`:

| Request | Members | Reply |
| --- | --- | --- |
| `open` | `route`, `presentation`, `handlers?` | `{ok:true, revision, epoch?, state, fields?, presentation, registration?}` |
| `close` | `route`, `presentation` | `{ok:true, presentation}` |
| `snapshot` | `route` | `{ok:true, revision, epoch?, state, fields?}` |

- `presentation` in the request is the presentation's id, unique within the page. `presentation` in the reply is
  `ok`, or `ignored` when another connection or client owns that id on the route; the reply still carries the state.
  An exact repeat of an open presentation answers `ok`.
- `open` reads the route's full state and registers the presentation. Where the producer has View objects, each
  presentation gets a fresh View; a producer without them treats `open` as a subscription. Every client sends `open`
  for every route it shows, including root routes without interactions.
- `handlers` registers the presentation's [interaction handlers](#interactions) as generation 0, and `registration`
  answers that registration.
- A connection holds at most 64 presentations per route; another `open` is `rejected` with `capacity`. A suspended
  route answers `released`.
- `close` answers `ok`, also for a presentation that is not open, or `ignored` when another connection owns it. Closing
  a connection closes its presentations.
- `snapshot` reads the full state without registering anything. Clients use it for recovery.
- On a connection that uses `presentation-epoch/1`, the `open` and `snapshot` replies carry the route's current
  `epoch` ([presentation epochs](#presentation-epochs)), also the `open` of a reconnect.
- A suspended route whose presentations are still open keeps them. When its object is presented again before the
  page closed them, as when one command sets `Main = b; Main = a;` and the page never saw `b`, the open presentations
  resume on the new View in a later turn of the window. The producer then pushes the new View's first state as a
  `state` push, whether or not a connection uses a feature, so a page that kept its presentations receives the new
  attachment's state and, with `presentation-epoch/1`, its new epoch.

**Reconnects.** After a new connection, a client sends `hello` with a new session and then `open` for each route it
shows, with the same presentation ids. While the closed connection's presentations are not yet released, the reply
can be `ignored`; the client retries `open` until it answers `ok`. Interaction handlers resume with the new
registration.

## Presented states and window close

With the feature `presented/1`, a page reports that it painted a state, and acknowledges that the application closes
its window:

```json
{"kind":"present","route":"result","revision":57}
{"kind":"presented","route":"result","revision":57}
{"ok":true}

{"kind":"closing"}
{"kind":"closing-reply"}
{"ok":true}
```

| Message | Members | Meaning |
| --- | --- | --- |
| push `present` | `route`, `revision` | To one connection: report when the page has painted `route` at `revision` or later. |
| request `presented` | `route`, `revision` | The page painted `route` at `revision`. The reply is `{ok:true}`. |
| push `closing` | none | To each connection that uses the feature: the application closes the window. The first push without a `route`. |
| request `closing-reply` | none | The page has shown the window's last state. The reply is `{ok:true}`. |

A producer sends `present` and `closing` only to connections that use the feature. A client without it would ignore
them as unknown push kinds.

### Painted states

**Waits.** The application can wait until a page painted what an object of the application presents, such as the page
a wizard has just shown. A wait names the object and a timeout:

- The producer publishes what is pending on every route that presents the object, as before a reply
  ([revisions](#revisions)), and takes each route's last published revision.
- The wait succeeds once a page reported `presented` for one of those routes at that revision or later, from any
  connection: the first report completes it. It fails when its timeout passes first.
- An object that no route presents yet counts with the first state of the first route that presents it within the
  timeout, as when the application sets a content slot to the object and waits before the slot's route is attached.

**`present`.** The producer pushes `present` only while a wait is pending, so an idle or typing page sends nothing
extra. It pushes it to each connection that uses the feature and has an open presentation of the route, at most once
per wait, route and connection, also to a connection that opens the route while the wait is pending, after the `open`
reply. `revision` is the revision the wait took for the route.

**`presented`.** The reply is `{ok:true}`. A report that no wait needs has no effect. `presented` on a connection that
does not use the feature, or whose `revision` is not a non-negative safe integer, is `rejected` with
`invalid-request`; a route the producer does not serve answers `released`.

**Client.** A client that uses the feature answers `present` for a route it shows with `presented` once the page has
painted the route at `revision` or later:

1. the route reached `revision` under the client's revision rules, including the revision timeout and recovery
   ([client resolution](#client-resolution));
2. the route's state was delivered to the subscribers of its presentations;
3. one animation frame of the page passed after that delivery, so the page rendered the state. A page without
   animation frames, such as a hidden page or a test runner, uses the timer or the end of the task with which it
   delivers notifications instead.

- `revision` in the report is the route's revision that was painted, which is at least the requested one. A client
  keeps at most one `presented` per route in flight and reports the highest revision requested.
- A route whose presentations have no subscriber, such as a presentation kept open while nothing shows it, is not on
  screen: the client reports once a subscriber is added and the steps above passed.
- A hidden page is not special: it reports once its notifications were delivered. A wait for a minimized window would
  otherwise last until its timeout.

### Window close

**`closing`.** Every close of a window that the application or its host makes pushes `closing` to each connection of
the window that uses the feature: a close the application requests, once its [close decision](#interactions) allowed
it, the window's disposal and the host's shutdown. The push happens once per window.

**The close waits.** A close with a timeout also waits until each connection it pushed `closing` to replied with
`closing-reply` or closed. It waits concurrently with the work the close already waits for
([limits and window close](#limits-and-window-close)), within the same timeout, and for at most 5 seconds. The close
reports whether every such connection acknowledged it; a close that pushed `closing` to no connection is
acknowledged. A close without a timeout pushes `closing` and does not wait. A later close of the same window does not
push or wait again, and reports the first close's result.

**`closing-reply`.** The reply is `{ok:true}`, also without a pending `closing`: the request is idempotent. On a
connection that does not use the feature it is `rejected` with `invalid-request`.

**Client.** On `closing`, a client that uses the feature delivers its pending notifications, writes the field drafts
it has not sent yet (such as an input that waits for a pause before it is written) and waits for those writes for at
most 2 seconds, waits one animation frame (or the fallback of step 3 above), sends `closing-reply`, and reports that
the producer closed the window. `closing` comes after the close was decided, so these writes reach the model but no
departure guard: a guard that must see them needs [field flush](#field-flush). When the connection then drops, it
keeps reporting that the producer closed the window, not a reconnecting or lost connection. A new connection reports a
connected client again.

### Field flush

With the feature `field-flush/1`, the producer asks the pages to write their field drafts before it decides a
departure, so a departure guard that reads the model (such as whether it has unsaved changes) sees the user's last
input, also when the page has not sent it yet.

```json
{"kind":"flush","id":"flush-7"}
{"kind":"flush-reply","id":"flush-7"}
{"ok":true}
```

**When.** Before the producer asks a departure guard of a departure that the page did not start through a request it
sends after its drafts: a navigation the model starts (from a menu, a shortcut or a command), a close the application
requests (`RequestCloseAsync`), and the window's close button when the window guards its close. The producer pushes
`flush` with an `id` of its choice, unique in the window, to each connection that uses the feature, and waits until
each of them replied `flush-reply` with that `id` or closed, for at most 1 second. A connection without the feature
gets no push, and a flush without such connections completes at once. Then the producer asks the guards, on the
model's state at that point, whether or not every page answered.

**`flush-reply`.** `{kind:"flush-reply", id}`; the reply is `{ok:true}`, also for an `id` no flush waits for. On a
connection that does not use the feature it is `rejected` with `invalid-request`.

**Client.** On `flush`, a client that uses the feature writes every field draft it has not sent yet, waits for those
writes for at most 800 milliseconds, and sends `flush-reply` with the push's `id`. A client also writes its drafts on
its own when the user may be about to leave without a request of its own: on a key with Ctrl, Cmd or Alt before the
page handles it, unless the key types a character (AltGr, which Windows reports as Ctrl and Alt, and macOS Option
type characters such as `@`, `€` or `{` on many layouts), and when the page's window loses focus, as when a native
menu opens.

## Translated text

Text that the application produces, such as a status line or a validation message, can be a
[text reference](values.md#text-reference) of Runic Translations instead of a formatted sentence: a member, field,
command result or other value of the type `text-reference`, and the [`text`](#text) of a validation error. The page
formats each reference in the window's language with the Translations runtime of the page. When the language changes,
the page formats the references it has again; no message crosses the wire per string.

The **window's locale** is the language the application chose for the window, or none. With the feature
`translation-text/1`, the producer pushes it:

```json
{"kind":"locale","locale":"de"}
```

| Message | Members | Meaning |
| --- | --- | --- |
| push `locale` | `locale` | To each connection that uses the feature: the window's locale, a language tag of 1 to 64 ASCII letters, digits and `-`, starting with a letter and without an empty subtag (`de`, `pt-BR`), or `null` when the application sets none. A window-level push without a `route`. |

- **When.** The producer pushes `locale` to a connection after its `hello` succeeded, when the window has a locale,
  and to every connection that uses the feature each time the application changes it, also to `null`. The last
  push a connection receives is the window's locale. A producer may send the first push before the `hello` reply
  reaches the page.
- **The application owns it.** Only the application sets the window's locale; a page that offers a language switch
  asks the application, for example with a command. Every page of the window therefore shows one language, and the
  application can keep the choice with its settings. A page that changes its runtime's locale itself is set back by
  the next push, at the latest after a reconnect.
- **Number, date and time fields.** A client's number, date or time field without a locale of its own reads and
  writes its text in the window's locale, and formats it again when the locale changes, except while the user edits a
  draft. Date and time texts also accept the ISO form. The values on the wire keep their forms (`values.md`); only
  the text a field shows and reads changes.
- **Client.** A client that uses the feature applies every `locale` push: it sets the locale of the page's
  Translations runtime, also when the push repeats the last value, since the page may have changed its runtime's
  locale itself meanwhile, and formats references in it. It reports a change of the window's locale only when the
  value differs. It applies a `locale` push
  whatever the features it has read so far, since the push may come before the `hello` reply. On `null` it keeps
  the language it shows. Without a window locale, the page formats references in its own language. A `locale` push
  whose `locale` is neither a tag nor `null` is invalid and ignored. A client without the feature ignores the push
  kind.
- **Formatting.** A reference the page's Translations runtime cannot format, such as one of another catalog or
  whose arguments no longer match, shows its `fallbackText`; the page chooses what to show without one. The text is
  plain: a page shows it as text, never as markup.

## Appearance

A page can read most of the desktop's appearance preferences from its engine, with CSS media queries such as
`prefers-color-scheme`, `prefers-contrast`, `forced-colors` and `prefers-reduced-motion`. Not every engine exposes every
one of them, and none exposes the desktop's text size, accent colour or forced-colours palette to script. With the
feature `appearance/1`, the producer pushes the **window's appearance**, the preferences its host reads from the
desktop:

```json
{"kind":"appearance","colorScheme":"dark","contrast":"forced-colors","reducedMotion":false,"textScale":1.25,"accentColor":"#0078d4","systemColors":{"canvas":"#000000","canvasText":"#ffffff","linkText":"#ffff00","grayText":"#3ff23f","highlight":"#1aebff","highlightText":"#000000","buttonFace":"#000000","buttonText":"#ffffff"}}
```

| Member | Value |
| --- | --- |
| `colorScheme` | `"light"`, `"dark"`, or `null`: the desktop's colour scheme. A window that shows a scheme of its own reports it in `prefers-color-scheme`, not here. |
| `contrast` | `"no-preference"`, `"more"`, `"forced-colors"`, or `null`: the desktop asks for no particular contrast, for more contrast with the application's own colours, or replaces the application's colours with `systemColors`. |
| `reducedMotion` | `true` when the desktop asks for less motion, `false`, or `null`. |
| `textScale` | A number greater than 0, the desktop's text size as a factor of the default (1 for the default, 1.5 for half as large again), or `null`. |
| `accentColor` | The desktop's accent colour as `#rrggbb` in lowercase, or `null`. |
| `systemColors` | While `contrast` is `"forced-colors"`, the palette the desktop forces, as an object of `#rrggbb` colours named after the CSS system colours: `canvas`, `canvasText`, `linkText`, `grayText`, `highlight`, `highlightText`, `buttonFace`, `buttonText`; otherwise `null`. |

`null`, and a member that is absent, mean that the host does not know the preference. A window-level push without a
`route`.

- **When.** The producer pushes `appearance` to a connection after its `hello` succeeded, with every member `null`
  while the window has no appearance, and to every connection that uses the feature each time it changes. Every push
  carries every member, so the last push a connection receives is the window's appearance, also for a page that
  reconnects after a change it missed. When the window's appearance becomes unknown again, the push has every member
  `null`. A producer may send the first push before the `hello` reply reaches the page.
- **The host owns it.** The values are what the host read from the desktop; the page cannot change them. A read that
  fails keeps the last appearance read. The desktop's accessibility settings and palette describe the user at that
  computer, so a host pushes them only to pages that nothing but that computer can reach: a host that serves pages to
  browsers on other machines, listens on other network interfaces or makes a window public pushes every member `null`
  (or does not list the feature).
- **Client.** A client that uses the feature keeps the last push. It applies it whatever the features it has read so
  far, since the push may come before the `hello` reply, and keeps it across a reconnect until the next push. A
  member of the wrong type, out of its range or with a value the client does not know, such as a later contrast
  value, is read as `null`; the rest of the push still applies. A client without the feature ignores the push kind.
- **The page's appearance.** A client that reports the page's appearance reports both sources, the engine's media
  queries (what the page's CSS sees) and the host's push, and combines them. Where the page's engine evaluates the
  media query, the client reports the engine's value for `colorScheme`. For `contrast` and `reducedMotion` the latest
  change wins: the engine's value, until a push changes the member from the host's previous value, whose new value
  then applies until the engine's media query changes; an engine may read the desktop's setting only when the page
  loads. Without the engine's value it reports the host's. `textScale`, `accentColor` and `systemColors` come from the
  host alone.

## Client languages

With the feature `client-languages/1`, the page tells the producer which languages its browser prefers, so the
application can choose the window's locale for a page it has not seen before, such as a browser that connects to a
CS-WebUI window:

```json
{"kind":"hello", …, "features":["client-languages/1","translation-text/1"],"languages":["de-CH","de","en"]}
```

| Member | Value |
| --- | --- |
| `languages` | A member of the `hello` request: an array of at most 16 strings, most preferred first. |

- **Client.** A client that implements the feature lists it and sends `languages` in every `hello`: the browser's
  `navigator.languages`, or `[navigator.language]` where the list is missing, without strings that are not language
  tags (1 to 64 ASCII letters, digits and `-`, starting with a letter, without an empty subtag), each once
  (compared without case), at most the first 16. A page that knows no language, or whose application turned the
  report off, sends `[]`; a client SHOULD let the page turn it off. Like `session` in
  [`request-session/1`](#request-sessions), the member is sent before the reply tells whether the feature is in use;
  a producer without the feature ignores it.
- **Producer.** On a `hello` that lists the feature, a `languages` that is present but is not an array of at most 16
  strings is `rejected` with `invalid-request`, as an invalid `features` is. The producer keeps the language tags of
  the array, in their order, in the case BCP 47 recommends (the language lower case, a script title case, a region
  upper case, and other subtags, including those after a singleton, lower case: `DE-ch` is `de-CH`), each once, and
  ignores its other strings, such as one longer than 64 characters; absent means none. It reports them as the
  connection's languages to the application, read-only, for the connection's lifetime. Without the feature in the
  `hello`, the member is ignored.
- **One language per window.** The languages are a preference the page reports, not a request. The window keeps one
  locale for all of its connections ([translated text](#translated-text)), which only the application sets: it may
  choose it from the languages of a connection, for example the first one of a window, but no connection is shown
  another language than the window's. The producer pushes nothing for this feature.
- **Negotiation.** An application negotiates the languages against the locales it ships and sets the result; it
  never sets a client's tag as the window's locale directly, which would let one remote page choose an arbitrary
  locale for everyone who shares the window.
- **Privacy.** The list is personal data and can help fingerprint a browser; it can say more than the
  `Accept-Language` header the producer may already see. An application uses it to choose the window's language
  only, and does not log, store or forward it. A producer does not log it either.

## Page errors

With the feature `client-errors/1`, a page reports the errors it cannot show to the application, such as an uncaught
exception, and the producer logs them where the application's other diagnostics go:

```json
{"kind":"client-error","source":"error","name":"TypeError","message":"x is not a function","stack":"TypeError: x is not a function\n    at load (https://app.example/main.js:10:5)","url":"https://app.example/index.html","dropped":2}
{"ok":true}
```

| Member | Value |
| --- | --- |
| `source` | An identifier of at most 32 characters: `error` for an uncaught exception, `unhandled-rejection` for a rejected promise that nothing handled, `runtime` for an error the client's runtime caught, such as one a subscriber threw. A producer logs another identifier as it is. |
| `message` | The error's message. |
| `name?` | The error's type, such as `TypeError`. |
| `stack?` | The stack, with `\n` between frames. |
| `url?` | The page's URL without its query and fragment. |
| `route?` | A route name: the View whose push or subscriber failed, for `runtime`. |
| `dropped?` | A non-negative safe integer: the reports the client counted but did not send since its previous report. |

**Client.** Reporting is opt-in: a client sends `client-error` only when the application asks it to report errors,
and only on a connection that uses the feature. A client may list `client-errors/1` in its hello whether or not a
reporter is installed (`@runic-artifex/views` always does), so a connection that uses the feature does not mean
reports will arrive. A report leaves the page bounded and redacted. The client:

- cuts the message and the stack, without splitting a surrogate pair, and removes the query and fragment of every URL in them and of `url`, which can
  carry tokens;
- sends at most a fixed number of reports a minute, counts a repeat of one error within the minute instead of sending
  it again, and states what it counted in `dropped` of its next report;
- does not wait for the reply, retry a failed report, or report a failure of `client-error` itself.

**Producer.** The reply is `{ok:true}`, also for a report the producer does not log. The report is untrusted input
from the page:

- `client-error` on a connection that does not use the feature, without a `source` identifier or a `message` string,
  or with a member of another type than above, is `rejected` with `invalid-request`. A text longer than the
  producer's limits is cut, not refused.
- Before logging, the producer removes control characters other than the stack's line breaks and the bidirectional
  embeddings, overrides and isolates (U+202A-U+202E, U+2066-U+2069), marks every line of the stack with a fixed
  prefix so it cannot pass for a log entry of its own, and removes the query and fragment of `url` again. A cut does
  not split a surrogate pair.
- The producer bounds what one page can log: it SHOULD log at most a fixed number of reports per connection a minute
  and a fixed number per window across its connections, so a page that reloads or reconnects gets no fresh budget,
  count the rest, and add the count to `dropped` of the next report it logs.

The `.NET` producer logs each report as event 1150 `PageError` (Error) in the category
`Runic.Application.Views.Page`, at most 10 per connection and 30 per window a minute, and event 1151
`PageErrorsSuppressed` or 1152 `WindowPageErrorsSuppressed` (Warning) once a minute when it starts counting. It cuts
`message` and `url` at 2048 characters, `stack` at 8192 and `name` at 128, and starts every stack line with `    | `.

## Navigation paths

With the feature `navigation-path/1`, the page can ask the window to go back, as its Back button does, and keep its
URL in step with the window's **route path**: the route of the current page of one region of the window, its **path
region**, which the application chooses. A route path is a string of segments separated by `/`, without a leading
`/`, such as `customers/42/invoices/7`; the application's route map defines which paths exist and what they open.

```json
{"kind":"hello", …, "features":["navigation-path/1"]}
{"ok":true, …, "features":["navigation-path/1"],"navigationPath":"customers/42"}

{"kind":"navigation","action":"open","path":"customers/42/invoices/7"}
{"kind":"navigation-path","path":"customers/42/invoices/7","operation":"push"}
{"ok":true,"outcome":{"kind":"Committed","rejection":null,"arrival":"None"},"path":"customers/42/invoices/7"}

{"kind":"navigation","action":"back"}
{"kind":"navigation-path","path":"customers/42","operation":"back"}
{"ok":true,"outcome":{"kind":"Committed","rejection":null,"arrival":"None"},"path":"customers/42"}
```

| Message | Members | Meaning |
| --- | --- | --- |
| `hello` reply member `navigationPath` | string or `null` | The window's route path when the connection uses the feature and the window has a path region; absent otherwise. `null` when the path region is empty or its page has no route. |
| request `navigation` | `action`: `"back"` | The window's Back: go back in the innermost presented region that can (below). |
| request `navigation` | `action`: `"open"`, `path` (a string of at most 2048 characters) | Open the route path in the path region, as a browser's Back, Forward or address bar does (below). |
| push `navigation-path` | `path` (string or `null`), `operation` (`"push"`, `"back"` or `"replace"`) | To each connection that uses the feature: the path region's current page changed. A window push without a `route`. |

`navigation` names no `route`; one that does, or whose `action` or `path` is missing or invalid, is `rejected` with
`invalid-request`, and so is `navigation` on a connection that does not use the feature. The producer sends
`navigationPath` and `navigation-path` only to connections that use the feature.

**Back.** `action: "back"` goes back in the innermost presented region that can: the producer looks at the window's
regions that no page owns, the most recently created first, and within a region at the regions its current page owns,
the most recently created first, before the region itself; of a region collection or other selection it follows only
the selected region. A region counts only while a View of the window presents it, as a content slot or a region of a
presented region collection; a region nothing presents, and the regions below it, are skipped. The first region whose current page is a result entry (a dialog) dismisses it; otherwise the
first region with a page below the current one goes back to it. The request is an ordinary navigation request: the
departure guards decide, and a refusal is an outcome, not an error. With no such region the outcome is `Rejected`
with the rejection `NoHistory`. Back works whether or not the window has a path region.

**Open.** `action: "open"` opens `path` in the path region: when the region's history has a page with that route, it
goes back to the newest such page, and otherwise it pushes the route's page. A path the current page already has
changes nothing and is `Committed`. A path that no route matches runs no request, and the reply's `outcome` is `null`.
A window without a path region answers `rejected` with `unavailable`.

**Replies.** The reply is `{ok:true, outcome, path}`. `outcome` is the request's navigation outcome, written as for
[region collections](#region-collections), or `null`. `path` is the window's route path after the request, and is
absent for a window without a path region. A page whose URL shows another path than `path` after the reply puts
`path` back: a refused Back or address bar edit reverts the URL. The reply follows the frames and the
`navigation-path` push of the change it caused.

**`navigation-path`.** When the window's path region becomes available after a connection's `hello`, because the
application chose it later or the window learned its regions later, the producer pushes the path with `operation:
"replace"` to that connection. After each change of the path region's current page that changes its route, or its
stack, the producer pushes the new path, after the frames of the change, with `operation`:

- `push` when one page was pushed on top of the previous stack;
- `back` when the new stack is the beginning of the previous one;
- `replace` otherwise, such as a replace or reset.

**Several connections.** Every connection of the window that uses the feature receives the pushes, so the history of
each page follows the one path region, and requests of several pages are ordinary navigation requests of that region:
the last change wins.

**Client.** The page may keep its URL and history in step: on `push` it adds a history entry with the path above the
current one, dropping those above it, on `back` it moves back to the previous entry when that entry shows the path and
replaces the current one otherwise, and on `replace` it replaces the current entry.

- When the user moves through the history or edits the address bar, the page sends `open` with the URL's path. A move
  back to an entry whose path is `null`, such as a home page without a route, sends `action: "back"` instead.
- When the reply's `path` differs from the URL's, the page shows the reply's path again; of several moves in quick
  succession only the latest one's reply does.
- A URL that already has a route path when a page loads is a deep link: the page sends `open` with it once the window
  first reports its path, in the `hello` reply or a later `replace`, instead of showing that path. A page that reloads
  an entry it wrote, or reconnects, shows the window's path instead.
- A client that uses the feature may also map the mouse's Back button and keyboard shortcuts for Back to
  `action: "back"`; while it keeps its history in step, it goes back in its history instead, so a Back runs once.
- How a route path appears in the URL is the client's choice; the Runic client uses the fragment, `#/customers/42`,
  and keeps each entry's index and path in `history.state`.

## Operations

An asynchronous command can also be started as a recoverable **operation**, which the page can observe, cancel and
find again, for example after a reload.

### Identity

- Operation requests carry `route`, `member`, `operation` (the operation id, chosen by the client) and `contract`.
- `contract` is `{view}:{fingerprint}`. `view` is a stable name of the View that the producer chooses. `fingerprint` is
  an opaque string of 1 to 128 characters from U+0021 to U+007E without `:`. Two builds whose generated client and
  producer differ in anything visible on the wire (members, value types, commands, inputs, results, failures, keys,
  collection and text fields, content slots) MUST have different fingerprints. A producer MAY cover more.
- The producer keys operations by `(contract, route, operation)`. A `start` with a known key is a **duplicate** when
  its `member` and `input` are [canonically equal](values.md#canonical-equality) to the original's; otherwise it is an
  **identity conflict**. `status`, `watch`, `unwatch`, `cancel` and `stream` find an operation by its key and member;
  an operation started for another member is `unknown` to them.
- Operations belong to the window, not to a connection or client. Any connection of the window that knows the
  identity can observe and cancel an operation. A page whose operations other clients must not reach needs its own
  window.
- An operation outlives its route: `status`, `watch`, `unwatch`, `cancel` and `stream` of an operation the window still
  knows are answered even after its route was released, such as when a command navigated away from the page that
  started it. For any other operation such a request answers `released`.

### Start

```json
{"kind":"start","route":"editor","member":"import","operation":"7c0e…","contract":"Notes.EditorViewModel:3f9a…"}
{"ok":true,"revision":51,"admission":"accepted","status":{"kind":"running"}}
```

- `admission` is `accepted`, `duplicate`, `expired` or `rejected`. `status` is the operation's current
  [status](#status) for `accepted` and `duplicate`. `reason` names a rejection: `owner-closing`, `owner-disposed`,
  `identity-conflict`, `capacity`, `unavailable`, `unresolved-key`, `stream-capacity` or `invalid-input`. `expired`
  means the id belonged to an operation whose status is no longer retained.
- **Ordering.** Requests that change state are admitted one at a time per window. An accepted `start` has started its
  command, so its availability and execution members have already changed, before the admission reply is sent. The
  reply carries `revision` like any request that changes state, so the next request of any connection observes the
  first operation's effects: a second `start` of a command that is not available while the first runs is rejected
  with `unavailable`.
- A start reply with an `admission` always carries `revision`, the route's last published revision when nothing
  changed.
- A `start` whose `contract` is not the contract of the route's View comes from a generated client of another build. It
  is answered `{"ok":false,"error":{"kind":"rejected","reason":"invalid-request",…}}`, without `admission` or
  `revision`, and starts nothing; the other requests find no operation under that contract (`unknown`).

### Status

A status is an object with `kind`:

| `kind` | Members |
| --- | --- |
| `running` | `progress?`: a number from 0 to 1, absent while the progress is unknown |
| `succeeded` | `revision`, `result?`, `delivery?`, `stream?` |
| `failed` | `revision`, `error`, `delivery?`, `stream?` |
| `domain-failed` | `revision`, `failure?`, `delivery?`, `stream?`; never `error` |
| `cancelled` | `revision`, `stream?` |
| `expired` | the operation's terminal status is no longer retained |
| `unknown` | the producer does not know the operation |

- `revision` on a terminal status is the revision of the first state of the route that includes the operation's
  effects. A client resolves an operation's outcome once the route has reached it, or at once when the route is not
  open.
- `result` is the command's result, encoded as its result type. `delivery: {kind, message}` reports a result or
  failure that could not be delivered: `result-too-large`, `result-encoding-failed`, `stream-overflow` or
  `stream-retention-too-large`. `stream: true` marks a stream operation.
- A declared failure stays `domain-failed` even when cancellation was requested. One that does not fit the retention
  bound stays `domain-failed` without `failure` and with `delivery: {kind:"result-too-large"}`, because the command
  did fail and must not be retried blindly. Cancellation never rewrites a success.

### Watching and the other requests

| Request | Extra members | Reply |
| --- | --- | --- |
| `status` | | `{ok:true, status}` |
| `watch` | | `{ok:true, status}`, then `operation` pushes |
| `unwatch` | | `{ok:true, status}` |
| `cancel` | | `{ok:true, cancellation}`: `cancellation-requested`, `not-running`, `unknown` or `expired` |
| `stream` | `cursor` | `{ok:true, page}` |

- `watch` answers the current status. Until the operation is terminal, until `unwatch` or until the connection closes,
  the producer then pushes `{"kind":"operation","route","member","operation","contract","status"}` to that connection
  on each status change. The terminal status is the last push.
- Pushes of a changed `progress` are throttled per watch: the producer pushes a change at once when at least 100 ms
  have passed since the previous `operation` push of the watch, and otherwise pushes the latest progress 100 ms after
  the previous push. A terminal status is pushed at once and replaces a pending progress push.
- A connection holds at most 64 watches; another is `rejected` with `capacity`. Watching an operation that the
  connection already watches does not add a watch.
- A `stream` page is `{kind, cursor, completed, items:[{sequence, value}], delivery?}`: `kind` is the operation's
  status kind, `items` the values from `cursor` on, and `cursor` the cursor of the next page. A stream operation that
  fails as declared keeps the values it published; its pages have `kind: "domain-failed"` and `completed: true`.

### Progress

A command reports progress as a number from 0 to 1 while it runs; how is the producer's
([language mappings](language-mappings.md#net-producer)). The producer clamps reported values to the range and ignores
values that are not numbers. `progress` stays absent until the first report.

### Limits and window close

- A window retains at most 64 operations, 32 terminal results, 128 expired ids and 256 KiB of results, declared
  failures and stream replay. Running streams reserve their bound against a 256 KiB window budget; the default stream
  bound is 128 items and 64 KiB. An overflowing stream is terminal and reports `stream-overflow`.
- Closing a window stops admission: `start` answers `owner-closing`, and an asynchronous `command` answers `released`.
  The window waits up to its close timeout for running operations and asynchronous commands, and, with `presented/1`,
  for the pages to acknowledge the close ([window close](#window-close)), then requests the cancellation of what
  remains, and keeps its resources until that work ends.

### Client-only behaviour

The TypeScript client adds what the producer never sends: `wait({ timeout })` resolves to a status of kind `timed-out`
after its deadline, having sent `cancel`, and rejects with a `BridgeError` of kind `timeout`; `wait({ signal })`
rejects with the abort reason and sends nothing. Both are built on `watch`, so the protocol has no long poll.

## Interactions

A presentation can register **interaction handlers**, through which the producer asks the page for input, such as a
confirmation.

| Message | Members |
| --- | --- |
| request `interactions` | `route`, `presentation`, `generation`, `handlers: [{name, contract}]` |
| push `interaction` | `route`, `presentation`, `requestId`, `ownerEpoch`, `name`, `contract`, `expiresAt`, `input` |
| push `interaction-cancelled` | `route`, `presentation`, `requestId`, `ownerEpoch`, `message` |
| request `interaction-reply` | `route`, `presentation`, `requestId`, `ownerEpoch`, `name`, `contract`, `outcome`, `output?` |

- **Registration.** `handlers` lists the interactions the presentation answers. `contract` is the string the generated
  contract defines for the interaction's input and output. The reply is `{ok:true, registration}`:
  - `ok`: registered;
  - `unsupported`: a handler names an interaction or contract the route does not have; nothing changes;
  - `stale`: `generation` is lower than the registered one, or equal with other handlers;
  - `ignored`: the presentation is not open on this connection.

  A higher `generation` replaces the registration. `open` registers its `handlers` as generation 0.
- **Requests.** The producer pushes `interaction` to the presentation's connection. `expiresAt` is an
  `offset-date-time` after which the request times out, and `ownerEpoch` a number the producer assigns that the
  reply repeats. `input` is encoded as the interaction's input type.
- **Cancellation.** When a request ends before the page answered, the producer pushes `interaction-cancelled` with a
  human-readable `message`. The page stops the handler.
- **Replies.** `outcome` is `answered` (with `output`, encoded as the interaction's output type), `cancelled` or
  `failed`. The reply is `{ok:true, result}`: `ok`, `already-completed` for a repeat of the reply already received
  ([canonically equal](values.md#canonical-equality) `outcome` and `output`), `stale` for a request that is no longer
  pending or an identity that does not match, or `invalid-output` when `output` does not decode, in which case the
  waiting interaction fails with an output error.

**Scope.** Only work of the same window can reach the page, and an interaction never crosses to another window, client
or connection. An interaction object (a ViewModel's member) can be presented on several routes of the window; the
presentations that count are those of all of them. Two scopes exist:

- A **command invocation** reaches only the single presentation of the invoking client and connection that registered
  the handler. It reaches the invoking route or another route of the same window opened by that client and connection,
  such as the content View whose interaction a shell's command asks, or the leave guard of the page that the shell's
  Back departs. A presentation on the invoking route is preferred when it is the only one there. A page's click on a
  menu item of its in-page menu bar is such a command invocation, with the identity of the connection that sent the
  click.
- The **window scope** reaches the single presentation of the window that registered the handler, from any connection,
  counted over every route that presents the object. With none or several, it asks nothing. The window's close
  decision (a user's close or a close the application requests) and a command that the window's native menu bar or a
  host shortcut runs are in it, and the application can enter it for other work of the window, such as a timer or a
  notification's activation. Entering it inside a command invocation of the same window keeps the command invocation.

Work outside both scopes never reaches the page. The producer MUST NOT let application code claim a client or
connection identity: only a request the transport authenticated enters a command invocation. A producer MUST NOT give
two connections of a window one connection identity, also when its transport reuses the key of a connection that
closed (CS-WebUI reuses WebUI's connection slots), so a command that runs on after its connection closed never reaches
the connection that came next. A command invocation lasts until its command ends: until an asynchronous command's or
operation's task completes, or until a synchronous command or an in-page menu item returns (an in-page menu item
whose command reports a task, such as CommunityToolkit's `IAsyncRelayCommand`, lasts until that task completes). Work that the command
started and that runs on after it, such as a timer, is outside both scopes. A navigation that work in a scope starts
runs its hooks, such as the departing page's leave guard, in that scope until the navigation ends. The window scope,
which names no connection, does not end: work that it starts stays in it. A request ends
when its scope ends, its presentation closes or its connection closes, or the window closes; the waiting interaction
then fails. Interactions time out after two minutes by default (at most ten). A window holds at most 32 pending
requests and 8 per presentation.

## View requests

With the feature `view-requests/1`, the producer sends a presentation one-way **view requests**: presentation actions
that need no answer, such as to focus a field, scroll a row into view, scroll to the top or show a toast.

```json
{"kind":"view-requests","route":"notes","presentation":"p-1","generation":1,"requests":[{"name":"focus","contract":"…"}]}
{"ok":true,"registration":"ok"}

{"kind":"view-request","route":"notes","presentation":"p-1","revision":8,"name":"focus","contract":"…","input":{"member":"title"}}
```

| Message | Members |
| --- | --- |
| request `view-requests` | `route`, `presentation`, `generation`, `requests: [{name, contract}]` |
| push `view-request` | `route`, `presentation`, `revision`, `name`, `contract`, `input` |

- **Registration.** `requests` lists the view requests the presentation handles, at most 64; `contract` is the string
  the generated contract defines for the request's input. The reply is `{ok:true, registration}` with the outcomes of
  [interaction registrations](#interactions): `ok`, `unsupported`, `stale` or `ignored`. A view request registration
  has its own `generation`, a positive integer independent of the presentation's interaction handlers, and a higher
  one replaces it. `view-requests` on a connection that does not use the feature is `rejected` with
  `invalid-request`.
- **Push.** The producer pushes `view-request` to the connection of each presentation it selects, and only to
  connections that use the feature. `revision` is the route's revision that includes everything changed before the
  request: the producer first publishes what is pending on the route, as before a reply. `input` is encoded as the
  request's input type. The page does not answer.

**Delivery.** Each view request declares a delivery rule: `drop` (the default), `next-mounted` or `broadcast`. A request
belongs to the object that raised it and reaches only presentations of routes that present that object.

- `drop` and `next-mounted` select one presentation by the [scope](#interactions) of the work that raised the request:
  in a command invocation, the single presentation of the invoking client and connection that registered the request;
  in the window scope or outside both scopes, the single presentation of the window that registered it.
- With no such presentation, or several, `drop` drops the request. `next-mounted` keeps it, replacing the request of
  that object and name it kept before (the latest request wins), and pushes it to the next presentation of the window
  that registers it on a route that presents the object. It is then gone. A request kept in a command invocation waits
  for a presentation of the invoking client and connection, and is gone when that connection closes, so a reconnected
  page does not receive it. A kept request is also gone when the window closes or no route of the window presents the
  object any longer. A request raised in a scope of a window that does not present the object yet, including before
  any window presents it, is kept for that window with the scope's identity: one a command invocation raised waits
  for a presentation of the invoking client and connection and is gone when that connection closes. A request raised
  outside both scopes for an object that no window presents yet is kept for the first window
  that presents it.
- `broadcast` reaches every presentation of the window that registered the request, on every connection of the window,
  also when a command invocation raised it: it is a window-wide announcement, as a state change is, such as a toast in
  every pane that shows it. A broadcast is never kept.
- Outside both scopes, a request of an object that several windows present is dropped. A view request never reaches
  another window; `broadcast` reaches every connection of the window, and `drop` and `next-mounted` keep to the
  invoking client and connection in a command invocation.

**Never replayed.** A view request is not state: a `snapshot`, an `open` or a reconnect never repeats one, and a
request pushed to a connection that closes before the page handled it is lost.

**Client.** For a `view-request` of a presentation it shows, a client calls the presentation's handler with the
decoded `input` once the page painted the route at `revision` or later, by steps 1 to 3 of
[painted states](#painted-states), so a handler finds the elements of that state rendered. A request whose presentation
closed, or that has no handler of that name and contract, is ignored. A handler's failure stays in the page.

A client registers its handlers with `view-requests` after the presentation's `open` succeeded on a connection that
uses the feature, and again with a higher `generation` whenever they change while the presentation is open. After a
reconnect, it registers them again once the new `open` succeeded.

## Hosted regions

With the feature `hosted-regions/1`, a host page declares **hosted regions**, places in its own document where the host
application can show a guest application's Window ([regions](../desktop/host-protocol.md#regions-region1) of the Runic
host protocol), and reports each region's size, visibility and focus to its own producer. The producer tells the page
what to show in each. The page never talks to the guest: the guest's page loads in the region's frame and connects to
the guest's own backend. Runic's client is the `<runic-hosted-region>` element of `@runic-artifex/views`.

```json
{"kind":"hosted-region-declare","name":"check","sizing":"fill"}
{"ok":true}
{"kind":"hosted-region-content","name":"check","embedding":"frame","src":"http://[::1]:40117/_runic_frame/Xk2bT9…","title":"System check","allow":""}
{"kind":"hosted-region-state","name":"check","width":800,"height":520,"scale":1.5,"x":0,"y":96,"visible":true,"focused":true,"focusSource":"tab"}
{"ok":true}
```

Requests (page → producer), each answered `{"ok":true}` without other members:

| `kind` | Members |
| --- | --- |
| `hosted-region-declare` | `name`; `sizing`: `"fill"` (the default) or `"content-height"` |
| `hosted-region-remove` | `name` |
| `hosted-region-state` | `name`; `width`, `height`: the region's size in CSS pixels, numbers from 0 to 32,767; `scale`: the page's device pixel ratio, a number from 0.25 to 8; `x`, `y`: the region's offset in the page's viewport in CSS pixels, numbers from −32,768 to 32,767; `visible`, `focused`: booleans; `focusSource`: `"tab"`, `"shift-tab"`, `"pointer"`, `"script"`, or `null` while the region does not hold the focus |

Pushes (producer → the connection that declared the region):

| `kind` | Members | The page |
| --- | --- | --- |
| `hosted-region-content` | `name`; `embedding`: `"frame"`; `src`: the frame launch address; `title`: the guest's title or `null`; `allow`: the frame's permissions policy, at most 1,024 characters | Shows a new frame with that `src`, replacing the region's content |
| `hosted-region-update` | `name`; optional `title`: a string or `null`; optional `height`: the region's height in CSS pixels, an integer from 0 to 32,767 | Applies the members it carries: the frame's accessible name, and with `sizing: "content-height"` the region's height |
| `hosted-region-empty` | `name`; `reason`: `"waiting"` (no Window shows in the region yet), `"detached"` (its Window is detached and may come back) or `"ended"` (its Window closed or its guest went away) | Removes the frame and shows the region's fallback content |
| `hosted-region-focus` | `name` | Moves the keyboard focus into the region, as the host decided ([focus in a region](../desktop/host-protocol.md#focus-in-a-region)) |

- **Names.** `name` is 1 to 64 ASCII letters, digits, dots, underscores or hyphens. A connection declares a name once
  until it removes it, and at most 64 regions at once (`rejected` with `capacity`); a second declaration of a name the
  connection declared, a `hosted-region-remove` or `hosted-region-state` of a name it did not declare, or a member out
  of range, is `rejected` with `invalid-request`. A name names one region of the window: when another connection of
  the window declares a name that a connection declared, the latest declaration wins, and the earlier one's region is
  emptied with `detached`.
- **Producer.** The producer keeps each connection's regions and passes them to the host application (in .NET,
  `RunicHostedRegion`), which decides what each shows. When the connection closes, its regions are removed, as a page
  reload or a crash removes them; the producer cannot tell those apart. `x` and `y` stay with the host: they place
  the host's context menus for the guest, and the host never sends them to a guest.
- **Reports.** A page sends `hosted-region-state` after its declaration and then for each change, coalesced to at most
  one per animation frame: of its size (a `ResizeObserver`), its visibility (an `IntersectionObserver`, and whether the
  element is hidden), its device pixel ratio (`matchMedia` of the current resolution, which also changes with zoom),
  its place and its focus (`focusin` and `focusout`).
- **`focusSource`.** How the focus entered the region: `"tab"` or `"shift-tab"` when the page's last input before it
  was the Tab key without or with Shift, which moved the focus out of the page's document into the frame; `"pointer"`
  when the page has transient user activation (`navigator.userActivation.isActive`) and saw no `pointerdown` or
  `keydown` event of its own in the last 5 seconds, as after a click inside the frame, since user activation reaches
  the ancestor documents; `"script"` in every other case, also where the page cannot tell. A click in the page and
  then, within 5 seconds, into the frame is reported as `"script"`: a false refusal, never a false grant. Focus that a
  `hosted-region-focus` of the producer moved is `"script"` as well. The host counts only `tab`, `shift-tab` and
  `pointer` as the user's ([features in a region](../desktop/host-protocol.md#features-in-a-region)).
- **The frame.** A client shows `src` in an `<iframe>` with `sandbox="allow-scripts allow-same-origin allow-forms"`, the
  `allow` attribute exactly as pushed, `referrerpolicy="no-referrer"`, and `title` from the push unless the page names
  the region itself. It loads each `src` once: a frame launch address is one-time, and loading it again revokes the
  guest page's access. It gives the page no reference to the frame, so a later embedding can replace it. A
  `hosted-region-content` with an `embedding` the client does not know removes the region's content and shows its
  fallback, and the client reports the region as unsupported.
- **Focus.** On `hosted-region-focus`, the client focuses the frame (`iframe.focus()`) only while the page holds no
  focus: its `document.activeElement` is none, its body or the region; otherwise it does nothing, and the focus stays where
  the user has it. When it moved the focus, its next `hosted-region-state` reports it with `focusSource: "script"`, and the
  producer tells the guest's page to focus its own element. The client moves the focus at no other time.

A connection that does not use the feature never receives these pushes; its requests of these kinds are `rejected`
with `invalid-request`. A guest's own page does not use the feature.

## Window turns

The producer serializes the work of one window: the part of a request that changes state, the publication of changes
and the capture of states run one at a time (one **turn** each). An asynchronous command continues in later turns,
so other requests can run while it waits. A state or frame is captured in a turn, so it never shows half of a turn's
changes. How the .NET producer implements turns is in the [language mappings](language-mappings.md#window-turns).

## `receive`

A host script on a transport without targeted pushes sends `{"kind":"receive"}`, with the `session` of the page's
`hello` on a connection that uses [`request-session/1`](#request-sessions). The producer answers
`{"ok":true,"messages":[…]}` with the connection's queued targeted pushes, in order, as soon as at least one is queued,
or with an empty list after 25 seconds. The host script delivers each message to its push listeners and sends the next
`receive`. At most one `receive` per connection is pending; another one answers at once with an empty list. Clients
never send `receive`: it is part of the host script ([mailbox host scripts](#mailbox-host-scripts)). A pending `receive`
answers with an empty list when its connection closes. A producer whose transport pushes to one connection directly
answers `receive` at once with an empty list.

## Message catalogue

Requests:

| `kind` | Members | Success reply |
| --- | --- | --- |
| `hello` | `protocol`, `session`, `client {name, version}`, `features?` | `protocol`, `implementation {name, version}`, `features?`, `blobs?` (`blobs/1`) |
| `open` | `route`, `presentation`, `handlers?` | `revision`, `epoch?` (`presentation-epoch/1`), `state`, `fields?`, `presentation`, `registration?` |
| `snapshot` | `route` | `revision`, `epoch?` (`presentation-epoch/1`), `state`, `fields?` |
| `close` | `route`, `presentation` | `presentation` |
| `set` | `route`, `member`, `value` or `splice` (`text-splice/1`), `edit` | `revision`, `superseded?` |
| `write` | `route`, `member`, `path?` (`field-paths/1`), `requestId`, `expectedVersion`, `expectedValue` and `value`, or `splice` (`text-splice/1`) | `revision`, `receipt` |
| `command` | `route`, `member`, `input?`, `epoch?` (`presentation-epoch/1`) | `revision`, `superseded?` |
| `can` | `route`, `member`, `input?` | `value` |
| `region` (`region-collections/1`) | `route`, `member`, `action`, `key` | `revision`, `outcome` |
| `start` | `route`, `member`, `operation`, `contract`, `input?` | `revision`, `admission`, `reason?`, `status?` |
| `status`, `watch`, `unwatch` | `route`, `member`, `operation`, `contract` | `status` |
| `cancel` | `route`, `member`, `operation`, `contract` | `cancellation` |
| `stream` | `route`, `member`, `operation`, `contract`, `cursor` | `page` |
| `interactions` | `route`, `presentation`, `generation`, `handlers` | `registration` |
| `interaction-reply` | `route`, `presentation`, `requestId`, `ownerEpoch`, `name`, `contract`, `outcome`, `output?` | `result` |
| `view-requests` (`view-requests/1`) | `route`, `presentation`, `generation`, `requests` | `registration` |
| `presented` (`presented/1`) | `route`, `revision` | none |
| `query-rows` (`query-collections/1`) | `route`, `member`, `start`, `count` | `version`, `total`, `start`, `keys`, `rows` |
| `query-set` (`query-collections/1`) | `route`, `member`, `query` | `revision` |
| `query-index` (`query-collections/1`) | `route`, `member`, `key` | `version`, `index` |
| `closing-reply` (`presented/1`) | none | none |
| `flush-reply` (`field-flush/1`) | `id` | none |
| `client-error` (`client-errors/1`) | `source`, `message`, `name?`, `stack?`, `url?`, `route?`, `dropped?` | none |
| `hosted-region-declare` (`hosted-regions/1`) | `name`, `sizing?` | none |
| `hosted-region-remove` (`hosted-regions/1`) | `name` | none |
| `hosted-region-state` (`hosted-regions/1`) | `name`, `width`, `height`, `scale`, `x`, `y`, `visible`, `focused`, `focusSource` | none |
| `receive` | none | `messages` |

Every failure reply is `{ok:false, error, revision?, released?}`, and a `hello` reply also carries `protocol` and
`implementation`. With [`request-session/1`](#request-sessions), every request but `hello` also carries `session`.

Pushes:

| `kind` | Members | Sent to |
| --- | --- | --- |
| `state` | `route`, `revision`, `state`, `fields?`, `epoch?` (`presentation-epoch/1`) | every connection |
| `frame` | `route`, `baseRevision`, `revision`, `changes` | every connection |
| `failure` | `route`, `revision`, `error` | every connection |
| `operation` | `route`, `member`, `operation`, `contract`, `status` | each watching connection |
| `interaction` | `route`, `presentation`, `requestId`, `ownerEpoch`, `name`, `contract`, `expiresAt`, `input` | the presentation's connection |
| `interaction-cancelled` | `route`, `presentation`, `requestId`, `ownerEpoch`, `message` | the presentation's connection |
| `view-request` (`view-requests/1`) | `route`, `presentation`, `revision`, `name`, `contract`, `input` | the connection of each selected presentation |
| `present` (`presented/1`) | `route`, `revision` | while a wait is pending, each connection that uses `presented/1` and has an open presentation of the route |
| `closing` (`presented/1`) | none | each connection that uses `presented/1` |
| `flush` (`field-flush/1`) | `id` | each connection that uses `field-flush/1`, before a departure is decided ([field flush](#field-flush)) |
| `locale` (`translation-text/1`) | `locale` | each connection that uses `translation-text/1`, after its `hello` and on each change |
| `appearance` (`appearance/1`) | `colorScheme`, `contrast`, `reducedMotion`, `textScale`, `accentColor`, `systemColors` | each connection that uses `appearance/1`, after its `hello` and on each change |
| `hosted-region-content`, `hosted-region-update`, `hosted-region-empty`, `hosted-region-focus` (`hosted-regions/1`) | `name` and the members of [hosted regions](#hosted-regions) | the connection that declared the region |

## Blobs and uploads

Feature `blobs/1`. Binary content never crosses the message channel: the producer serves a **blob** at a URL, and the
page sends a file as an **upload**, over HTTP to the host, which a request then names. The wire values are
[`blob` and `upload`](values.md#blobs-and-uploads).

### Blobs

- A `blob` value is `{url, type, size, name}`. The producer writes it in state, in a collection row, in a result or in
  any other value that goes to the page; a page never sends one (a settable member that holds a blob is read-only for
  the page, and a request that sets it is `invalid-input`). Blob values do not depend on the feature: they are part of
  the contract, and a producer writes them on every connection.
- `url` is an absolute URL that the window's host serves with `GET` and `HEAD`, or the empty string when the host
  serves no blobs. It is a **capability**: it contains at least 128 random bits, it is valid only for the window that
  wrote it, and the producer serves it only while a holder keeps it. A state member holds the blobs of its latest
  value: when a later value no longer contains a blob, its URL stops working. A row of a keyed collection holds the
  blobs of its latest write, until it is removed or replaced. The producer MUST NOT release a blob that state or a
  row still holds. A blob in a result or another value is held until the route is released; a producer MAY release
  the least recently written of these earlier, to bound what a long-lived route holds, and its model MAY release any
  blob explicitly. Closing the window
  releases every URL. One blob of the producer's model has one URL per window, so an unchanged member keeps its
  encoding; a blob written again after it was released gets a new URL.
- A response to a blob URL has the blob's `type` as `Content-Type`, `X-Content-Type-Options: nosniff`, a
  `Content-Security-Policy` with `sandbox` and `default-src 'none'` (active content such as HTML or SVG never runs in
  the page's origin when it is opened), `Content-Disposition` `inline` or `attachment` with the blob's `name`,
  `Cache-Control: private, max-age=31536000, immutable` and an `ETag`; the content behind a URL never changes. A blob
  whose length the producer knows answers a single byte range (`Range: bytes=a-b`, `206`, `416`). A released or
  unknown URL answers `404`.

### Uploads

- A connection that uses `blobs/1` gets a grant in the successful `hello` reply when the host takes uploads:
  `blobs: {upload, token, maximumBytes}`. `upload` is the URL of uploads, `token` a secret of this connection (at least
  128 random bits) and `maximumBytes` the largest upload the window takes. A connection that does not list the feature,
  or a host that takes no uploads, gets no `blobs` member.
- The page uploads one file per request: `POST {upload}` with the file as the body, its media type as `Content-Type`,
  the header `X-Runic-Upload-Token: {token}` and `X-Runic-Upload-Name` with the file name, percent-encoded as UTF-8.
  The host answers `201` with `{"ok": true, "upload": {id, name, type, size}}`, or an error
  `{"ok": false, "error": {kind, reason, message}}`: `403` (`forbidden`, a missing or wrong token or origin), `413`
  (`too-large`, larger than `maximumBytes`), `429` or `507` (`capacity`, the window receives too many uploads at once,
  or its unclaimed uploads fill its space), `408` (`timeout`, the body sent nothing for 30 seconds), `400`
  (`invalid-request` or `cancelled`), `410` (`released`, the connection or window closed meanwhile) and `500`
  (`failed`, the host could not store it). An upload that fails in any way, including a client that goes away, keeps
  none of the window's upload slots or space.
- An `upload` value names an upload by its `id`. It decodes only in a request of the connection that uploaded it, and
  only while no request claimed it; any other id is `invalid-input`. The producer's model claims an upload when it
  reads it; afterwards the id is invalid. An unclaimed upload is deleted when its connection closes, when the window
  closes, or 10 minutes after it arrived. Everything about an upload — its bytes, type and name — comes from the page
  and is untrusted: the producer reduces the name to a file name.

### What a host enforces

Every host checks the `Host` of a loopback listener (no DNS rebinding), takes uploads only as `POST` from the page's
origin with the connection's token, and refuses a blob request whose fetch metadata (`Sec-Fetch-Site: cross-site`)
names another site. Beyond that:

| Host | Blob URLs | Uploads |
| --- | --- | --- |
| Runic Desktop and WPF | served below the window's surface (`{surface}/_runic/blobs/{token}`, same origin as the page); only to the browser client that holds the window's client cookie, and in an installed browser only to the client bound to its launch code | `{surface}/_runic/upload`; also only from that client |
| CS-WebUI | a loopback listener of the window (`http://localhost:{port}/_runic/blobs/{token}`), another origin than WebUI's page, which it grants with CORS; it holds both loopback addresses that `localhost` names on its port (`::1` and `127.0.0.1`), so no other process can listen on either and receive a blob load, an upload or its token; it serves loopback peers only and cannot see WebUI's clients, so a URL is bound to the window, not to a connection or browser | the listener's `/_runic/upload`, with CORS for the page's origin only |
| Tests (`Runic.Application.Testing`) | served in memory below `http://runic.test/` | simulated with `UploadAsync` |

A blob URL cannot be bound to one connection, because one encoding of a state goes to every connection of the
window; the token of an upload is.

## Versions and compatibility

The protocol version is strict: a client and a producer of different versions do not connect
([Handshake](#handshake)). Within a version, additions never change it:

- Both sides ignore members they do not know.
- A client ignores push kinds it does not know.
- A client recovers from a frame with a change kind it does not know ([state changes](state-changes.md#applying-frames)),
  so new change kinds are additive.
- A producer answers a request kind it does not know with `rejected` and `invalid-request`.
- `reason` values and diagnostic codes are open sets.

The version changes only when a client built for the previous version could misread a message. Additions that both
sides must implement are [features](#features), listed in `hello`.

| Version | Release | Change |
| --- | --- | --- |
| 1 | 0.6 | Named routes with a snapshot and a state callback per route; collection delta frames were added in 0.7 without a version change. |
| 2 | 0.9 | The `domain-failed` error and operation status kind. |
| 3 | 0.10 | A released View answers `released` instead of `disconnected`; the reply error `reason`; a strict version check ([protocol 3](https://github.com/Runic-Artifex/runic-sdk/blob/v0.12.1/specs/application/README.md)). |
| 4 | 0.13 | One message channel with `hello`; `set` changes in frames; replies by revision; edit numbers; `open`/`close`; operation `watch` and `progress`; RFC 8785 canonical form; value types named by value; ISO 8601 durations; a language-neutral specification and conformance corpus. 0.14 added, without a version change, `hello` `features` with the first feature `text-splice/1`, and requests held behind the `hello`. 0.15 added, without a version change, the features `presentation-epoch/1` and `presented/1`, and 0.16 the features `view-requests/1`, `region-collections/1`, `query-collections/1`, `validation-pending/1` and `translation-text/1`, and the value type `text-reference`, and 0.17 the features `appearance/1` and `client-languages/1`, and 0.18 the feature `hosted-regions/1`. |
