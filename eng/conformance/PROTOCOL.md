# `rax.conformance-adapter/1`

The protocol between the language-neutral conformance driver ([README](README.md)) and an **adapter**: a small
program, written in the language of the implementation under test, that links that implementation and its test Views
or test application.

- **The driver owns everything the corpus defines:** steps, ids, generators, message writing and matching, unordered
  groups, requirements, timeouts and results.
- **The adapter owns only what the implementation's API decides:** creating a window of a test View, the transport
  seam, the virtual clock, calling the reader and writer, and mapping the implementation's errors to the corpus's codes.

An adapter does not check the spec itself. Where its implementation reports a failure in terms it cannot map to a
corpus code, it reports that failure as it is (for example `unmapped:FormatException`), and the case fails.

Adapters live in each repository's test tree and are never shipped (R7 T1-17).

## Framing

- The driver starts the adapter as a child process, with the command, working directory and environment of its entry
  in [`implementations.json`](implementations.json). One process serves many cases in sequence; the driver may start
  several for parallel runs (`--jobs`).
- **stdin:** one request per line, UTF-8 JSON: `{"seq": <int>, "op": "<name>", "args": {…}}`.
- **stdout:** one response per line: `{"seq": <same>, "ok": <result>}` or
  `{"seq": <same>, "error": {"code": "unsupported" | "failed", "message": "…"}}`. Nothing else may be written to
  stdout; an adapter sends its runtime's console output to stderr.
- **stderr:** a free-form log. The driver writes it to `artifacts/conformance/<implementation>.adapter.log` and shows
  its last lines with a failure.
- Bytes that are not text travel as base64 strings (`input`), so the adapter's own JSON reader never sees a case's
  JSON, and its depth or duplicate-member rules do not interfere.
- `unsupported` means the implementation does not have the capability: an unknown test View, a feature it does not
  list, a seam or op it lacks. The driver reports the case `unsupported`. `failed` is a harness error, and the case
  fails.
- The driver bounds every call: `waitMs` plus 30 seconds where a call waits, 30 seconds otherwise. An adapter that does
  not answer in time is killed and restarted, and the case fails with the last lines of its log. An adapter that exits
  is treated the same way.

## Common operations

| op | args | ok |
| --- | --- | --- |
| `describe` | — | `{protocol, implementation: {id, name, version}, corpora: ["views/4", …], roles: [...], features: [...], capabilities: [...]}`. `features` are what the implementation lists in its own handshake, never a hand-kept list: a producer adapter reads its producer's `hello` reply to a client that lists every feature name, and a client adapter the `hello` its client sends. The driver reports a scenario that requires a feature not in this list `unsupported`. `capabilities` are the runner capabilities of the corpus's requirements, such as `virtual-clock` and `held-delivery`. The driver sends `describe` first and stops if `protocol` is not this protocol. |
| `shutdown` | — | `{}`; the adapter exits after answering. |

## Views protocol 4 (`views/4`)

The corpus is [`specs/application/conformance`](../../specs/application/conformance/README.md).

### Vectors

| op | Role | args | ok |
| --- | --- | --- | --- |
| `views.canonicalize` | producer | `{input}` | `{output: text}`, or `{error: code}` with a code of README "Canonical form" |
| `views.decode` | both | `{type, parameters, input}` | `{value: text}`: the normal form the implementation writes after reading the input; or `{error: "invalid-value"}`. The driver checks both directions by sending the normal form back. |
| `views.readRequest` | producer | `{input, inputBytes, hello}` | `{result: "valid"}` or `{result: "invalid", kind, reason}`, after the README's setup (README "Messages"): a fresh window, and `hello` (base64, the `hello` of the case's file) sent first unless the case is a hello |
| `views.readPush` | client | `{input}` | `{result: "valid" \| "ignored" \| "invalid"}` |
| `views.readReply` | client | `{request, input}` | `{result: "valid" \| "invalid"}` |

