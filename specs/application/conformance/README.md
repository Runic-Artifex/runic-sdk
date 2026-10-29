# Views protocol 4 conformance corpus

Portable vectors and scenarios for the [Runic Application Views protocol](../README.md), version 4: contract id
`runic.views`, version 4. They are normative. An implementation in any language checks itself with two small
runners, one for vectors and one for scenarios, and reports, for each vector case and scenario id, its own
implementation identity and one outcome: pass, fail, unsupported (an optional capability it does not have) or excluded
(a role it does not implement).

| Directory | Content |
| --- | --- |
| [`vectors/canonical`](vectors/canonical) | The canonical form (RFC 8785): input bytes and canonical bytes or an error |
| [`vectors/values`](vectors/values) | Valid and invalid encodings of every wire value type, with their normal forms |
| [`vectors/messages`](vectors/messages) | Well-formed and malformed requests, replies and pushes |
| [`scenarios/<set>`](scenarios) | Transcripts of a window: model changes, client calls, requests, replies and pushes |

The repository checks the corpus itself in `eng/views-conformance.test.mjs` (run with
`bun run test eng/views-conformance.test.mjs`, and in the engineering job): every file against its schema, the
canonical vectors against a reference RFC 8785 reader and JavaScript's `JSON.stringify` with sorted members, the value
and message vectors against reference readers of the specification, and every scenario for the resources it creates.
The producer runner belongs in `Runic.Application.Testing.Tests` and the client runner in the `@runic-artifex/views`
tests; both are added with the protocol-4 implementations.

## Vectors

A vector file follows [`vector.schema.json`](vector.schema.json) (format `runic.views.vector/1`) and holds the cases of
one topic. Its `id` is `{area}.{file}`, and each case's `id` starts with it and is unique in the corpus. Every case has
an `operation`, an `input` and one `expect`. `input.encoding` is `json` (the value is embedded), `utf8` (the bytes are
the UTF-8 encoding of the string `value`) or `base64` (for bytes that are not UTF-8).

### Canonical form (`vectors/canonical`)

