# Runic Application Views wire protocol

This is the protocol, version 1, between a .NET window session
(`WindowContentSession` and generated `ViewModelBridge` subclasses) and the
generated TypeScript clients, as implemented by `Runic.Application.Views`. The
previous preview Application Bridge protocol and its Bridge IR are retired.

Generated code is the only supported producer and consumer. The protocol is
documented so host adapters, generated clients and the runtime can change
together; it is not an application API. The independent MessageFormat 2
specification remains under [`specs/translations`](../translations/README.md).

## Version

Every reply from a snapshot route carries `"protocol": 1`. Clients ignore
envelope members they do not know, so adding members is compatible. The version
changes only when a client built for the previous version could misread a reply.

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
  Delivery coalesces: a newer state of a route may replace one not yet sent.

The browser host script defines `__runicBridge`:

| Member | Contract |
| --- | --- |
| `isConnected(): boolean` | The host connection is authenticated. |
| `call(name, ...args): Promise<string>` | Invokes a route. Rejects if the transport fails. |
| `onReconnect?(listener): () => void` | Optional. Calls `listener` after the connection is re-established without a page reload; returns an unsubscribe function. |

A route unbound by `RebindableBridgeTransport` answers the envelope
`{"ok":false,"state":null,"error":{"kind":"disconnected",...}}`.

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
{ "ok": true, "state": { "revision": 12 }, "error": null, "protocol": 1 }
```

`error` is `{kind, message}` with `kind` one of `rejected`, `cancelled`,
`failed` or `disconnected`; `ok` is false exactly when `error` is set. `state`
is null when the bridge was detached or closed. A checked write adds `receipt`:
`applied` or `committed-with-error` with `snapshot: {value, version}`,
`conflict` with `incoming`, or `rejected` with `message`.

State is a JSON object with `revision` and the generated members. Content is
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
- After `onReconnect`, a client re-reads each live route's snapshot and re-sends
  each mounted token from the new connection, retrying while it is `ignored`
  because .NET has not yet released the former connection.

## Operations

A `Start` route admits a recoverable operation and answers

```json
{ "contract": "...", "requestId": "...", "kind": "accepted", "status": "running", "reason": null, "terminal": null }
```

`kind` is `accepted`, `duplicate`, `expired` or `rejected`; `reason` names a
rejection (`owner-closing`, `owner-disposed`, `identity-conflict`, `capacity`,
`unavailable`, `stream-capacity`). A failed admission before identity checks
answers `{kind, reason}`. `contract` is
`{ViewModel full name}:{fingerprint}:{route prefix}`. A request id is bound to the
command member and the canonical input digest: reusing it for other work is
`identity-conflict`, and a retry observes the original operation.

A status is `{contract, requestId, kind}` with `kind` one of `running`,
`succeeded`, `failed`, `cancelled`, `expired`, `unknown`. Success may add
`result`, `delivery: {kind, message}` (`result-too-large`,
`result-encoding-failed`, `stream-overflow`, `stream-retention-too-large`) and
`stream: true`; failure adds `error`. A cancel answers `cancellation-requested`,
`not-running`, `unknown` or `expired`; cancellation never rewrites a success. A
stream page adds `cursor`, `completed`, `items: [{sequence, value}]` and
`delivery`. Malformed requests answer `{kind: "invalid-request"}`.

A window retains at most 64 operations, 32 terminal results, 128 expired ids, and
256 KiB of results and stream replay. Running streams reserve their bound against
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

Only a trusted bridge invocation can reach the browser, and only the single
presentation of the invoking client and connection. Interactions time out after
two minutes by default (at most ten); a window holds at most 32 pending requests,
8 per presentation, and payloads up to 64 KiB.

## Model context

Routes are synchronous. A bridge runs its work inline when its model context
reports `IsExecuting`, and otherwise blocks until `InvokeAsync` completes.
Content exposed in a window binds to the window graph's context while attached.
Teardown runs inline when an application-owned context was disposed first.

## Compatibility

- A wait request without `generation` (generation 0) replaces capabilities on
  each poll, but never overrides a registration with a later generation.
- `member` is optional on operation recovery routes; generated clients send it.
- A page runtime created by an earlier generated client gains new runtime
  fields when a newer module loads.
- Clients that predate `protocol` or `onReconnect` ignore them.
