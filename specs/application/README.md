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
| This document | Conventions, transport, host bridge, handshake, requests and replies, state and revisions, publication, edits, checked writes, commands, presentations, operations, interactions, versions |
| [state-changes.md](state-changes.md) | Frames and their changes (`set`, keyed collections, text), producing and applying them, delivery bounds and recovery |
| [values.md](values.md) | The JSON profile, wire value types and the canonical form (RFC 8785) |
| [language-mappings.md](language-mappings.md) | The .NET producer and the TypeScript client |
| [conformance/](conformance/README.md) | Vectors and scenarios |
| [protocol-3.md](protocol-3.md), [collection-deltas.md](collection-deltas.md) | Protocol 3, which the implementations still speak until protocol 4 lands |

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
  page reports its protocol diagnostic.
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
| `runic.views.connection` | `globalThis` | `{ listeners, state, requested, start }`: a `Set` of listener functions called with the page's `BridgeConnectionState` on each change, and the latest state. The first View that connects starts it. A copy of `@runic-artifex/views` sets `start`, a function that starts it, when it loads; a reader that wants the state before a View connects (the Vite development client) sets `requested` to `true`, which a copy that loads later honours, and calls `start` if it is set. |
| `runic.views.diagnosticReported` | An error | `true` once a diagnostic reported the error, so each error is reported once. |
| `runic.views.unavailableReported` | `globalThis` | `true` once the page reported that it has no `__runicBridge`. |
| `runic.views.BridgeError`, `runic.views.BridgeOperationUncertainError` | An error | `true` on errors of that class, so `instanceof` holds across copies. |
| `runic.views.BridgeOutcome` | An outcome | A non-enumerable `true` that `isBridgeOutcome` checks. |
| `runic.views.mockFailure` | An error | The declared failure of an error from `mockFailure()` (`@runic-artifex/views/mock`). |

## Handshake

The first request on each connection is `hello`:

```json
{"kind":"hello","protocol":4,"session":"6f1c2a9e-0d4b-4c51-9a83-5d2f7e1b0c44","client":{"name":"@runic-artifex/views","version":"0.13.0"}}
{"ok":true,"protocol":4,"implementation":{"name":"Runic.Application.Views","version":"0.13.0"}}
```

| Member | Contract |
| --- | --- |
| `protocol` | The protocol version the sender implements: `4`. |
| `session` | An identifier the client chooses for this connection. A client MUST send a new session for each connection. |
| `client`, `implementation` | `{name, version}`: the name of the client or producer implementation (1 to 128 characters) and its version (1 to 64 characters, SemVer without build metadata recommended). They are informational. |

Producer rules:

- Every reply to a `hello`, successful or not, carries the producer's `protocol` and `implementation`.
- A `hello` whose `protocol` is not 4 is answered
  `{"ok":false,"protocol":4,"implementation":{…},"error":{"kind":"failed","reason":"protocol-mismatch","message":"…"}}`.
  The producer logs this once per connection, naming both versions.
- A `hello` without a valid `protocol`, `session` or `client` is `rejected` with `invalid-request`. So is a second
  `hello` on a connection that already completed one.
- A `hello` whose session a closed connection used is `released`.
- A `hello` whose session another open connection uses is `rejected` with `invalid-request`.
- Until a `hello` succeeds, every other request is `rejected` with the reason `no-hello`, and the connection receives
  no pushes.

Client rules:

- It sends `hello` before any other request on a connection, and again after each new connection.
- It refuses a producer whose reply has a `protocol` other than its own, or a successful reply without `protocol`:
  every pending and later call of that connection rejects with one `failed` error whose message names both versions
  and the fix (use the producer and client of one release, rebuild so the generated clients are regenerated, and
  reload). It reports this with one diagnostic with code `protocol`, once per page, and with no other diagnostic, and
  sends no `open`.
