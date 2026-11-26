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
| `describe` | — | `{protocol, implementation: {id, name, version}, corpora: ["views/4", …], roles: [...], features: [...], capabilities: [...]}`. `features` are what the implementation lists in its own handshake; the .NET adapter reads its producer's `hello` reply. `capabilities` are the runner capabilities of the corpus's requirements, such as `virtual-clock` and `held-delivery`. The driver sends `describe` first and stops if `protocol` is not this protocol. |
| `shutdown` | — | `{}`; the adapter exits after answering. |

## Views protocol 4 (`views/4`)

The corpus is [`specs/application/conformance`](../../specs/application/conformance/README.md).

### Vectors

| op | Role | args | ok |
| --- | --- | --- | --- |
| `views.canonicalize` | producer | `{input}` | `{output: text}`, or `{error: code}` with a code of README "Canonical form" |
| `views.decode` | both | `{type, parameters, input}` | `{value: text}`: the normal form the implementation writes after reading the input; or `{error: "invalid-value"}`. The driver checks both directions by sending the normal form back. |
| `views.readRequest` | producer | `{input, inputBytes}` | `{result: "valid"}` or `{result: "invalid", kind, reason}`, after the README's setup: a fresh window, and a hello with every feature first unless the case is a hello |
| `views.readPush` | client | `{input}` | `{result: "valid" \| "ignored" \| "invalid"}` |
| `views.readReply` | client | `{request, input}` | `{result: "valid" \| "invalid"}` |

### Producer scenarios

One window is open at a time.

| op | args | ok | Notes |
| --- | --- | --- | --- |
| `views.open` | `{view, model, inputBytes}` | `{}` | The test View `view` with the model values (generators already expanded), failure detail off and publication coalescing off. `unsupported` for a View the adapter does not have. |
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
| `views.setLocale`, `views.setAppearance` | `{locale}`, `{appearance}` | `{}` | Host inputs. |
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

These are a later slice (R7 G10, #1350). The adapter implements the host bridge that the client talks to, and the
driver plays the producer:

- `client.connect`, `client.disconnect`;
- `client.nextRequest {conn, waitMs}` and `client.deliver {conn, replyTo, text}`;
- `client.call {id, op, …}` (README "Calls") and `client.callResult {id, waitMs}`;
- the read ops `client.state {route}`, `client.notifications {route}`, `client.diagnostics`, `client.connection`,
  `client.locale`, `client.appearance` and `client.requests {route, request}`.

## Other corpora

Each corpus module adds ops under its own prefix:

- Command Line: `cl.parse {catalog, argv, env}`, `cl.readEnvelope {input}`, `cl.write {case}`;
- Translations: `tr.loadPack {artifact}`, `tr.format {pack, message, args, locale}`, `tr.resolveReference {text}`;
- Assets: `assets.read {archive}`;
- the host protocol: `host.*`, with guest and host roles.

The framing, the results, the statuses and the driver core are the same for every corpus.