### Producer scenarios

One window is open at a time.

| op | args | ok | Notes |
| --- | --- | --- | --- |
| `views.open` | `{view, model, inputBytes, pendingUploads}` | `{}` | The test View `view` with the model values (generators already expanded), failure detail off and publication coalescing off. `inputBytes` and `pendingUploads` are the case's `options` (README "Scenarios"), or `null` for the producer's defaults. `unsupported` for a View the adapter does not have, or a `pendingUploads` its producer cannot set. |
| `views.dispose` | — | `{}` | Closes every connection and the window. |
| `views.connect`, `views.disconnect` | `{conn}` | `{}` | `conn` is the scenario's connection name; the adapter gives it fresh transport identities. |
| `views.send` | `{conn, tag, text}` | `{}` | Delivers the request; its reply is tagged with `tag`. |
| `views.next` | `{conn, waitMs}` | `{message: {replyTo: tag \| null, text} \| null}` | The producer's next message on the connection, waiting up to `waitMs` real milliseconds (0: only what is there). |
| `views.settle` | `{waitMs}` | `{settled: true \| false}` | Returns once the producer has no work in flight that needs no further input, or `false` after `waitMs` real milliseconds. See [Settling](#settling). |
| `views.advance` | `{ms}` | `{}` | Advances the virtual clock. |
| `views.delivery` | `{hold}` | `{}` | `held-delivery`: stop or resume taking pushes. |
| `views.mutate` | `{mutations, each}` | `{}` | README "Mutations", including `waitPresented` and `renew`. |
| `views.flushFields` | `{id}` | `{}` | Starts the flush named `id` (`field-flush/1`). |
| `views.closeWindow` | `{id, timeout}` | `{}` | Starts the close named `id`. |
| `views.setLocale`, `views.setAppearance` | `{locale}`, `{appearance}` | `{}` | Host inputs. `appearance.override` names the members the host decided. |
| `views.upload` | `{conn, name, type, text, size, hold}` | `{status, body}`, or `{}` with `hold` | `blobs/1`: the page of `conn` uploads a file as README "Uploads" says, through the host's upload path with the connection's token: the body is the UTF-8 of `text`, `size` (or `null`, the body's length) its `Content-Length`. `status` is the HTTP status of the host's answer and `body` its JSON. With `hold` (a name, or `null`), the adapter hands the host the body's bytes but not its end, and answers once the host is receiving the upload, so it counts as an upload being received, or once the host answered without receiving it, as for a `Content-Length` above `maximumBytes`; `views.endUpload` then gives that answer. |
| `views.endUpload` | `{hold}` | `{status, body}` | Ends the body of the held upload `hold` and answers with the host's answer, as `views.upload`. |
| `views.readMember` | `{member}` | `{value}` | The root View's member as its wire value. |
| `views.provide` | `{name}` | `{value \| null}` | A `provided` id that the driver must write before any message carried it. |
| `views.waitResult`, `views.closeResult`, `views.flushResult` | `{id, waitMs}` | `{result: true \| false \| null}` | `null`: still pending after `waitMs`. |
| `views.languages` | `{conn}` | `{value: [...]}` | The languages the producer reports for the connection. |

#### Settling

An asynchronous producer can still be working when a step returns: a model turn is queued, a delivery has not handed
its frame to the transport, or a reply is completing through a continuation. A check made then can miss a late push
(R6 F8). The driver therefore calls `views.settle` before each `expect` step and before its end-of-scenario check that
no connection has a message that no receive step matched.

`views.settle` returns once the producer is idle: it has run the work it has, and is waiting only for input that the
scenario gives, such as the virtual clock, a page's reply or a released delivery. Work that waits for such input is not
in flight, so `settle` does not wait for it.

- **Synchronous (sans-IO) producer:** its messages are in its outbox when a call returns, so the adapter answers at
  once with `settled: true`.
- **.NET producer:** the adapter repeats three steps until a pass records no new message:
  - it runs the window's queued model turns;
  - it waits until every delivery of the window has settled what it was handed (`BridgeReplyOrder.WhenPublished`),
    unless delivery is held;
  - it yields to the continuations that complete replies and pushes.

`settled: false` is not a failure by itself; the check that follows reports what is missing or left over.

### Client scenarios

The driver plays the producer: it writes each message that a `receive` step delivers, and checks each request that a
`send` step expects, with the README's ids, generators and comparison rules, including its session and languages
rules. The adapter owns the client, its test Views, the scripted host bridge that the client talks to (README
"Steps"), and the virtual clock. It runs the calls in the client's API. Each op that drives the client answers once the
client has run the work it queued, such as a reply's continuation; for a JavaScript client, a few turns of the event
loop.

| op | args | ok | Notes |
| --- | --- | --- | --- |
| `client.begin` | `{view}` | `{}` | A fresh scenario: no connection, no open route, no diagnostics, and the virtual clock at 0 on the corpus's default day. `unsupported` for a View the adapter does not have. |
| `client.end` | — | `{}` | Releases every route and the connection, and stops the virtual clock. |
| `client.connect` | `{conn}` | `{}` | The host bridge reports a new connection named `conn`, after any earlier one. The client then sends its `hello`. |
| `client.disconnect` | `{conn}` | `{}` | The host bridge reports the connection lost; requests still waiting for a reply fail. |
| `client.nextRequest` | `{conn, waitMs}` | `{request: {tag, text} \| null}` | The client's next request on the connection that the driver has not taken, waiting up to `waitMs` real milliseconds (0: only what is there). `tag` names it for its reply. |
| `client.deliver` | `{conn, replyTo, text}` | `{}` | The host delivers `text`: as the reply to the request `replyTo` (a `tag`), or as a push when `replyTo` is `null`. |
| `client.advance` | `{ms}` | `{}` | Advances the virtual clock, running each timer that falls due, in order. |
| `client.call` | `{id, call}` | `{}` | Starts the step's `call` (README "Calls") and names it `id`. For `start`, `wait`, `status` and `cancel`, `call.operation` is the operation id's value as the driver writes it. `unsupported` for a call the client has no API for. |
| `client.callResult` | `{id, waitMs}` | `{state: "pending"}`, `{state: "resolved", value}` or `{state: "rejected", error: {kind, reason?, failure?}}` | Waits up to `waitMs` for the call to complete. `value` is the call's result as JSON. `error.kind` is the client error's kind, `domain-failed` with `failure` for a declared failure, or `unmapped:<what>` for an error that is not the client's documented kind. |
| `client.state` | `{route}` | `{value}` | The state the client exposes for the route. |
| `client.operation` | `{operation}` | `{status}` | The last status of the operation that the client observed, without `revision`, or `null`. |
| `client.notifications` | `{route}` | `{count}` | How often the route's subscribers were notified of a new state since its `open` resolved. |
| `client.diagnostics` | — | `{codes}` | The codes of every diagnostic the client reported since `client.begin`, in order. |
| `client.connection` | — | `{state}` | The connection state the client reports, or `null`. |
| `client.locale`, `client.appearance` | — | `{locale}`, `{appearance}` | The window's locale, and the appearance the client keeps from the host. |
| `client.requests` | `{route, request}` | `{inputs}` | The inputs the `handle` handler of that route and view request was called with, in order. |

The driver runs a client scenario as the README's client runner does:

- **Skipped steps.** It skips the steps that only a producer runner runs, and the producer's expectations (`model`,
  `presented`, `closed`, `flushed`, `languages`).
- **Unordered steps.** It delivers `unordered` messages in the order the steps list them.
- **Producer capabilities.** It meets `held-delivery` itself, since it is the producer runner and skips `delivery`
  steps. The adapter lists `virtual-clock`.
- **Features.** A client adapter's `features` are those of the `hello` its client sends to a probe connection.
- **Expectations.** It reads a call that should have completed, and a `requests` expectation, until they match, up to
  the receive timeout. It reads everything else once.
- **The end.** It requires that no connection has a request that no `send` step took.
- **Ids.** A `wire` id that the client sends binds the first value the driver sees, as the README says. The driver
  writes the bound value after that. It writes a `provided` id that it must write before the client sent it as
  `"provided:<name>"`, or as the number 1 when the name contains `epoch`. The test Views give the view requests'
  contracts these values too.

## Assets (`assets/2`)

The corpus is [`specs/assets/vectors`](../../specs/assets/vectors/README.md): archives for the reader rules of
`runic.assets.archive/1` and `/2`. Every case has the role `reader`.

The adapter lists in `capabilities` the [reading modes](../../specs/assets/archive-v1.md#eager-and-lazy-reading) its
reader has, `eager-reading` and `lazy-reading`, and `error-codes` when its reader reports the spec's
[error codes](../../specs/assets/archive-v1.md#error-codes). The driver runs each case in every listed mode; with
neither mode the case is `unsupported`.

| op | args | ok |
| --- | --- | --- |
| `assets.read` | `{archive, mode}`: the archive's bytes in base64, and `eager` or `lazy` | `{result: "valid", assets}` or `{result: "invalid", stage, error?, message?}` |

The adapter reads the archive with its implementation's default limits, in the mode's reader, then opens every asset
the reader reports, in order, and reads it to its end. It answers:

- `valid`, when the read and every open succeeded: `assets` in the reader's order, each with the reported `path`,
  `mediaType`, `length`, `sha256`, `entryPoint` and `cacheMode`, and `served: {length, sha256}`, the byte count and
  lower-case SHA-256 digest of the bytes the open gave;
- `invalid`, when the read (`stage: "read"`) or an open (`stage: "open"`) failed: `error` is the spec's code, or
  `unmapped:<what>` for a failure that is not the reader's documented kind; an implementation without codes leaves it
  out for its documented kind. `message` is the failure's text, for the report.

The driver compares as the [vectors README](../../specs/assets/vectors/README.md#running-a-case) says:

- a valid case: the assets member by member and in order, every `served.length` equal to `length`, and in eager
  reading every `served.sha256` equal to `sha256`;
- an invalid case: `invalid`, from the read in eager reading; any `unmapped:` error fails; with `error-codes` the code
  must equal the case's.

A case passes when it passes in every mode. `expectLazy`, where a case has it, replaces `expect` in lazy reading.

## Desktop server (`desktop-server/1`)

The corpus is [`specs/desktop/server`](../../specs/desktop/server/README.md): the requests, responses and WebSocket
frames of the server under a Runic Desktop host. Every case has the role `server`. The adapter runs the server with
the corpus's [test application](../../specs/desktop/server/README.md#test-application) and records what the
application sees; the driver is the client, over real sockets to the port the adapter reports, and checks every byte
the server writes.

`capabilities` lists `stop` when the adapter can stop a running server, `peer-admission` when the server can admit or
refuse a peer before it reads its request, and `ipv6-loopback` when it can listen on `[::1]` on this machine. A case
that requires one the adapter does not list is `unsupported`.

| op | args | ok |
| --- | --- | --- |
| `server.start` | `{maxMessageBytes?, ipv6Loopback?, admit?}`: the case's `config` | `{port, address}`: a fresh server with a fresh application, on a port the system chose; `address` is `127.0.0.1` or `::1`. A running server is stopped first. |
| `server.stop` | — | `{}` once the server stopped; its waiting requests end. |
| `server.events` | `{waitMs, count}` | `{events: [...]}`: the events recorded since the last call, once there are at least `count` or after `waitMs`. Each has `type` (`request`, `message`, `closed`, `aborted`) and `conn`, the request's `X-Rax-Conformance` value; a `request` has `method`, `target` (as sent), `websocket` (the server reports an upgrade it can accept) and `headers` (`[name, value]` pairs, names in lower case); a `message` has `kind` (`text` or `binary`) and `data` (base64); a `closed` has the peer's `code` and `reason`, both `null` when the connection ended without the peer's close frame. |
| `server.wsSend` | `{conn, kind, data}` | `{}`: the host sends the message (data in base64) on the WebSocket that the connection `conn` opened. |
| `server.wsClose` | `{conn, code, reason}` | `{}`: the host closes that WebSocket after the messages it sent. |

## Host protocol (`host/1`)

The corpus is [`specs/desktop/host-conformance`](../../specs/desktop/host-conformance/README.md): the scenarios of the
Runic host protocol, `runic-host/1`. For the `guest` role the driver plays the host. It writes the offer, reads the
launch lines, connects to the guest's channel over a real loopback WebSocket, sends and matches every message, computes
the nonce, keys and proofs, and makes the region scenarios' frame and component requests. The adapter starts the guest
with the corpus's [test application](../../specs/desktop/host-conformance/README.md#test-application) and runs its
actions. The driver has no steps for the `host` role yet.

`describe` adds `pageTransport`, the guest's [page-transport profile](../../specs/desktop/host-conformance/README.md#page-transport)
(`webui-compat/52f9e75` for Runic's .NET guest). `features` are the features the guest lists in its `hello` reply when
its application opts in to everything the corpus uses.

One guest runs at a time:

| op | args | ok |
| --- | --- | --- |
| `host.guest.start` | `{application, limits}`: the scenario's `arrange` | `{}`: the guest starts in hosted mode with the test application and these limits, reading its standard input |
| `host.guest.input` | `{line}` | `{}`: writes the line and a line feed to the guest's standard input; `null` writes nothing |
| `host.guest.closeInput` | — | `{}`: closes the guest's standard input |
| `host.guest.line` | `{waitMs}` | `{line}`: the guest's next line of standard output, or `null` when none came within `waitMs` |
| `host.guest.output` | — | `{lines}`: every line of standard output so far, for failure output |
| `host.guest.exit` | `{waitMs}` | `{state, code?, failure?, message?, processExits}` once the guest's hosted run ended, or after `waitMs`: `state` is `running`, `returned` (with Run's `code`) or `failed` (with the hosted start's `failure` code, or `unmapped:<what>`); `processExits` are the codes with which the guest tried to end its process |
| `host.guest.act` | `{as, action}`: the step's `guest` object, its `$file` values resolved to paths, and its `as` (or `null`) | `{}` at once; the action runs on |
| `host.guest.result` | `{as, waitMs}` | `{state}` once the action ended or after `waitMs`: `pending`; `cancelled`; `failed` with `codes`, the open's diagnostic codes; `faulted` with `message`; or `done` with `value`, the README's result members (`opened: true`, `closed`, `outcome`, `selected`, `dismissed`, `chosen`, `button`, `text`, `monitors`, or a state the driver waits for: `activity`, `appearance`, `dropped`, `invoked`, `locale`, `placement`, read again on each call) |
| `host.guest.stop` | — | `{}` once the guest's run ended and everything it held is released |

An outcome is `{"success": {}}`, `{"unavailable": <reason>}` or `{"failed": <code>}` in kebab case. The driver compares
results by JSON value, and waits for a state by asking again until it matches or its timeout passes.

## Other corpora

Each corpus module adds ops under its own prefix:

- Command Line: `cl.parse {catalog, argv, env}`, `cl.readEnvelope {input}`, `cl.write {case}`;
- Translations: `tr.loadPack {artifact}`, `tr.format {pack, message, args, locale}`, `tr.resolveReference {text}`;
- the host protocol's host role: `host.host.*`;
- the Desktop presentation contract (`desktop.presentation/1`): `desktop.*`.

The framing, the results, the statuses and the driver core are the same for every corpus.