- A client MAY compare `implementation.version` with its own release when `implementation.name` names the producer it
  is released with. `@runic-artifex/views` reports a diagnostic with code `version` when `implementation.name` is
  `Runic.Application.Views` and the version differs from its own, once per page, and keeps working. It reports no
  version diagnostic for other producers.

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
| `invalid-request` | `rejected` | The request is not a request of this protocol: an unknown `kind`, a missing member or a member of the wrong JSON type, or a `member` that the route does not have for that kind. |
| `no-hello` | `rejected` | The connection has not completed a `hello`. |
| `protocol-mismatch` | `failed` | A `hello` named another protocol version. |
| `invalid-input` | `rejected` | A value or input does not decode as its type ([values](values.md#wire-value-types)), the setter or command rejects it as invalid input, or the request is larger than the input limit. |
| `unavailable` | `rejected` | A command is not available ([Commands](#commands-and-availability)). |
| `unresolved-key` | `rejected` | The key of a key command resolves to nothing. |
| `capacity` | `rejected` | A limit of the window or connection is reached: 64 awaited commands, 64 presentations per route and connection, or 64 watches per connection. |
| `state-withheld` | any | The request ran, but the producer cannot publish a state that includes its effects, because a keyed collection has invalid keys ([state changes](state-changes.md#invalid-keys)). |

A request that names a route the producer does not serve, because it was never bound or because it was released, is
answered `{"ok":false,"error":{"kind":"released","message":"This View was released."}}`. A producer cannot tell those
cases apart, and neither can the client. A client reports a `released` reply on every request kind as `released`: the
producer answered, so the transport is connected and only the route is gone.

### Revisions in replies

Every reply to a request that changes state carries `revision`: `set`, `write`, `command` and `start`, whether they
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
([language mappings](language-mappings.md#failure-detail)).

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
`interactions` and `interaction-reply` do not wait.

### Client resolution

A client resolves a call whose reply carries `revision` R once the call's route has accepted a revision of R or more.
Normally that has already happened when the reply arrives.

- If a recovery read of the route is in flight, the call resolves after it.
- If the route is not open, the call resolves at once.
- If the route still has not reached R 5 seconds after the reply, the client reads a `snapshot` of the route, resolves
  the call after it, and reports a diagnostic with code `revision-timeout`, because a correct producer cannot cause it.

## Publication

The producer pushes the state of its routes to every connection that completed `hello`. A client ignores pushes for
routes it has not opened, and pushes of a kind it does not know.

| Push | Members | Meaning |
| --- | --- | --- |
| `state` | `route`, `revision`, `state`, `fields?` | A full state. |
| `frame` | `route`, `baseRevision`, `revision`, `changes` | Changes on top of the state at `baseRevision`. See [state changes](state-changes.md). |
| `failure` | `route`, `revision`, `error` | A publication was withheld because a keyed collection has invalid keys ([state changes](state-changes.md#invalid-keys)). `revision` is the producer's current revision; it does not change the client's. |

**Order of pushes.** On each connection, the producer sends the `state`, `frame` and `failure` pushes that a window
turn published before the `operation` and `interaction` pushes that the same turn caused. So the terminal status of an
operation follows the state or frame at its `revision`, and an interaction request follows the frame that shows its
command executing. A transport with a [mailbox](#receive) does not order the two channels relative to each other.

A full state is sent only when a route needs one: as the first state of a route on a connection (the `open` and
`snapshot` replies), after the delivery queue overflowed, or when the producer chooses one, which is always allowed.
Every other change is a frame. The delivery bounds and the recovery rules are in
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

### Client overlay

A client that exposes state to a user interface MUST apply this rule; a client without one MAY.

- While a member has an edit whose reply has not arrived, or whose reply revision the route has not reached yet, the
  client exposes the member's latest edit value instead of the state's.
- It exposes the state's value again once the route has reached the reply revision of the member's latest edit, or
  when the latest edit failed. A failed edit also rejects the setter's call.
- A reply marked `superseded` does not end the overlay: the latest edit is still on its way.
- Subscribers are notified whenever the exposed value changes, including when the overlay starts or ends.
- The overlay applies to setters only. Checked writes keep their baseline semantics, and commands are not tied to a
  member.

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
    it, including any normalization by the setter, and `validation` a message when validation after the write failed
    without undoing it;
  - `{kind:"committed-with-error", snapshot, message}`: the setter changed the field and then failed;
  - `{kind:"conflict", incoming:{value, version}, message}`: the field's version or value differed; `incoming` is the
    current field;
  - `{kind:"rejected", message}`: the setter refused the value, and the field is unchanged.
- A retry with the same `requestId` whose `expectedVersion`, `expectedValue` and `value` are canonically equal to the
  original receives the original receipt and does not apply the write again. Reusing a `requestId` for another write
  answers `conflict`. A producer retains receipts for a bounded number of request ids.
- The reply carries `revision` like a setter's. A value that does not decode is `rejected` with `invalid-input`.

## Commands and availability

| Request | Members | Reply |
| --- | --- | --- |
| `command` | `route`, `member`, `input?` | `{ok:true, revision}` after the command completed |
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

## Content

A View can present other Views in content slots. Each presented object and kind has a **content reference**
`{kind, id}` with an `id` of 32 lowercase hexadecimal digits that stays the same for the window, including while the
object is suspended. Its route is `content{id}`.

Content is attached while it is presented. Replacing, clearing or pruning it, or its parent's release, suspends it
when no other slot presents it. A suspended object releases its route, its Views and its checked fields, so its
requests answer `released`; only its identity remains. The producer MAY forget the identity later, and a forgotten
object gets a new id when it is presented again.

## Presentations

A client registers each route it shows with `open`, and ends the registration with `close`:

| Request | Members | Reply |
| --- | --- | --- |
| `open` | `route`, `presentation`, `handlers?` | `{ok:true, revision, state, fields?, presentation, registration?}` |
| `close` | `route`, `presentation` | `{ok:true, presentation}` |
| `snapshot` | `route` | `{ok:true, revision, state, fields?}` |

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
- A suspended route whose presentations are still open keeps them. When its object is presented again before the
  page closed them, as when one command sets `Main = b; Main = a;` and the page never saw `b`, the open presentations
  resume on the new View in a later turn of the window.

**Reconnects.** After a new connection, a client sends `hello` with a new session and then `open` for each route it
shows, with the same presentation ids. While the closed connection's presentations are not yet released, the reply
can be `ignored`; the client retries `open` until it answers `ok`. Interaction handlers resume with the new
registration.

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
- A start reply always carries `revision`, the route's last published revision when nothing changed.

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
([language mappings](language-mappings.md#operations)). The producer clamps reported values to the range and ignores
values that are not numbers. `progress` stays absent until the first report.

### Limits and window close

- A window retains at most 64 operations, 32 terminal results, 128 expired ids and 256 KiB of results, declared
  failures and stream replay. Running streams reserve their bound against a 256 KiB window budget; the default stream
  bound is 128 items and 64 KiB. An overflowing stream is terminal and reports `stream-overflow`.
- Closing a window stops admission: `start` answers `owner-closing`, and an asynchronous `command` answers `released`.
  The window waits up to its close timeout for running operations and asynchronous commands, then requests the
  cancellation of what remains, and keeps its resources until that work ends.

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

**Scope.** Only work of the same window can reach the page, and a request never crosses to another window, client or
connection. Two scopes exist:

- A command invocation reaches only the single presentation of the invoking client and connection. It reaches the
  invoking route, or, while a navigation hook that the command started runs (such as the leave guard of the page that
  the shell's Back departs, or the activation hook of the page that its navigation made current), another route of the
  same window opened by that client and connection.
- The window's close decision (a user's close or a close the application requests) reaches the single presentation of
  the window that registered the handler, from any connection. With none or several, it asks nothing.

Work outside both scopes, such as navigation that application code starts, never reaches the page. A request ends
when its scope ends, its presentation closes or its connection closes, or the window closes; the waiting interaction
then fails. Interactions time out after two minutes by default (at most ten). A window holds at most 32 pending
requests and 8 per presentation.

## Window turns

The producer serializes the work of one window: the part of a request that changes state, the publication of changes
and the capture of states run one at a time (one **turn** each). An asynchronous command continues in later turns,
so other requests can run while it waits. A state or frame is captured in a turn, so it never shows half of a turn's
changes. How the .NET producer implements turns is in the [language mappings](language-mappings.md#window-turns).

## `receive`

A host script on a transport without targeted pushes sends `{"kind":"receive"}`. The producer answers
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
| `hello` | `protocol`, `session`, `client {name, version}` | `protocol`, `implementation {name, version}` |
| `open` | `route`, `presentation`, `handlers?` | `revision`, `state`, `fields?`, `presentation`, `registration?` |
| `snapshot` | `route` | `revision`, `state`, `fields?` |
| `close` | `route`, `presentation` | `presentation` |
| `set` | `route`, `member`, `value`, `edit` | `revision`, `superseded?` |
| `write` | `route`, `member`, `requestId`, `expectedVersion`, `expectedValue`, `value` | `revision`, `receipt` |
| `command` | `route`, `member`, `input?` | `revision` |
| `can` | `route`, `member`, `input?` | `value` |
| `start` | `route`, `member`, `operation`, `contract`, `input?` | `revision`, `admission`, `reason?`, `status?` |
| `status`, `watch`, `unwatch` | `route`, `member`, `operation`, `contract` | `status` |
| `cancel` | `route`, `member`, `operation`, `contract` | `cancellation` |
| `stream` | `route`, `member`, `operation`, `contract`, `cursor` | `page` |
| `interactions` | `route`, `presentation`, `generation`, `handlers` | `registration` |
| `interaction-reply` | `route`, `presentation`, `requestId`, `ownerEpoch`, `name`, `contract`, `outcome`, `output?` | `result` |
| `receive` | none | `messages` |

Every failure reply is `{ok:false, error, revision?, released?}`, and a `hello` reply also carries `protocol` and
`implementation`.

Pushes:

| `kind` | Members | Sent to |
| --- | --- | --- |
| `state` | `route`, `revision`, `state`, `fields?` | every connection |
| `frame` | `route`, `baseRevision`, `revision`, `changes` | every connection |
| `failure` | `route`, `revision`, `error` | every connection |
| `operation` | `route`, `member`, `operation`, `contract`, `status` | each watching connection |
| `interaction` | `route`, `presentation`, `requestId`, `ownerEpoch`, `name`, `contract`, `expiresAt`, `input` | the presentation's connection |
| `interaction-cancelled` | `route`, `presentation`, `requestId`, `ownerEpoch`, `message` | the presentation's connection |

## Versions and compatibility

The protocol version is strict: a client and a producer of different versions do not connect
([Handshake](#handshake)). Within a version, additions never change it:

- Both sides ignore members they do not know.
- A client ignores push kinds it does not know.
- A client recovers from a frame with a change kind it does not know ([state changes](state-changes.md#applying-frames)),
  so new change kinds are additive.
- A producer answers a request kind it does not know with `rejected` and `invalid-request`.
- `reason` values and diagnostic codes are open sets.

The version changes only when a client built for the previous version could misread a message.

| Version | Release | Change |
| --- | --- | --- |
| 1 | 0.6 | Named routes with a snapshot and a state callback per route; collection delta frames were added in 0.7 without a version change. |
| 2 | 0.9 | The `domain-failed` error and operation status kind. |
| 3 | 0.10 | A released View answers `released` instead of `disconnected`; the reply error `reason`; a strict version check ([protocol 3](protocol-3.md)). |
| 4 | 0.13 | One message channel with `hello`; `set` changes in frames; replies by revision; edit numbers; `open`/`close`; operation `watch` and `progress`; RFC 8785 canonical form; value types named by value; ISO 8601 durations; a language-neutral specification and conformance corpus. |
