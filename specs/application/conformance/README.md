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

The runners:

- the language-neutral conformance driver, [`eng/conformance`](../../../eng/conformance/README.md)
  (`bun run conformance <implementation>`), which runs every case through a small adapter in the implementation's
  language ([`rax.conformance-adapter/1`](../../../eng/conformance/PROTOCOL.md)) and compares the outcomes with the
  implementation's [status file](status);
- the .NET producer's own runner, in `tests/dotnet/Runic.Application.Testing.Tests/Conformance`;
- the TypeScript client's own runner, `packages/web/views/test/client-conformance.test.ts`.

**Features an implementation has.** A runner takes the features an implementation has from the implementation's own
handshake: the `features` of its `hello` reply to a client that lists every feature, or of its `hello` request. It
never keeps a list of its own. A scenario that [requires](#requirements) a feature the implementation does not list is
`unsupported`; one that it lists and fails is a failure.

## Vectors

A vector file follows [`vector.schema.json`](vector.schema.json) (format `runic.views.vector/1`) and holds the cases of
one topic. Its `id` is `{area}.{file}`, and each case's `id` starts with it and is unique in the corpus. Every case has
an `operation`, an `input` and one `expect`. `input.encoding` is `json` (the value is embedded), `utf8` (the bytes are
the UTF-8 encoding of the string `value`) or `base64` (for bytes that are not UTF-8). A runner writes a `json` value
as compact JSON, but its escaping may differ, so a case whose result depends on its size in bytes, such as one with
`limits`, uses `utf8` or `base64`.

`roles` names the runners that execute a file's cases, as a scenario's `roles` does; absent means both. A runner of
another role reports the file's cases excluded. A case's `requires`, like a scenario's
([requirements](#requirements)), names the features it needs; a runner whose implementation does not list one reports
the case unsupported. The request vectors of a kind that a feature defines require it: `view-requests` requires
`view-requests/1`, `region` `region-collections/1`, `navigation` `navigation-path/1`, `client-error` `client-errors/1`,
and `presented` and `closing-reply` `presented/1`.

| Files | `roles` | Why |
| --- | --- | --- |
| `canonical/*` | `producer` | The producer canonicalizes, for an operation's identity; the client never does. |
| `values/*` | both | Both sides read and write every wire value. |
| `messages/requests.json` | `producer` | Requests as the producer reads them. |
| `messages/pushes.json`, `messages/replies.json` | `client` | Pushes and replies as the client reads them. |

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

The cases check the message envelope only; whether a route or member exists is checked by scenarios. Replies and
pushes are read as on a connection that uses every [feature](../README.md#features).

A producer runner reads each `read-request` case in a **fresh window**, on a new connection. The window serves no View,
so every route is unknown and a request that names one is answered `released` once its envelope is valid. (An
implementation that cannot open a window without a View may root it at the `list` View: no case names its route.)

1. Unless the case is itself a `hello`, it first sends the `hello` of the case's file and requires that its reply is
   successful. That `hello` lists the features the cases' request kinds need (`text-splice/1`,
   `presentation-epoch/1`, `presented/1`, `view-requests/1`, `region-collections/1`, `client-errors/1` and
   `navigation-path/1`), and not `request-session/1`, whose requests would have to name the session. A case is a
   `hello` when its input bytes are one JSON object whose top-level `kind` is the string `"hello"`; input that is not
   JSON, or not an object, is not a `hello`.
2. It sends the case's bytes as one request, with failure detail off and, when the case has `limits.inputBytes`,
   that input limit.
3. The request is **valid** when its reply is not `rejected` with the reason `invalid-request`, `invalid-input` or
   `no-hello`. Any other reply counts: a route that nothing serves answers `released`, which is a scenario's concern.
   It is **invalid** when the reply is `rejected` with one of those reasons, and then `kind` and `reason` must equal
   `expect.error`.

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
| `delivery` | `hold`: the transport stops taking pushes after the one being delivered, and when none is being delivered, it takes none, the next one included; `release`: it takes them again, in order. Only pushes are held; replies are always taken. The runner's transport lets the producer wait until it took a push, as the producer's [reply order](../README.md#reply-order) needs. | skips it |
| `flushFields` | starts a [field flush](../README.md#field-flush) of the window and names it `id` | skips it |
| `closeWindow` | starts the window's close with `timeout` milliseconds and names it `id` ([window close](../README.md#window-close)) | skips it |
| `setLocale` | sets the window's locale, a tag or `null` ([translated text](../README.md#translated-text)) | skips it |
| `setAppearance` | sets the window's appearance, an object with the members of the `appearance` push or `null` ([appearance](../README.md#appearance)); `override` names the members the host decided | skips it |
| `upload` | the page of the step's `connection` uploads a file ([uploads](../README.md#uploads)): a `POST` to the window's upload URL with the connection's token, the body `text` as UTF-8, the `name` and the media `type`, and `size` as its `Content-Length` (the body's length by default). The host's answer, `{status, body}`, must equal `answer`, compared as a message; an `{"$id"}` of kind `provided` in it binds the upload's id | skips it |
| `expect` | checks `model`, `presented` or `closed`; a `model` expectation waits until it matches | checks `state`, `call`, `operation`, `diagnostics`, `notifications` or `connection` |

A `receive` step waits until the expected message is available, up to the runner's own timeout; time in the scenario
passes only through `advance`. A producer runner's `expect` with `model` waits the same way: it reads the model again
until every member matches, up to the same timeout. A runner scales its timeouts for slow machines, such as by
`RAX_TEST_TIMEOUT_SCALE` for the driver or `RUNIC_TEST_TIMEOUT_SCALE` for the .NET runner. A scenario uses it to wait for work the producer does on its own, such as
a guard that a request asks, before its next step.

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
  the same way. One exception: within a group of `unordered` steps, revisions first seen in the group on different
  routes may map in either order, since the order of the routes' publications is the producer's
  ([publication units](../state-changes.md#publication-units)). They still map to different revisions, and each is
  ordered with every revision outside the group.
- **Epochs.** The values of `epoch` ([presentation epochs](../README.md#presentation-epochs)) have their own mapping:
  the runner maps each distinct expected epoch of a route to the actual one the first time it occurs, and the mapping
  must be consistent and one-to-one per route, since a producer only has to make each epoch differ from the earlier
  ones. A runner that writes a message writes the mapped value.
- **Changes.** A frame's `changes` are compared per field: the changes of each field in order, and the set of fields.
  The order between fields carries no meaning ([frames](../state-changes.md#frames)).
- **`{"$any": type}`** matches any value of that JSON type (`string`, `number`, `object`, `array`, …). Where the runner must
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
- **Sessions.** A client that implements [`request-session/1`](../README.md#request-sessions) names the session of
  its connection's `hello` in every request. A client runner requires that a request's `session`, when present, is
  the session of the connection's `hello`, and then compares the request without it unless the step's message lists
  `session`. The scenarios of `request-sessions` list it; the others leave it out, so they hold for clients with and
  without the feature. A producer runner sends what the step lists, so only a scenario whose `hello` lists the
  feature sends `session`.
- **Languages.** A client that implements [`client-languages/1`](../README.md#client-languages) sends its browser's
  `languages` in every `hello`. A client runner requires that a `hello`'s `languages`, when present, is an array of at
  most 16 strings, and then compares the `hello` without it unless the step's message lists `languages`. Only the
  `client-languages-*` scenarios list it.
- **Release.** A producer may name its Runic release in each `hello` reply ([handshake](../README.md#handshake)). A
  producer runner requires that a `hello` reply's `release`, when present, is a string, and then compares the reply
  without it unless the step's message lists `release`.
- Failure detail is off in every scenario.

### Calls

A `call` names the client operation in language-neutral terms; the runner maps it to the client's API.

| `op` | Members | Meaning |
| --- | --- | --- |
| `open`, `close` | `route` | Connect a View of the route, or release it. |
| `set` | `route`, `member`, `value` | A generated setter. |
| `write` | `route`, `member`, `path?`, `requestId`, `baseline: {value, version}`, `value` | A checked write; with `path`, of that field path below the member (`field-paths/1`). |
| `command`, `can` | `route`, `member`, `input?` | A command, or its availability. |
| `start` | `route`, `member`, `operation`, `input?` | Start an operation with the id `operation`. |
| `wait`, `status`, `cancel` | `operation` | Wait for the operation's terminal status, read its status, or cancel it. |
| `register` | `route`, `interaction`, `answer` | Register a handler that answers `{output}`, `cancel`s, `fail`s or `never` answers. |
| `handle`, `unhandle` | `route`, `request` | Register a [view request](../README.md#view-requests) handler of the route that records each input it is called with, or unregister it. |
| `region` | `route`, `member`, `action`, `key` | Select or close the region with `key` of a region collection ([region collections](../README.md#region-collections)): `action` is `select` or `close`. |
| `queryRange` | `route`, `member`, `start`, `count` | Show `count` rows of the query collection from `start`. It resolves with the keys of the range's rows once they are all loaded at the current version. |
| `setQuery` | `route`, `member`, `value` | Change the query collection's query to `value`. |
| `indexOf` | `route`, `member`, `value` | Find the position of the row with the key `value`; resolves with it, or `null`. |

Expectations of the client:

- `state`: the state the client exposes for the route, after the [setter overlay](../README.md#client-overlay).
- `call`: `pending`, `resolved` (with the call's value: `null` for calls without one, the boolean of `can`, a checked
  write's receipt, an operation's terminal status without `revision`, a region request's `outcome`), or `rejected` with the error's `kind` and the
  `reason` or `failure` given.
- `operation`: the last status the client observed, without `revision`.
- `diagnostics`: the codes of every diagnostic reported since the scenario started, in order.
- `notifications`: how often subscribers of the route were notified of a new state since its `open` resolved.
- `connection`: the connection state the client reports. `closed` means that it reports that the producer closed the
  window ([window close](../README.md#window-close)).
- `locale`: the window's locale the client reports, or `null` ([translated text](../README.md#translated-text)).
- `appearance`: the window's appearance the client keeps from the host's last `appearance` push, with every member, or
  `null` before the first push ([appearance](../README.md#appearance)).
- `requests`: `{route, request, inputs}`, the inputs the `handle` handler of that route and request was called with
  since it was registered, in order. The runner waits for them as for a `receive` step, since a handler runs once the
  page painted the request's revision.

Expectations of the producer:

- `model`: members of the root View's model, each compared as its wire JSON value with the rules above; members it
  does not list are not checked. In the same way, `arrange.model` gives the members a View starts with, as wire JSON
  values, and a member it does not list keeps its default from [Test Views](#test-views).
- `presented`: the result of the `waitPresented` wait `id` ([painted states](../README.md#painted-states)): `true` or
  `false` once it completed, which the runner waits for as for a `receive` step, or `"pending"` while it has not.
- `closed`: the close of the `closeWindow` step `id` completed, and `acknowledged` is whether every page acknowledged
  it ([window close](../README.md#window-close)). The runner waits for the close as for a `receive` step.
- `flushed`: `{id, answered}`, the result of the `flushFields` step `id`: whether every page answered the flush. The
  runner waits for it as for a `receive` step.
- `languages`: `{connection, value}`, the languages the producer reports for the connection after its `hello`, in
  order and in BCP 47 case ([client languages](../README.md#client-languages)), as the producer's API reports them
  (in .NET, `RunicConnection.Languages`).

### Requirements

`requires` names what a scenario needs beyond the base protocol. A runner without it reports the scenario unsupported.
This table is the closed list of requirement names, of two kinds:

- **Runner capabilities**, `held-delivery` and `virtual-clock`, describe the test seam, not the protocol. An adapter
  reports them as its `capabilities`.
- **Features** are [feature names](../README.md#features). An implementation has the features it lists in its own
  handshake ([the runners](#views-protocol-4-conformance-corpus)). A scenario requires every feature whose behaviour
  it checks, also one whose members are part of a state value on every connection, such as `validation-pending/1`.

| Requirement | Meaning |
| --- | --- |
| `held-delivery` | The producer runner can hold the transport's deliveries (`delivery` steps). |
| `virtual-clock` | The runner's time passes only through `advance` steps. |
| `text-splice/1` | The implementation implements the feature [`text-splice/1`](../README.md#features). |
| `presentation-epoch/1` | The implementation implements the feature [`presentation-epoch/1`](../README.md#presentation-epochs). |
| `presented/1` | The implementation implements the feature [`presented/1`](../README.md#presented-states-and-window-close). |
| `view-requests/1` | The implementation implements the feature [`view-requests/1`](../README.md#view-requests). |
| `request-session/1` | The implementation implements the feature [`request-session/1`](../README.md#request-sessions). |
| `region-collections/1` | The implementation implements the feature [`region-collections/1`](../README.md#region-collections). |
| `query-collections/1` | The implementation implements the feature [`query-collections/1`](../README.md#query-collections). Its scenarios list it in the `default` of both `hello` steps. |
| `field-paths/1` | The implementation implements the feature [`field-paths/1`](../README.md#field-paths). Its scenarios list the feature in the `default` of both `hello` steps, beside `text-splice/1`. |
| `translation-text/1` | The implementation implements the feature [`translation-text/1`](../README.md#translated-text). |
| `navigation-path/1` | The implementation implements the feature [`navigation-path/1`](../README.md#navigation-paths). |
| `appearance/1` | The implementation implements the feature [`appearance/1`](../README.md#appearance). |
| `client-languages/1` | The implementation implements the feature [`client-languages/1`](../README.md#client-languages). Its client `hello` sends `languages` as `{"$any":"array",…}`, since a client sends its browser's. |
| `client-errors/1` | The implementation implements the feature [`client-errors/1`](../README.md#page-errors). |
| `blobs/1` | The implementation implements the feature [`blobs/1`](../README.md#blobs-and-uploads). Its scenarios use the `files` View. |
| `field-flush/1` | The implementation implements the feature [`field-flush/1`](../README.md#field-flush). Its scenarios use the `flushFields` step and the `flushed` expectation. |
| `validation-pending/1` | The implementation implements the feature [`validation-pending/1`](../README.md#pending). |
| `trace-context/1` | The implementation implements the feature [`trace-context/1`](../README.md#trace-context). |
| `handler-timing/1` | The implementation implements the feature [`handler-timing/1`](../README.md#handler-timing), which a producer lists only in development: a producer runner runs the test windows in development for it, with failure detail still off. |

Every `hello` of a scenario that does not require a feature lists `features` as `{"$any":"array","default":["text-splice/1"]}`:
a runner that writes the `hello` (or its reply) lists `text-splice/1`, and a runner that checks one accepts any list.
A client lists every feature it implements, so the client's `hello` of a scenario that a client runner executes is
always `{"$any":"array","default":[…]}`, also when the scenario requires a feature; the producer's reply decides which
features are in use.
The strings of those scenarios are shorter than 64 code units, so their frames and requests are the same with and
without the feature. None of `presentation-epoch/1`, `presented/1` and `translation-text/1` is in use in them, so their messages carry no
`epoch` and no `present`, `closing`, `locale` or `appearance` push. A scenario that requires `presentation-epoch/1`, `presented/1`,
`view-requests/1`, `region-collections/1`, `translation-text/1`, `navigation-path/1` or `appearance/1` lists the feature in the
`default` of both `hello` steps, beside `text-splice/1`, so it is in use whichever runner writes the `hello` or its
reply; one that shows the producer without the feature, such as `locale-is-not-pushed-without-the-feature`, lists only
`text-splice/1`.

### Identifiers

`ids` lists every identifier the scenario uses, and nothing else: each connection (`connection`), each `send` step
(`request`), each call (`call`), each `waitPresented` mutation (`wait`), each `closeWindow` step (`close`), each `flushFields` step (`flush`), each
identifier sent on the wire (`wire`, with its `value`) and each value the implementation provides (`provided`). Nothing exists before the steps run. A runner fails a scenario that uses a
connection before connecting it, answers a request it did not send, expects a call it did not make, or uses an id that
`ids` does not list; it never fills in a default. The repository's check rejects such scenarios (C1, #667).

### Sets

| Set | Content |
| --- | --- |
| [`handshake`](scenarios/handshake) | `hello`; protocol mismatch in either direction; the version diagnostic for a producer that names another Runic release, or for `Runic.Application.Views` or, in the rename window, `RunicArtifex.Application.Views` of another version, and none for other producers; unknown kinds; `features` both ways, unknown and invalid ones, and a feature only one side lists; the window's locale pushed after `hello` and on each change, not when it is set again, and not without `translation-text/1` (`locale-*`); the window's appearance pushed after `hello`, also while unknown, and on each change, an `override` that lists only known members, and not without `appearance/1` (`appearance-*`); the client's languages in `hello` reported on the connection, invalid ones, and ones without `client-languages/1` (`client-languages-*`); requests held before `hello`, admitted after it or answered `no-hello` after 5 seconds; the `open` sent with the `hello`, and sent again after a `no-hello` |
| [`state-changes`](scenarios/state-changes) | The protocol-3 collection and text fixtures, converted (`source` names the case; the text fixtures were removed in 0.13, see [v0.12.1](https://github.com/Runic-Artifex/runic-sdk/tree/v0.12.1/specs/application/fixtures)); member `set` changes together with collection changes; members that never announce a change; a `set` that resets a collection; overflow; recovery while frames arrive; invalid frames; text splices (`splice-*`): a keystroke, a deletion, a short member, a rolling member, a rewrite, a surrogate pair, a checked field, and a splice that does not fit; a validation rule that names no member ([validation](../README.md#validation)); an asynchronous check that starts and ends (`validation-pending-*`); revisions: one unit that changes two routes, a withheld publication, which takes a revision, and a checked field's version, which counts changes |
| [`replies`](scenarios/replies) | Revision inclusion; a change without a notification; a call that changes nothing; a call that changes another route; a call that releases its route; a reply before its frame; the revision timeout; a trace context, also one that does not parse, which changes no reply (`trace-context/1`); handler timing, which a reply carries when asked (`handler-timing/1`) |
| [`edits`](scenarios/edits) | `superseded`; edit numbers per member; the client overlay and its end, with the reply to an earlier edit before and after the frame of a later one; a failed edit; checked writes; `set` with a splice against a revision and against the previous edit, `splice-base` that admits nothing and the client's resend in full, a superseded splice, a normalizing setter; checked writes with a splice (`write-splice-*`): applied, a conflict, `splice-base` that retains nothing and the client's resend in full with the same `requestId`, a retry, and a short value sent in full; an edit followed by the frame with its validation, which the overlay does not cover; a set of a checked field, which raises its version through its announcements; the receipts of 64 writes retained; path writes (`field-paths/1`): a retry of a rejected or conflicting one, which repeats its receipt, row conflicts, an object member that became `null` and an object again, and a nested field |
| [`operations`](scenarios/operations) | `watch` with throttled `progress` and the terminal `revision`; `start` ordering; duplicate versus `identity-conflict` by canonical equality; cancel; a declared failure; an unknown operation; a `start` with another build's contract |
| [`presentations`](scenarios/presentations) | `open` and `close`; a presentation owned by another connection; a reconnect with a new session; an unknown route; presentation epochs (`epoch-*`): the `epoch` of `open` and `snapshot`, a renewal published as a `state`, a scoped command with the current epoch, a second scoped command from the same state that is superseded, an unscoped command without `epoch`, and a connection without the feature; painted states (`presented-*`): a report that completes a wait, and a wait that times out; window close (`closing-*`): an acknowledged close and the client's closed connection, and a close that times out; field flush (`flush-*`): a flush that the client answers, and a connection without the feature, which is not asked |
| [`regions`](scenarios/regions) | Region collections (`region-collections/1`): a select that commits and the frame that carries the selection; a select a departure guard refuses, which publishes nothing; a close of the selected region, which selects the next one, or the previous one after the last; the key of a closed region, which is `unresolved-key`; a close the collection does not allow, which is `unavailable`; a region request on a connection without the feature, which is `invalid-request`; a close of a region whose close is pending, which joins it; a select that supersedes a pending close |
| [`navigation`](scenarios/navigation) | Navigation paths (`navigation-path/1`): the path in the `hello` reply; a path region that appears after the `hello`, pushed as a `replace`; an opened route that pushes a page and one that goes back to it, each with its frame, `navigation-path` push and reply; the window's Back without history (`NoHistory`); a route a departure guard refuses, whose reply keeps the path; a route no route matches (`outcome: null`); a window without a path region, whose Back works and whose `open` is `unavailable`; a `navigation` request on a connection without the feature, which is `invalid-request` |
| [`interactions`](scenarios/interactions) | A confirmation answered, and its repeat; declined; asked with no handler registered; an output that does not decode; a timeout; registration outcomes and their precedence; a reply on a route nothing serves; a closed presentation, which ends its request without a push |
| [`view-requests`](scenarios/view-requests) | A command's request reaches the invoking presentation after its state; a request outside a command reaches the single presentation, and is dropped with none or several; `next-mounted` keeps the latest request for the next registration; `broadcast` reaches every presentation; registration outcomes; a connection without the feature |
| [`request-sessions`](scenarios/request-sessions) | `request-session/1`: a request that names another session or none is `no-hello` and does not act; a `hello` with another session replaces the connection and releases its presentation; the client names its session in every request |
| [`query-collections`](scenarios/query-collections) | Query collections (`query-collections/1`): the state member without rows and a range read by one connection; a query change published as a frame with a new version and an unknown total before its reply, and the rows in its order with the count they start; a filter that changes the total, and a client that asks for no position past it; a query the source rejects, and an unknown filter `op` (`invalid-input`); the position of a key, and of a key the query excludes; a row that changes in place raises the version and the page loads its range again; the request kinds on a connection without the feature |
| [`blobs`](scenarios/blobs) | Blobs and uploads (`blobs/1`): the upload grant in the `hello` reply, and none without the feature; a blob member that crosses as a URL, and its change to `null`; a `set` of it, which is `invalid-request`; a blob in an editable row, which is no field path; an upload id that names no unclaimed upload, which is `invalid-input`; uploads (`upload` steps): claimed once, of another connection, expired after 10 minutes, and larger than `maximumBytes` |
| [`errors`](scenarios/errors) | The protocol-3 `error-reasons`, `domain-failures` and `released` fixtures, converted (`error-reasons` and `released` were removed in 0.13); capacity |

## Test Views

The scenarios use these Views. A producer runner implements them in its own language; their behaviour, not their
code, is normative, and this section is all of it. Each root View is the root of its own window, and its route is its
name. Each window has failure detail off, offers [`handler-timing/1`](../README.md#handler-timing) as a producer does
in development, and publishes each mutation step as soon as it ran: it does not coalesce changes by time
([publication units](../state-changes.md#publication-units)).

A test View's contract IR ([contract IR](../contract-ir.md)) has the View's name with an uppercase first letter as its
`name` (`Notes`), its name as its `route` and only kind, and its members, commands, interactions and view requests in
the order of the tables below; a member's or command's IR `name` is its wire name with an uppercase first letter.
[`views.ir.json`](views.ir.json) is the IR of the .NET producer's test Views, from which its adapter derives their
contracts; `ConformanceContractTests` checks it against them, and `eng/views-contract-ir.test.mjs` its fingerprints.

### Provided ids

A `provided` id ([identifiers](#identifiers)) is a value the producer chooses. A runner asks the producer's adapter for
it before a message it writes carries it, or binds it the first time a message it checks carries it. These are all of
them:

| Id | Type | Value |
| --- | --- | --- |
| `contract` | string | The operation contract of the `notes` View ([operations](../README.md#operations)), which a `start` names. |
| `confirmContract` | string | The contract of the `notes` View's `confirm` interaction ([interactions](../README.md#interactions)). |
| `focusContract`, `revealContract`, `toastContract` | string | The contracts of the `notes` View's view requests `focus`, `reveal` and `toast` ([view requests](../README.md#view-requests)). |
| `interactionRequest` | string | The `requestId` of an interaction request the producer sends. |
| `ownerEpoch` | number | The `ownerEpoch` the producer assigns to an interaction request ([interactions](../README.md#interactions)); not the presentation epoch. |
| `flushId` | string | The `id` of a `flush` push ([field flush](../README.md#field-flush)). |

A producer derives the contract ids from the `notes` View's fingerprint, `runic.views.fingerprint/1`
([contract IR, Fingerprint](../contract-ir.md#fingerprint)), as [derived contracts](../contract-ir.md#derived-contracts)
says:

- `contract` is `{view}:{fingerprint}`, with the producer's View id;
- `confirmContract` is `{fingerprint}:interaction:Confirm`;
- `focusContract`, `revealContract` and `toastContract` are `{fingerprint}:request:Focus`, `…:Reveal` and `…:Toast`.

The fingerprint depends only on the View's wire contract, so producers whose test Views describe the same contract
compute the same one, and the ids are comparable across producers: compare a producer's IR of the test Views with
[`views.ir.json`](views.ir.json). Two parts still differ between producers by design:

- `contract` contains the producer's View id (`Runic.Application.Testing.Tests.Conformance.NotesViewModel` in .NET);
- the [projection](../contract-ir.md#projection) keeps the ids of the Views a member presents, so the fingerprint of a
  View with a content member, `notes` (`detail`) and `tabs` (`page`), and with it `confirmContract`, `focusContract`,
  `revealContract` and `toastContract`, contains the producer's id of the presented View.

The other test Views' fingerprints are the same in every producer. Runners take every contract id from the adapter, so
this corpus does not check the derivation itself.

### `list`

The model of the protocol-3 collection fixtures. Members:

| Member | Type | Notes |
| --- | --- | --- |
| `rows` | keyed collection of `{id: int32, label: string}`, keyed by `id` | `[]`; changed by the mutations below |
| `title` | `string` | settable; default `""`; a text member |

A key of `rows` is the decimal text of `id`, such as `"1"`.

### `log`

The model of the protocol-3 text fixtures. Members: `log`, a nullable append-only text field (default `""`), and
`title` (`string`, settable, default `""`, a text member).

### `notes`

| Member | Type | Default and behaviour |
| --- | --- | --- |
| `title` | `string` | `""`; settable; the setter trims whitespace |
| `note` | `string` | `""`; settable; a checked field, written with `write` or set by `set` and `assign` |
| `priority` | enum `Low`, `Normal`, `High` | `Normal`; settable |
| `due` | nullable `local-date` | `null`; settable |
| `elapsed` | `duration` | `PT0S`; settable |
| `rows` | keyed collection of `{id: int32, label: string}`, keyed by `id` | `[]` |
| `count` | `int32` | the number of rows; never announces a change |
| `summary` | `string` | `"rows: {count}"`; never announces a change |
| `detail` | content reference | the `detail` View below, with id `0123456789abcdef0123456789abcdef` |
| `canClear`, `canTouch`, `canSave`, `canConfirmClear` | `boolean` | availability members ([commands](../README.md#commands-and-availability)) |
| `isSlowImportExecuting`, `isConfirmClearExecuting` | `boolean` | execution members |

View requests ([view requests](../README.md#view-requests)), whose contracts are the provided ids `focusContract`,
`revealContract` and `toastContract`. A request's input is written as the table gives it:

| Request | Input | Delivery | Raised by |
| --- | --- | --- | --- |
| `focus` | `{member: string}` | `drop` | the `touch` command, with `{"member":"title"}` after its effect; the `raise` mutation |
| `reveal` | `{id: int32}` | `next-mounted` | the `raise` mutation |
| `toast` | `{text: string}` | `broadcast` | the `raise` mutation |

Its `fields` are `{"note": {"version"}}`, starting at version 0; the version takes 1 more for each change of `note`
that the model announces (an `assign`, also of an equal value) and for each write the producer applies
([members](../state-changes.md#members)). Its text members are `title`, `note` and `summary`. Its operation contract is
the provided id `contract`. Every setter announces its member's change, also to an equal value.

Commands. Each command ends inside one publication unit with its effects, so the frame that shows its effects also
shows it no longer executing, and an operation's terminal status carries that frame's revision:

| Command | Input | Available | Effect |
| --- | --- | --- | --- |
| `addRow` | `string` (the label) | when the label is not empty | appends `{id, label}`, with `id` one more than the largest id (1 for the first row) |
| `removeRow` | the key of a row (a key command): the string `"1"` for the row with `id` 1 | always | removes the row; a key of no row is `unresolved-key` |
| `clear` | none | when there are rows | removes every row, as a reset |
| `touch` | none | always | appends `!` to `title` without announcing the change, then raises the `focus` view request |
| `save` | none | always | fails with its declared failure `{"$case": "titleRequired"}` when `title` is empty; otherwise changes nothing |
| `annotate` | `string` | always | sets the `detail` View's `lastAction` to the input; changes nothing in `notes` |
| `slowImport` | `{source: string, limit: int32}` | while it is not executing | an operation, or an asynchronous command when invoked with `command`; runs until a `finish` mutation, and ignores its input; reports each `progress` mutation's `value` (a number) as its progress, in the mutation's unit; `finish` with `rows` appends rows with those labels as one change (one `add` with all their keys), with ids as `addRow` gives them, and succeeds with their number (`int32`); `finish` with `failure` fails with the declared failure `{"$case": "sourceMissing"}`; `finish` with `outcome: "cancelled"` ends cancelled after a `cancel`, and fails without one |
| `confirmClear` | none | when there are rows and it is not executing | asynchronous; asks the `confirm` interaction with `{question: "Clear all rows?"}` and clears the rows, as a reset, when the output `{accepted: boolean}` accepts. It succeeds without an effect when the output declines, and, asking nothing, when no presentation of the invoking connection registered `confirm`. An output of another shape fails the interaction, and the command with `failed`, as a timeout or a `failed` or `cancelled` reply does; a request that ends because its presentation closed ends the command `cancelled` |

The `confirm` interaction's contract is the provided id `confirmContract`; its input is `{question: string}` and its
output `{accepted: boolean}`.

### `detail`

The content View in the `notes` View's `detail` slot, served on the route `content0123456789abcdef0123456789abcdef`.
Members: `lastAction` (`string`, default `""`, a text member) and `canDismiss`. An `assign` mutation with
`"view": "detail"` sets `lastAction`; in the same step as a change of `notes`, it is one unit that changes two routes.
Command `dismiss` (no input, always available) clears the `notes` View's `detail` slot, which releases this View's
route.

### `form`

A View whose model validates ([validation](../README.md#validation)).

| Member | Type | Default and behaviour |
| --- | --- | --- |
| `name` | `string` | `""`; settable |
| `nameErrors` | array of `string` | the messages of the rules of `name` |
| `validation` | validation | the View's validation |

Rules, in this order:

| Rule | Holds | Otherwise |
| --- | --- | --- |
| `name` | while `name` has at most 8 UTF-16 code units | `Name must be at most 8 characters.` at the path `["name"]`, with the [`text`](../README.md#text) `{"version":2,"catalog":"conformance","contractFingerprint":"sha256:` and 64 zeros `","key":"name_too_long","arguments":{"max":{"type":"int64","value":"8"}},"fallbackText":"Name must be at most 8 characters."}`, and in `nameErrors` |
| `approval` | until a `rule` mutation changes it; initially it holds | `Approval is required.` at the path `[]`. The rule names no member: a change of it changes no other member and announces none. |

`name` also has an asynchronous check ([`pending`](../README.md#pending), `validation-pending/1`): while a `check`
mutation of `name` says it runs, `["name"]` is pending. Initially it does not run.

### `wizard`

A View whose pages are presentations of one route ([presentation epochs](../README.md#presentation-epochs)).

| Member | Type | Default and behaviour |
| --- | --- | --- |
| `page` | `int32` | `1`; read-only |
| `canNext`, `canBack`, `canPing` | `boolean` | availability members |

| Command | Input | Available | Scoped | Effect |
| --- | --- | --- | --- | --- |
| `next` | none | always | yes | adds 1 to `page`, then renews the route |
| `back` | none | when `page` is greater than 1 | yes | subtracts 1 from `page`, then renews the route |
| `ping` | none | always | no | changes nothing |

Its contract marks `next` and `back` scoped, so a client that uses `presentation-epoch/1` sends `epoch` with them.

### `invoice`

A View with field paths ([field paths](../README.md#field-paths), `field-paths/1`).

| Member | Type | Default and behaviour |
| --- | --- | --- |
| `address` | `{street: string, city: string}` | `{"street":"","city":""}`; settable, a checked field; its field paths are `["street"]` and `["city"]` |
| `billing` | nullable `{name: string, address: {street: string, city: string}}` | `null`; settable, a checked field; its field paths are `["name"]`, `["address"]`, `["address","street"]` and `["address","city"]` |
| `lines` | keyed collection of `{id: int32, label: string, quantity: int32}`, keyed by `id` | `[]`; its field paths are a row's `["label"]` and `["quantity"]` |

Its `fields` are `{"address": {"version"}, "billing": {"version"}}`, starting at version 0. The `address` and `billing`
setters announce a change only when the new value differs from the current one, compared by value (every member), so an
`assign` of an equal value changes nothing, not even the version. The model value `rowConflicts` (`boolean`, default
`false`), set only by `arrange`, makes the row editor check a line as a whole ([row conflicts](../README.md#field-paths)). Its row editor replaces the row with the key and
refuses a negative `quantity` with the message `Quantity must not be negative.` Mutations: `assign` of `address` and of
`billing` (an object or `null`), and `insert`, `replaceAt` and `removeAt` with `member` `lines`.

### `tabs`

A View with region collections ([region collections](../README.md#region-collections)). Members:

| Member | Type | Default and behaviour |
| --- | --- | --- |
| `documents` | region collection of `page` Views; the page may close a region | three regions with the keys `"1"`, `"2"` and `"3"`, which present the `page` Views `one`, `two` and `three` with the ids `00000000000000000000000000000001`, `…02` and `…03`; the first is selected |
| `pinned` | region collection of `page` Views; the page may not close a region | no regions |

The model value `path` (`boolean`, default `false`), set only by `arrange`, makes the first region of `documents` the
window's path region ([navigation paths](../README.md#navigation-paths)), with one route: `pages/{title}` names a page
by its `title`, and opening it pushes a new page with that title. The pages `one`, `two` and `three` therefore have the
routes `pages/one`, `pages/two` and `pages/three`, and a page opened by a route gets the next id, `…04` for the first. The
mutation `syncPath` makes it the path region later, as when the window chooses it after a page's `hello`.
The model value `veto` (`boolean`, default `false`) is not a state member; `assign` sets it and publishes nothing.
While it is `true`, the departure guard of every page refuses, so a `select` that leaves a page and a `close` of a page
are `Rejected` with the rejection `Guard`. Otherwise a request commits, with the arrival `None`. After a `holdGuard`
mutation, the next ask of a page's departure guard stays pending until a `settleGuard` mutation answers it, or until a
later request supersedes the one that asked. The model value `asks` (`int32`, read-only) counts the asks of the pages'
departure guards.

### `page`

The content View that each region of the `tabs` View presents, of kind `page`. Member: `title` (`string`, a text
member), the View's name.

### `files`

A View of [blobs and uploads](../README.md#blobs-and-uploads) (`blobs/1`). The window's blob URLs and upload endpoint
are below `http://conformance.test/`, and it takes uploads of up to 256 MiB (268435456 bytes): the `maximumBytes` of
its upload grant.

| Member | Type | Default and behaviour |
| --- | --- | --- |
| `avatar` | nullable `blob` | `null`; the model and `assign` give it as `{type, text, name}`, whose `text` is its UTF-8 content |
| `lastUpload` | nullable `string` | `null`; the file name of the last upload `attach` claimed |
| `attachments` | keyed collection of `{id: int32, name: string, preview: blob}`, keyed by `id` | `[]`; the model gives `preview` as `{type, text, name}`, as `avatar`; its field paths are a row's `["name"]`, and `preview`, which holds a blob, is none |

| Command | Input | Available | Effect |
| --- | --- | --- | --- |
| `attach` | `upload` | always | claims the upload (a later request that names it is `invalid-input`) and sets `lastUpload` to its file name |

Its row editor replaces the attachment with the key.

### Mutations

| `op` | Members | Effect |
| --- | --- | --- |
| `assign` | `view?`, `member`, `value` | sets a member of the root View, announcing the change; with `view: "detail"`, a member of the `notes` View's `detail` View |
| `insert`, `removeAt`, `replaceAt`, `move` | `member?`, `index`, `item`; `member?`, `index`; `member?`, `index`, `item`; `oldIndex`, `newIndex` | changes `rows`, or the invoice View's `member` (`lines`), at a position |
| `reset` | `items` | replaces all rows at once, as a reset |
| `appendRows` | `start`, `count`, `width?` | appends the rows of the `$rows` generator, one change each |
| `append` | `member`, `text` | appends text to a text field |
| `progress` | `member`, `value` | the running operation of the command reports progress |
| `finish` | `member`, `rows?`, `failure?`, `outcome?` | ends the running operation of the command (see `slowImport`) |
| `rule` | `rule`, `holds` | sets whether a rule of the `form` View holds, announcing no member change |
| `check` | `member`, `running` | starts or ends the asynchronous check of a `form` member, announcing no member change |
| `renew` | none | renews the root View's route ([presentation epochs](../README.md#presentation-epochs)), as one unit |
| `holdGuard` | none | the next ask of a departure guard of the `tabs` View's pages stays pending |
| `syncPath` | none | makes the first region of the `tabs` View's `documents` the window's path region, unless it is one already |
| `settleGuard` | `allow` | answers the held ask, also one that is still to come: `true` lets the page be left or closed, `false` refuses |
| `waitPresented` | `id`, `timeout` | starts a wait of `timeout` milliseconds until a page painted the root View ([painted states](../README.md#painted-states)), named `id` |
| `raise` | `request`, `input` | raises a view request of the `notes` View outside any scope ([view requests](../README.md#view-requests)) |

### `catalog`

A query collection ([`query-collections/1`](../README.md#query-collections)). Members:

| Member | Type | Notes |
| --- | --- | --- |
| `rows` | query collection of `{id: int32, label: string}`, keyed by `id` | `arrange.model.rows` are the source's rows; its first state already has `total`. After a query change the total is `null` until a range is read, which counts it. |

The row source has the fields `id` (sort, `equals`, `range`) and `label` (sort, `equals`, `contains` ignoring case), and
the custom filter `even`, which keeps rows with an even `id`. Rows are ordered by the sort keys, then by `id`. The
mutation `reset` replaces the source's rows, as after rows were added or removed (a refresh: a new version and a new
count); `replaceAt` replaces one row in place (a new version). `expect.model` reads `rows` as the query.
