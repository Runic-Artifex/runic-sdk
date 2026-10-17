# Runic Application Views wire protocol

This is the protocol, version 2, between a .NET window session
(`WindowContentSession` and generated `ViewModelBridge` subclasses) and the
generated TypeScript clients, as implemented by `Runic.Application.Views`. The
previous preview Application Bridge protocol and its Bridge IR are retired.

Generated code is the only supported producer and consumer. The protocol is
documented so host adapters, generated clients and the runtime can change
together; it is not an application API. The independent MessageFormat 2
specification is maintained by [Runic Translations](https://github.com/Runic-Artifex/runic-translations-sdk).

## Version

Every reply from a snapshot route carries `"protocol": 2`; command, setter and
write replies do not. The version is informational: generated clients do not
gate on it, and report a snapshot reply whose `protocol` differs from their own
as a diagnostic, in every environment and once per Bridge. Clients ignore envelope members they do not
know, so adding members is compatible. The version changes only when a client
built for the previous version could misread a reply. Version 2 added the
`domain-failed` error and operation status kind.

## Transport

A host transport (`IBridgeTransport`) offers named routes and one-way state
publication:

- A route handler receives positional arguments (`GetString`, `GetInt64`,
  `GetBoolean`) and returns one string. Structured arguments are JSON strings.
- The transport supplies trusted `ClientKey` and `ConnectionKey` values. They
  are never read from request JSON. CS-WebUI uses the client id and
  `clientId:connectionId`; Desktop uses its connection id for both, so a
  reconnect has new keys.
- `Publish(route, state)` makes the browser call `__{route}Changed(state)`.
  Full snapshots coalesce: a newer state may replace one not yet sent, and a
  state is serialized when the host can take it rather than on every change.
  Because that capture runs on the delivery thread, a snapshot writer that
  throws (including `ObjectDisposedException` from a getter) no longer throws
  to the code that raised `PropertyChanged`. The failure is traced with
  `Trace.TraceError`, nothing is published, and the next change requests a
  new capture. Route replies still capture immediately and report a writer
  failure to the caller.
  Dependent collection deltas remain ordered. An `IAsyncBridgeTransport`
  acknowledges each delivery outside the model context before the next frame
  is sent; Desktop implements this path.

The browser host script defines `__runicBridge`:

| Member | Contract |
| --- | --- |
| `isConnected(): boolean` | The host connection is authenticated. |
| `call(name, ...args): Promise<string>` | Invokes a route. Rejects if the transport fails. |
| `onReconnect?(listener): () => void` | Optional. Calls `listener` after the connection is re-established without a page reload; returns an unsubscribe function. |

A route unbound by `RebindableBridgeTransport` answers the envelope
`{"ok":false,"state":null,"error":{"kind":"disconnected",...}}`. Runic Desktop
answers a late call of a capability it released shortly before (it remembers the
last 256) with the same envelope. A client reports it on every route, including
`Can`, `Mount` and `Start` routes, as `disconnected`. Runic Desktop answers
other calls it cannot serve with an empty reply: a capability that was never
admitted, an undecodable call and a handler that threw. No Views route replies
with an empty string, so a client reports an empty reply as an invalid response
(`failed`).

## Routes

A bridge's route prefix is its root ViewModel's camel-cased short name (for
example `counter`), or `content{id}` for presented content, where `id` is the
32-digit hex id of its page reference.

| Route | Arguments | Reply |
| --- | --- | --- |
| `{p}Snapshot` | none | envelope with `protocol` |
| `{p}Set{Property}` | value | envelope |
| `{p}Write{Property}` | `{requestId, expectedVersion, expectedValue, value}` | envelope with `receipt` |
| `{p}{Command}` | input, if any | envelope after the command completes |
| `{p}Start{Command}` | `requestId`, or `{requestId, input}` | operation admission |
| `{p}Can{Command}` | input, if any | `"true"` or `"false"` |
| `{p}Mount`, `{p}Unmount` | mount token | see [Mounts](#mounts) |
| `__runicOperationStatus`, `Wait`, `Cancel` | `{contract, requestId, member?}` | operation status or cancel result |
| `__runicOperationStream` | `{contract, requestId, member?, cursor}` | stream page |
| `__runicInteractionWait`, `Reply`, `Control`, `ControlWait` | see [Interactions](#interactions) | `{kind, ...}` |

`Start` routes exist only for asynchronous commands of a bridge attached to a
window session with a generated contract fingerprint. Root `Mount` and `Unmount`
routes exist only for a root bridge that exposes interactions.

## Reply envelope and state

```json
{ "ok": true, "state": { "revision": 12 }, "error": null, "protocol": 2 }
```

`error` is `{kind, message}` with `kind` one of `rejected`, `cancelled`,
`failed`, `domain-failed` or `disconnected`; `ok` is false exactly when `error`
is set. In development (`BridgeDiagnostics`), an error caused by an exception
adds `detail: {type, message, stack}`; production replies never carry it.

`domain-failed` is a command's declared failure (`[RunicFailure]`, thrown as
`RunicFailureException`): `error` adds `failure`, the encoded value of the
declared type, and never has `detail`. The reply carries the state after the
call. A thrown value that is not the declared type, cannot be encoded or
encodes to more than 4 KiB of UTF-8 is `failed`.

When a setter or command ran but the state after it cannot be sent because of
invalid collection keys, the reply has `state: null` and a normative message,
which clients may match:

- a call that succeeded replies `failed` with
  `"The call ran, but the updated state could not be sent: <reason>"`;
- a call that failed keeps its kind (including `domain-failed` with `failure`)
  and its message followed by `" The updated state could not be sent: <reason>"`;
  a `domain-failed` reply still has no `detail`;
- a checked write replies `failed` with
  `"The field write was processed, but the updated state and receipt could not be sent: <reason>"`.

Clients report a `domain-failed` reply without state as an error only when its
message contains `" The updated state could not be sent: "`; otherwise the
Bridge was detached.

```json
{ "ok": false, "state": { "revision": 13 }, "error": { "kind": "domain-failed", "message": "Save failed.", "failure": { "$case": "titleRequired" } } }
```

A call can release its own bridge before its reply captures a state, as a page's
command that replaces or leaves the page in its navigation region does. The reply
then has `state: null` and `released: true`: a call that succeeded stays `ok`, and
a failed call keeps its `error`. A client resolves a successful `released` reply
with the last state it accepted; a successful reply without state and without
`released` is invalid. A snapshot read of a released bridge replies
`disconnected`, and so does a checked write, which adds `released: true` too.

`state` is also null when the bridge was detached or closed before the call ran;
such a reply is `disconnected`. A checked write adds `receipt`:
`applied` or `committed-with-error` with `snapshot: {value, version}`,
`conflict` with `incoming`, or `rejected` with `message`.

State is a JSON object with `revision` and the generated members. A DTO in
state, a result or a failure has every member, except a `JsonElement` member
that holds no value, which is absent. A reader of a value the page sends fills
in a missing member that is nullable or whose constructor parameter has a
default, and rejects any other missing member. Content is
`{kind, id}` or null; content collections are arrays of references. Checked
fields add hidden metadata, `__runicFields: {<wire name>: {version}}`, which a
client strips before exposing state.

### Revisions

Revisions of a bridge attached to a window session come from one counter per
window. Each notification takes the next value, so a route's revisions only
increase, including when a route is re-attached to a new bridge, but they are
not contiguous. A reply captures the current revision without advancing it. A
client accepts a state whose revision is at least the one it holds and ignores
an older one. A snapshot batch defers capture, never the revision.

## Collection deltas

An opt-in `[RunicCollection]` read-only DTO collection publishes indexed
`add`, `remove`, `replace` and `move` changes as frames with `__runicDelta: 1`,
`baseRevision` and `revision`, instead of full states. A client applies a frame
only on top of its current revision and otherwise reads one snapshot. Batches
above 4,096 changes, delivery queues above 64 frames or 1 MiB, resets and mixed
changes fall back to full states. A `[RunicAppendOnlyText]` string, such as a
live log, publishes `text` changes in the same frames: the number of characters
dropped from its start and the text appended.
[Collection delta frames](collection-deltas.md) specifies the frame format, the
fallback and recovery rules, and the shared conformance fixtures.

## Content

`Present`, `PresentItem` and `Expose` give each (object, kind) a page reference
that stays the same for the window, including while the object is suspended.
Content is attached while presented: replacing, clearing or pruning it, or its
parent's disposal, suspends it when no other slot presents it. A suspended object
releases its bridge, Views, model-context binding and checked-field registries;
only its weak identity remains. `Forget` also drops the identity.

## Mounts

A generated client mounts each presentation of a content reference with a token
`{browserSession}:{presentation}`, unique to the page and presentation.

- `Mount` answers `ok` (also for an exact duplicate), `invalid` for a malformed
  token, `ignored` when another client or connection owns the token, and
  `disconnected` when the route is suspended or the token's browser session was
  used by a connection that has since closed. A new browser session from the same
  client supersedes that client's earlier tokens.
- `Unmount` answers `ok`, `ignored` for another owner's token, or `disconnected`.
- Each mounted token gets a fresh .NET View. A disconnect releases the tokens of
  that connection.
- A suspended reference whose tokens are still mounted keeps its `Mount` and
  `Unmount` routes. A browser that removed the reference unmounts normally, which
  retires the routes. If the reference is presented again first, as when one
  command sets `Main = b; Main = a;` and the browser never saw `b`, the retained
  tokens resume on the new bridge with fresh Views in a later model turn.
- After `onReconnect`, a client re-reads each live route's snapshot, starts a new
  browser session and mounts each mounted presentation again from the new
  connection as `{newBrowserSession}:{presentation}`, retrying while it is
  `ignored`. A CS-WebUI reconnect can keep the connection key, so re-sending the
  former token would be `disconnected`: .NET cannot tell it from a late call of
  the closed connection. Interaction handlers resume under the new token.

## Operations

A `Start` route admits a recoverable operation and answers

```json
{ "contract": "...", "requestId": "...", "kind": "accepted", "status": "running", "reason": null, "terminal": null }
```

`kind` is `accepted`, `duplicate`, `expired` or `rejected`; `reason` names a
rejection (`owner-closing`, `owner-disposed`, `identity-conflict`, `capacity`,
`unavailable`, `stream-capacity`). A failed admission before identity checks
answers `{kind, reason}`, with `detail` as in a reply error; it is `disconnected`
when the bridge was released, and a client reports it so. `contract` is
`{ViewModel full name}:{fingerprint}:{route prefix}`. A request id is bound to the
command member and the canonical input digest: reusing it for other work is
`identity-conflict`, and a retry observes the original operation.

A status is `{contract, requestId, kind}` with `kind` one of `running`,
`succeeded`, `failed`, `domain-failed`, `cancelled`, `expired`, `unknown`.
Success may add `result`, `delivery: {kind, message}` (`result-too-large`,
`result-encoding-failed`, `stream-overflow`, `stream-retention-too-large`) and
`stream: true`; failure adds `error: {kind, message}`, with `detail` in
development. `domain-failed` adds `failure` and may add `delivery` and
`stream: true`, but never `error` or `detail`. It is classified by exception
type, so a declared failure thrown while cancellation is requested is still
`domain-failed`. A stream operation that fails as declared keeps the values it
published; its pages report `kind: "domain-failed"` and `completed: true`. A cancel answers `cancellation-requested`,
`not-running`, `unknown` or `expired`; cancellation never rewrites a success. A
stream page adds `cursor`, `completed`, `items: [{sequence, value}]` and
`delivery`. Malformed requests answer `{kind: "invalid-request"}`.

A window retains at most 64 operations, 32 terminal results, 128 expired ids, and
256 KiB of results, declared failures and stream replay. A declared failure that
does not fit stays `domain-failed` (the command did fail and must not be retried
blindly) without `failure` and with `delivery: {kind: "result-too-large"}`. Running streams reserve their bound against
a 256 KiB window budget; the default stream bound is 128 items and 64 KiB. An
overflowing stream is terminal and visible as `stream-overflow`.

Awaited command routes are window work as well. Closing a window stops admission
(`Start` answers `owner-closing`, an awaited route answers `disconnected`), waits
up to its timeout for operations and awaited commands, and then requests
cancellation of what remains. The host keeps its DI scope until that work ends.

## Interactions

A mounted presentation polls `__runicInteractionWait` with
`{route, presentationId, handlers: [{name, contract}], generation?}`. The reply
is a request

```json
{ "kind": "request", "requestId": "...", "route": "...", "presentationId": "...", "ownerEpoch": 1,
  "name": "...", "contract": "...", "expiresAt": "...", "input": {} }
```

or `{kind}` with `cancelled`, `disconnected`, `ignored`, `unsupported`, `stale`
or `invalid-request`. The browser answers on `__runicInteractionReply` with the
request identity, `kind` `answered` (with `output`), `cancelled` or `failed`. The
reply route answers `ok`, `already-completed` for an identical retry, `stale`,
`invalid-request`, `disconnected`, or `invalid-output` when the generated decoder
rejects `output`; the waiting interaction then fails with
`RunicInteractionOutputException`. `__runicInteractionControl` registers
capabilities with `{route, presentationId, generation, handlers}`;
`__runicInteractionControlWait` delivers
`{kind: "cancelled", requestId, route, presentationId, ownerEpoch, reason}`.

Only a trusted invocation scope of the same window session can reach the
browser; a request never crosses to another window session, client or
connection. Two scopes exist:

- A bridge command invocation reaches only the single presentation of the
  invoking client and connection. It reaches the invoking route, or, while a
  navigation hook that the command started runs (such as the leave guard of the
  page that the shell's Back departs, or the activation hook of the page that
  its navigation made current), another route of the same window mounted by
  that client and connection.
- The window's close decision (`CanCloseAsync` for a user close or
  `RequestCloseAsync`) reaches the single presentation of the window with the
  handler mounted, from any connection. With none or several, it asks nothing.

Work outside both scopes, such as navigation that .NET code starts, never
reaches the browser. A request ends when its scope's token or the navigation
hook's token is cancelled, its presentation unmounts or disconnects, or the
window closes; the awaiting interaction then fails. Interactions time out after
two minutes by default (at most ten); a window holds at most 32 pending requests,
8 per presentation, and payloads up to 64 KiB.

## Model context

Routes are synchronous. A bridge runs its work inline when its model context
reports `IsExecuting`, and otherwise blocks until `InvokeAsync` completes.
Content exposed in a window binds to the window graph's context while attached.
Teardown runs inline when an application-owned context was disposed first.

A requested full state is captured by the delivery thread in a model turn.
With a model context that turn is the context's; without one, the capture
runs on a thread-pool thread while holding the model gate that also serializes
routes and change notifications. Snapshot writers and the getters they call
must therefore not assume the thread that raised `PropertyChanged`.

## Canonical values

Checked-field equality and receipts, operation input digests, interaction
reply signatures and `BridgeWire.EncodeCanonical` share one canonical JSON form:
no whitespace; object members sorted by ordinal (UTF-16) name; numbers written
by exact decimal value in the ECMAScript `Number::toString` layout (`1.0`,
`1e0` and `10e-1` are `1`, `-0` is `0`, nothing is rounded); strings re-escaped
by the default `Utf8JsonWriter` encoder. An object with a duplicate member name
is rejected, as is a number whose decimal exponent exceeds ±100,000. Checked-field
equality treats byte-identical writer output as equal without validating it.

## Compatibility

- A wait request without `generation` (generation 0) replaces capabilities on
  each poll, but never overrides a registration with a later generation.
- `member` is optional on operation recovery routes; generated clients send it.
- A page runtime created by an earlier generated client gains new runtime
  fields when a newer module loads.
- Clients that predate `protocol` or `onReconnect` ignore them.
- Clients treat a reply error `kind` they do not know as `failed`. A generated
  client also treats `domain-failed` as `failed` for a command or operation
  that declares no failure, so a newer host cannot surprise an older client.

Portable fixtures under [`fixtures/domain-failures`](fixtures/domain-failures)
pair the wire .NET writes for declared failures with the outcome a declared
client reports; the .NET tests and `@runic-artifex/views` check both sides.