`operation` is `canonicalize`. The implementation reads the input bytes as one JSON text under the
[canonical form's rules](../values.md#canonical-form) and either writes its canonical form, which must equal the UTF-8
encoding of `expect.output.value` byte for byte, or fails with the error `expect.error.code`:

| Code | Meaning |
| --- | --- |
| `invalid-utf8` | The bytes are not well-formed UTF-8. |
| `invalid-json` | The text is not one JSON text (RFC 8259), including a leading byte order mark. |
| `lone-surrogate` | A string or member name, after unescaping, contains a lone surrogate. |
| `duplicate-member` | An object has two members with the same name, compared after unescaping. |
| `depth-exceeded` | The value is nested deeper than 64. |
| `number-out-of-range` | A number rounds to an infinite double. |

An implementation whose reader reports these failures in other terms maps them to the codes; a runner compares the
code only. [`jcs-testdata.json`](vectors/canonical/jcs-testdata.json) holds the sample inputs of the RFC 8785
reference test data ([cyberphone/json-canonicalization](https://github.com/cyberphone/json-canonicalization),
Apache-2.0).

### Values (`vectors/values`)

`operation` is `decode`, `type` is a [wire value type](../values.md#wire-value-types), and `parameters` gives the
enum's `names` or `nullable`. The implementation reads the input as a value of the type. A valid input's normal form
must equal `expect.value` as a JSON value (numbers compared as doubles), or `expect.text` read as JSON, which the
corpus uses for values nested too deep to embed in a file whose readers stop at depth 64. An invalid input fails with
`invalid-value`. A runner checks both directions: decoding the input, and encoding the decoded value, which must give
the normal form.

### Messages (`vectors/messages`)

The cases check the message envelope only; whether a route or member exists is checked by scenarios.

| `operation` | Role | `expect.result` |
| --- | --- | --- |
| `read-request` | producer | `valid`, or `invalid` with `expect.error`, the `kind` and `reason` of the producer's failure reply. `limits.inputBytes` sets the input limit. |
| `read-push` | client | `valid`; `ignored`, a push kind the client does not know; or `invalid`, a push the client discards (and, for a state or frame of an open route, recovers from) |
| `read-reply` | client | `valid` or `invalid` (the call rejects with `failed`, an invalid response). `request` is the kind of the request the reply answers. |

## Scenarios

A scenario file follows [`scenario.schema.json`](scenario.schema.json) (format `runic.views.scenario/1`). Its `id` is
`{set}.{file}.v{n}`. It describes one window whose root View is `view`, one of the [test Views](#test-views), with the
model values of `arrange.model`, and lists `steps` in order. `roles` names the runners that execute it.

### Steps

Each step has one action. `role` limits a step to one runner; `connection` names the connection of a `send` or
`receive` and defaults to the first connection the scenario creates; `note` is a comment.

| Step | Producer runner | Client runner |
| --- | --- | --- |
| `connect` | opens a transport connection | connects the client to a scripted host bridge; the client then sends `hello` |
| `disconnect` | closes the connection | reports the connection lost (`onConnectionChange(false)`) |
| `mutate` | applies the mutations to the model as one [publication unit](../state-changes.md#publication-units), or each mutation and each appended row as its own unit with `units: "each"` | skips it |
| `call` | skips it | the application calls the client ([calls](#calls)) |
| `send` | delivers the request to the producer | requires that the client's next request on the connection equals the message |
| `receive` | requires that the producer's next message on the connection equals the message | delivers the message: as the reply to the request `replyTo`, or as a push. With `drop: true` it does not deliver it. |
| `advance` | advances the virtual clock by that many milliseconds | the same |
| `delivery` | `hold`: the transport stops taking pushes after the one being delivered; `release`: it takes them again | skips it |
| `expect` | checks `model` | checks `state`, `call`, `operation`, `diagnostics` or `notifications` |

A `receive` step waits until the expected message is available, up to the runner's own timeout; time in the scenario
passes only through `advance`.

Where the protocol leaves the order of messages open, such as a reply and the frame or reply of a request admitted
after it ([reply order](../README.md#reply-order)), the steps mark them with `unordered: true`. The `receive` steps with
`unordered` of one run of steps form a group; only steps that the producer runner skips (calls and client
expectations) may separate them, and they are on one connection. A producer runner requires that its next messages
are the group's messages in any order. A client runner delivers them in the order the steps list them, so a pair of
scenarios covers both orders for the client. When the scenario ends, each connection must have no unmatched messages left: no
request the client sent that no `send` step matched, and no message the producer sent that no `receive` step matched.

### Comparing messages

Messages are compared **by JSON value**, never by bytes or member order, with these rules:

- **Revisions.** The values of `revision` and `baseRevision` are compared by order: the runner maps each distinct
  expected revision to the actual one the first time it occurs, and the mapping must be consistent and strictly
  increasing. A producer may therefore advance its counter differently, as long as it orders and repeats revisions
  the same way.
- **Changes.** A frame's `changes` are compared per field: the changes of each field in order, and the set of fields.
  The order between fields carries no meaning ([frames](../state-changes.md#frames)).
- **`{"$any": type}`** matches any value of that JSON type (`string`, `number`, `object`, …). Where the runner must
  write the message itself (a producer runner's `send`, a client runner's `receive`), it writes `default` instead,
  which the scenario then provides. Messages, human-readable texts and the implementations' names use it.
- **`{"$id": name}`** is an identifier from `ids`. For a `wire` id, a runner that writes the message writes its
  `value`; a client runner, which checks what the client sends, binds the first value it sees and requires the same
  value later. A `provided` id is a value of the implementation under test, such as a View's operation contract or an
  interaction's contract: the runner takes it from the implementation, or binds it the first time a message it checks
  carries it.
- **`{"$json": text}`** appears only in messages a runner writes: the runner embeds `text` as it is, to send number or
  member layouts that a JSON value cannot keep (`2.0`).
- **Generators.** To keep large cases small, an array element that is a single-member object is a generator.
  `{"$rows": [start, count, width?]}` expands to `count` rows of the `list` View with ids from `start` and the label
  `"row {id}"`, padded with `.` to `width` characters. `{"$adds": [start, count, width?]}` expands to one `add` change
  per such row, at the index equal to its id.
- Failure detail is off in every scenario.

### Calls

A `call` names the client operation in language-neutral terms; the runner maps it to the client's API.

| `op` | Members | Meaning |
| --- | --- | --- |
| `open`, `close` | `route` | Connect a View of the route, or release it. |
| `set` | `route`, `member`, `value` | A generated setter. |
| `write` | `route`, `member`, `requestId`, `baseline: {value, version}`, `value` | A checked write. |
| `command`, `can` | `route`, `member`, `input?` | A command, or its availability. |
| `start` | `route`, `member`, `operation`, `input?` | Start an operation with the id `operation`. |
| `wait`, `status`, `cancel` | `operation` | Wait for the operation's terminal status, read its status, or cancel it. |
| `register` | `route`, `interaction`, `answer` | Register a handler that answers `{output}`, `cancel`s, `fail`s or `never` answers. |

Expectations of the client:

- `state`: the state the client exposes for the route, after the [setter overlay](../README.md#client-overlay).
- `call`: `pending`, `resolved` (with the call's value: `null` for calls without one, the boolean of `can`, a checked
  write's receipt, an operation's terminal status without `revision`), or `rejected` with the error's `kind` and the
  `reason` or `failure` given.
- `operation`: the last status the client observed, without `revision`.
- `diagnostics`: the codes of every diagnostic reported since the scenario started, in order.
- `notifications`: how often subscribers of the route were notified of a new state since its `open` resolved.

### Identifiers

`ids` lists every identifier the scenario uses, and nothing else: each connection (`connection`), each `send` step
(`request`), each call (`call`), each identifier sent on the wire (`wire`, with its `value`) and each value the
implementation provides (`provided`). Nothing exists before the steps run. A runner fails a scenario that uses a
connection before connecting it, answers a request it did not send, expects a call it did not make, or uses an id that
`ids` does not list; it never fills in a default. The repository's check rejects such scenarios (C1, #667).

### Sets

| Set | Content |
| --- | --- |
| [`handshake`](scenarios/handshake) | `hello`; protocol mismatch in either direction; the version diagnostic only for `Runic.Application.Views`; a request before `hello`; unknown kinds |
| [`state-changes`](scenarios/state-changes) | The protocol-3 collection and text fixtures, converted (`source` names the case; the text fixtures were removed in 0.13, see [v0.12.1](https://github.com/Runic-Artifex/runic-sdk/tree/v0.12.1/specs/application/fixtures)); member `set` changes together with collection changes; members that never announce a change; a `set` that resets a collection; overflow; recovery while frames arrive; invalid frames |
| [`replies`](scenarios/replies) | Revision inclusion; a change without a notification; a call that changes nothing; a call that changes another route; a call that releases its route; a reply before its frame; the revision timeout |
| [`edits`](scenarios/edits) | `superseded`; edit numbers per member; the client overlay and its end, with the reply to an earlier edit before and after the frame of a later one; a failed edit; checked writes |
| [`operations`](scenarios/operations) | `watch` with throttled `progress` and the terminal `revision`; `start` ordering; duplicate versus `identity-conflict` by canonical equality; cancel; a declared failure; an unknown operation; a `start` with another build's contract |
| [`presentations`](scenarios/presentations) | `open` and `close`; a presentation owned by another connection; a reconnect with a new session; an unknown route |
| [`interactions`](scenarios/interactions) | A confirmation answered, and its repeat; registration outcomes |
| [`errors`](scenarios/errors) | The protocol-3 `error-reasons`, `domain-failures` and `released` fixtures, converted (`error-reasons` and `released` were removed in 0.13); capacity |

## Test Views

The scenarios use these Views. A producer runner implements them in its own language; their behaviour, not their
code, is normative. Each root View is the root of its own window, and its route is its name.

### `list`

The model of the protocol-3 collection fixtures. Members:

| Member | Type | Notes |
| --- | --- | --- |
| `rows` | keyed collection of `{id: int32, label: string}`, keyed by `id` | changed by the mutations below |
| `title` | `string` | settable; default `""` |

### `log`

The model of the protocol-3 text fixtures. Members: `log`, a nullable append-only text field (default `""`), and
`title` (`string`, settable, default `""`).

### `notes`

| Member | Type | Default and behaviour |
| --- | --- | --- |
| `title` | `string` | `""`; settable; the setter trims whitespace |
| `note` | `string` | `""`; a checked field, written with `write` or set by `assign` |
| `priority` | enum `Low`, `Normal`, `High` | `Normal`; settable |
| `due` | nullable `local-date` | `null`; settable |
| `elapsed` | `duration` | `PT0S`; settable |
| `rows` | keyed collection of `{id: int32, label: string}`, keyed by `id` | `[]` |
| `count` | `int32` | the number of rows; never announces a change |
| `summary` | `string` | `"rows: {count}"`; never announces a change |
| `detail` | content reference | the `detail` View below, with id `0123456789abcdef0123456789abcdef` |
| `canClear`, `canTouch`, `canSave`, `canConfirmClear` | `boolean` | availability members ([commands](../README.md#commands-and-availability)) |
| `isSlowImportExecuting`, `isConfirmClearExecuting` | `boolean` | execution members |

Its `fields` are `{"note": {"version"}}`, starting at version 0. Commands:

| Command | Input | Available | Effect |
| --- | --- | --- | --- |
| `addRow` | `string` (the label) | when the label is not empty | appends `{id, label}`, with `id` one more than the largest id (1 for the first row) |
| `removeRow` | the key of a row (a key command) | always | removes the row; a key of no row is `unresolved-key` |
| `clear` | none | when there are rows | removes every row, as a reset |
| `touch` | none | always | appends `!` to `title` without announcing the change |
| `save` | none | always | fails with its declared failure `{"$case": "titleRequired"}` when `title` is empty; otherwise changes nothing |
| `annotate` | `string` | always | sets the `detail` View's `lastAction` to the input; changes nothing in `notes` |
| `slowImport` | `{source: string, limit: int32}` | while it is not executing | asynchronous; runs until a `finish` mutation; reports `progress` mutations; `finish` with `rows` appends rows with those labels and succeeds with their number (`int32`); `finish` with `failure` fails with that declared failure (`{"$case": "sourceMissing"}`); `finish` with `outcome: "cancelled"` ends cancelled, which the command honours only after `cancel` |
| `confirmClear` | none | when there are rows and it is not executing | asynchronous; asks the `confirm` interaction with `{question: "Clear all rows?"}` and clears the rows, as a reset, when the output `{accepted: boolean}` accepts |

### `detail`

The content View in the `notes` View's `detail` slot. Members: `lastAction` (`string`, default `""`) and `canDismiss`.
Command `dismiss` (no input, always available) clears the `notes` View's `detail` slot, which releases this View's
route.

### Mutations

| `op` | Members | Effect |
| --- | --- | --- |
| `assign` | `member`, `value` | sets a member of the root View, announcing the change |
| `insert`, `removeAt`, `replaceAt`, `move` | `index`, `item`; `index`; `index`, `item`; `oldIndex`, `newIndex` | changes `rows` at a position |
| `reset` | `items` | replaces all rows at once, as a reset |
| `appendRows` | `start`, `count`, `width?` | appends the rows of the `$rows` generator, one change each |
| `append` | `member`, `text` | appends text to a text field |
| `progress` | `member`, `value` | the running operation of the command reports progress |
| `finish` | `member`, `rows?`, `failure?`, `outcome?` | ends the running operation of the command (see `slowImport`) |
