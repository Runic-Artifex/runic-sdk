# Runic Desktop server corpus

Portable vectors for the **server** of a Runic Desktop host: the part between the loopback socket and the host's
request handler. It covers HTTP/1.1 framing, the `Host` check, the default headers, file bodies with byte ranges,
WebSocket framing and the close handshake. The host decides everything else: what a path serves, the bootstrap
scripts, origin and credential admission, and the Views protocol ([presentation host](../presentation-host.md)). The
corpus fixes those decisions with a small [test application](#test-application), so a vector checks only the server.

The rules below are normative for every server a Runic host uses: runic-server
([`native/runic-server`](../../../native/runic-server), the Rust crate `rax_server` of D080.7), the planned C++ server
on cpp-httplib (R7 C1) and later ones. They came from runic-server's own tests and the R4e research, which drove
runic-server and a cpp-httplib server with the same client (R4e F4, open question 4; R7 T2-23).

| File | Cases |
| --- | --- |
| [`http.json`](http.json) | 35: the Host check, default headers, the server's own errors, framing, persistent connections, the request head, bodies, uploads, limits, a requester that leaves, stop, the IPv6 listener and peer admission |
| [`files.json`](files.json) | 15: file bodies, byte ranges, and ranges a server ignores |
| [`handshake.json`](handshake.json) | 12: the WebSocket opening handshake |
| [`frames.json`](frames.json) | 39: data and control frames, the message limit, and failing a connection that breaks the protocol |
| [`close.json`](close.json) | 13: the closing handshake |

The directory also holds the conformance driver's [`cases.json`](cases.json) and [`status/`](status) (below).

## Requirement levels

Every case has a `level`, the level of the rule it checks, and a `rule`, the anchor of that rule below.

- **`must`**: an RFC MUST, or a Runic requirement. An implementation that fails such a case records `known-failure`
  with an issue; it cannot record a `deviation`.
- **`should`**: an RFC SHOULD. The case states what Runic servers do. An implementation may record a `deviation` with a
  row in [Recorded differences](#recorded-differences) that says why.
- **`may`**: the RFC allows several answers. The case states the one Runic servers give, so that every Runic host
  behaves the same. Another answer is a `deviation` with a row below.

Where a rule has a MUST part and a SHOULD part, they are separate cases: for example, a server MUST end a connection
that breaks the WebSocket protocol (`server.frames.unmasked`), and SHOULD send a close frame with code 1002 first
(`server.frames.unmasked-close-code`).

Sources: RFC 9110 (HTTP semantics), RFC 9112 (HTTP/1.1), RFC 6455 (WebSocket), and runic-server's
[`server.rs`](../../../native/runic-server/src/server.rs), [`host.rs`](../../../native/runic-server/src/host.rs),
[`files.rs`](../../../native/runic-server/src/files.rs) and
[`websocket.rs`](../../../native/runic-server/src/websocket.rs), cited below as `file:line` at `main` c574ee0.

## Rules

### Requests and responses

| Rule | Level | Requirement | Source | runic-server |
| --- | --- | --- | --- | --- |
| <a id="host-check"></a>`host-check` | MUST | A loopback listener answers a request (an upgrade too) whose `Host` is not `localhost` or a loopback address at the bound port with **421**, before the host sees it. A `Host` without a port, or an HTTP/1.0 request without `Host`, is refused the same way. | [presentation host](../presentation-host.md#security-policy); RFC 9110 §15.5.20 (421) | `host.rs:8-46`, `server.rs:693-697` |
| <a id="host-missing"></a>`host-missing` | MUST | An HTTP/1.1 request without `Host` gets **400**, before the host sees it. | RFC 9112 §3.2 | `server.rs:689-692` |
| <a id="host-two-lines"></a>`host-two-lines` | MUST | A request with more than one `Host` field line gets **400**, before the Host check. | RFC 9112 §3.2; [presentation host](../presentation-host.md#security-policy) | `server.rs:686-688` |
| <a id="server-errors"></a>`server-errors` | SHOULD | The server's own responses (400, 421, 503) have no body, `Content-Length: 0`, no `Content-Type`, `Cache-Control: no-store` and the default headers. | Runic | `server.rs:662-669` |
| <a id="default-headers"></a>`default-headers` | MUST | Every response the server writes gets `X-Content-Type-Options: nosniff`, and `X-Frame-Options: SAMEORIGIN` unless the host set `X-Frame-Options` itself or a `Content-Security-Policy` that names `frame-ancestors` (any case), which then decides alone. | [presentation host](../presentation-host.md#security-policy); CSP 3 §6.4.2 | `server.rs:639-660` |
| <a id="host-headers"></a>`host-headers` | MUST | The host's headers are sent as given, repeated fields such as two `Set-Cookie` included. | Runic | `server.rs:731-747` |
| <a id="content-types"></a>`content-types` | MUST | The server never adds a `Content-Type` the host did not set, and a 204 has no `Content-Length`. | RFC 9110 §8.3, §8.6 | `server.rs:734-739` |
| <a id="bodies"></a>`bodies` | MUST | An in-memory body is sent with its `Content-Length`; a streamed body's chunks arrive in order (chunked on HTTP/1.1); a `HEAD` response has the `GET` headers and no content. | RFC 9110 §8.6, §9.3.2; RFC 9112 §6, §7.1 | `server.rs:731-775` |
| <a id="request-head"></a>`request-head` | MUST | The host receives the method as sent, whatever it is, and the target as sent, still percent-encoded (`%2e%2e` and `%2F` included). Path resolution is the host's ([presentation host](../presentation-host.md#security-policy)). | Runic | `server.rs:713-724` |
| <a id="persistence"></a>`persistence` | MUST | HTTP/1.1 connections persist; pipelined requests are answered in order; `Connection: close`, and HTTP/1.0 without keep-alive, close the connection after the response. | RFC 9112 §9.3, §9.3.2, §9.6 | hyper |
| <a id="request-bodies"></a>`request-bodies` | MUST | A request body framed by `Content-Length` or by the chunked coding (with chunk extensions) reaches the host whole. | RFC 9112 §6.3, §7.1 | `server.rs:414-438` |
| <a id="framing-errors"></a>`framing-errors` | MUST (request line: SHOULD) | Conflicting `Content-Length` values, or white space between a field name and its colon, get **400** (or the connection ends) before the host; a request with both `Content-Length` and `Transfer-Encoding` closes the connection after the response; an invalid request line gets 400. | RFC 9112 §3, §5.1, §6.3 | hyper |
| <a id="limits"></a>`limits` | MUST | Header and request limits fail closed: a 1 MiB header line gets a 4xx, or the connection ends, before the host. The numbers are the server's ([presentation host](../presentation-host.md#security-policy)). | Runic | hyper's defaults |
| <a id="lifecycle"></a>`lifecycle` | MUST | A requester that leaves before the answer is reported to the host; stopping the server ends a waiting request with 503 or by ending its connection. | Runic | `server.rs:614-637`, `server.rs:540-565` |
| <a id="listener"></a>`listener` | MUST | An IPv6 loopback listener serves on `[::1]` and admits `[::1]:port`. | Runic | `server.rs:348-355` |
| <a id="admission"></a>`admission` | MUST, SHOULD | With peer admission, a refused peer's request never reaches the host (MUST), and the server closes the connection before reading it, so the peer gets no response (SHOULD). | Runic (C ABI 5 `on_admit`) | `server.rs:229-303` |

### File bodies and byte ranges

The test application's `/file` is the 10 bytes `0123456789`.

| Rule | Level | Requirement | Source | runic-server |
| --- | --- | --- | --- | --- |
| <a id="file-bodies"></a>`file-bodies` | MUST | A file body is sent whole with its `Content-Length` and the host's `Content-Type`; `HEAD` sends the headers only; a missing file gets 404 with no body and no `Content-Type`. | RFC 9110 §9.3.2; Runic | `files.rs:25-77` |
| <a id="accept-ranges"></a>`accept-ranges` | MAY | A file body sent with 200 carries `Accept-Ranges: bytes`. | RFC 9110 §14.3 | `files.rs:43-45` |
| <a id="ranges"></a>`ranges` | MUST | One satisfiable byte range of a `GET` (`a-b`, `a-`, `-n`, an end past the file) gets **206** with `Content-Range: bytes a-b/length` and those bytes. | RFC 9110 §14.1.2, §14.4, §15.3.7 | `files.rs:106-151` |
| <a id="ranges-unsatisfiable"></a>`ranges-unsatisfiable` | SHOULD | A range that starts at or past the end, or `-0`, gets **416** with `Content-Range: bytes */length`, no body and no `Content-Type`. | RFC 9110 §14.4, §15.5.17 | `files.rs:55-58`, `files.rs:69-77` |
| <a id="ranges-ignored"></a>`ranges-ignored` | MUST | The whole file is sent (200) for a range of another unit, for a `Range` with `If-Range` (the server sends no validators, so none can match), and for a `Range` on any method but `GET`. | RFC 9110 §13.1.5, §14.2 | `files.rs:42-43`, `files.rs:106-109` |
| <a id="ranges-several"></a>`ranges-several` | MAY | Several ranges get the whole file (200), not `multipart/byteranges`. | RFC 9110 §14.2, §14.6 | `files.rs:114-116` |
| <a id="ranges-invalid"></a>`ranges-invalid` | MAY | A syntactically invalid range (`bytes=a-b`, `bytes=9-1`) gets the whole file (200), not 416. | RFC 9110 §14.2 | `files.rs:120-150` |

### WebSocket

| Rule | Level | Requirement | Source | runic-server |
| --- | --- | --- | --- | --- |
| <a id="ws-handshake"></a>`ws-handshake` | MUST | A `GET` with `Upgrade: websocket` (any case), a `Connection` that includes the `upgrade` token, version 13 and a key, that the host accepts, gets 101 with `Upgrade`, `Connection`, the `Sec-WebSocket-Accept` of RFC 6455 §1.3, no `Content-Length`, the subprotocol the host chose or none, and no extension. A message the host sends as it accepts follows the 101. | RFC 6455 §4.2.1, §4.2.2, §9.1 | `server.rs:671-678`, `websocket.rs:480-548` |
| <a id="ws-version"></a>`ws-version` | MUST, SHOULD | An upgrade with another `Sec-WebSocket-Version` gets a 4xx with `Sec-WebSocket-Version: 13` (MUST), and that 4xx is 426 (SHOULD). | RFC 6455 §4.2.2, §4.4 | `server.rs:700-702`, `websocket.rs:490-505` |
| <a id="ws-invalid-handshake"></a>`ws-invalid-handshake` | MUST | An upgrade without a key, a `POST` upgrade, or one the host refuses never gets 101. | RFC 6455 §4.2.1, §4.2.2 | `websocket.rs:480-487`, `server.rs:777-782` |
| <a id="ws-messages"></a>`ws-messages` | MUST | The host receives each message whole, in order, with its kind: fragments joined, control frames between fragments handled, empty messages delivered, and text validated as a whole message, so a fragment may end inside a character. | RFC 6455 §5.4, §5.6 | `websocket.rs:763-861` |
| <a id="ws-ping"></a>`ws-ping` | MUST | A ping is answered with a pong with the same payload; an unsolicited pong is ignored. A server MAY answer only the latest of several unanswered pings. | RFC 6455 §5.5.2, §5.5.3 | `websocket.rs:850-855` |
| <a id="ws-lenient-lengths"></a>`ws-lenient-lengths` | MAY | A client frame whose length is not in its shortest form is accepted. | RFC 6455 §5.2 (binds the sender) | `websocket.rs:671-706` |
| <a id="ws-server-frames"></a>`ws-server-frames` | MUST | The server's frames are unmasked, have no reserved bits and the shortest length; control frames are final and at most 125 bytes; text is UTF-8. The driver checks this on every frame of every case. A server MAY fragment its messages. | RFC 6455 §5.1, §5.2, §5.4, §5.5 | `websocket.rs:454-470` |
| <a id="ws-limit"></a>`ws-limit` | MUST | A message up to the configured limit is delivered; a message over it, whether one frame, a declared length or fragments together, is never delivered and ends the connection, without reading a declared payload over the limit. | Runic ([presentation host](../presentation-host.md#security-policy): limits fail closed) | `websocket.rs:702-705`, `websocket.rs:786-789`; `max_message_bytes`, default 64 MiB (`server.rs:117`) |
| <a id="ws-fail"></a>`ws-fail` | MUST | A connection that breaks the protocol (unmasked frame, reserved bits, reserved opcode, fragmented or long control frame, continuation without a message, a message inside another, a 64-bit length with its top bit set) or the limit ends: nothing more is delivered, the host learns that it closed, and the server writes at most a close frame before it ends the connection. A frame cut short by the peer's end ends the connection with no close code. | RFC 6455 §5.1, §5.2, §5.4, §5.5, §7.1.7 | `websocket.rs:671-706`, `websocket.rs:878-888` |
| <a id="ws-fail-close-code"></a>`ws-fail-close-code` | SHOULD | Before it ends such a connection, the server sends a close frame with 1002 (protocol error), 1009 (message too big) or 1007 (text not UTF-8). | RFC 6455 §7.1.7, §7.4.1 | `websocket.rs:878-888` |
| <a id="ws-utf8"></a>`ws-utf8` | MUST | A text message that is not UTF-8, also across fragments, fails the connection. | RFC 6455 §8.1 | `websocket.rs:864-873` |
| <a id="ws-close"></a>`ws-close` | MUST (data after close: SHOULD) | The peer's close is answered with a close frame, and the connection then ends. Data after the peer's close is not delivered. | RFC 6455 §5.5.1, §1.4 | `websocket.rs:831-849` |
| <a id="ws-close-report"></a>`ws-close-report` | MUST | The host learns the peer's close code and reason; a close without a code is reported as 1005. Codes 1000–1003, 1007–1014 and 3000–4999 are accepted. | RFC 6455 §7.1.5, §7.4.1, §7.4.2; Runic (C ABI 5 `on_closed`) | `websocket.rs:729-731`, `websocket.rs:831-848` |
| <a id="ws-close-answer"></a>`ws-close-answer` | MAY | The answer to the peer's close has code 1000 and no reason, whatever the peer sent, as the .NET host does. | RFC 6455 §5.5.1 ("typically echoes") | `websocket.rs:847` |
| <a id="ws-close-tcp"></a>`ws-close-tcp` | SHOULD | After the closing handshake the server ends the TCP connection first, without waiting for the client to. | RFC 6455 §7.1.1 | `websocket.rs:592-626` |
| <a id="ws-host-close"></a>`ws-host-close` | MUST | The host's close is written after the messages it sent before it, with its code and reason; the peer's answer is reported to the host. | Runic | `websocket.rs:244-302` |
| <a id="ws-close-invalid"></a>`ws-close-invalid` | SHOULD | A close frame with a code outside the accepted ranges (such as 999, 1005 or 2000), or a one-byte payload, gets a close with 1002; a reason that is not UTF-8 gets 1007. | RFC 6455 §5.5.1, §7.4, §8.1 | `websocket.rs:831-845` |

Not normative, so not in the corpus: the default headers on a 101 (runic-server adds them, cpp-httplib cannot); pings
the server sends itself, and idle timeouts (runic-server has none; a quiet Views connection must stay open); how long
the server waits for the peer's close answer (runic-server: 5 s, `websocket.rs:49`).

### Questions for the maintainer

1. **The answer to the peer's close** (`ws-close-answer`): MAY, with 1000 as Runic's answer. RFC 6455 says only that an
   endpoint "typically echoes" the code. *Recommendation:* keep MAY; cpp-httplib's echo (R4e §1.2) becomes a
   `deviation` row of the C++ server rather than a failure.
2. **Reporting the peer's close code to the host** (`ws-close-report`): MUST, because C ABI 5 reports it. cpp-httplib
   cannot until upstream change S3 (R4e §1.5). *Recommendation:* keep MUST; the C++ server records `known-failure`
   with the S3 issue until C1 carries the patch.
3. **Close frames before failing** (`ws-fail-close-code`, `ws-close-invalid`): SHOULD, as RFC 6455 §7.1.7 says, with
   ending the connection (`ws-fail`) as the MUST. *Recommendation:* keep this split, so cpp-httplib passes every MUST
   case today and its missing close frames are `deviation` rows until S1.
4. **Malformed `Range`** (`ranges-invalid`): MAY, pinned to 200. cpp-httplib answers 416 before routing.
   *Recommendation:* pin 200, so a Runic host answers one way, and let the C++ server record a `deviation`.
5. **An invalid `Host` value**, such as `localhost:+8080`: RFC 9112 §3.2 asks for 400; runic-server answers 421 and
   the corpus has no case. *Recommendation:* answer 400 for a value that does not parse as `uri-host [":" port]` and
   421 for a well-formed foreign one, then add the cases. This needs the exact grammar decided first.
6. **Absolute-form targets** (`GET http://127.0.0.1:p/x HTTP/1.1`): RFC 9112 §3.2.2 says an origin server uses the
   target's authority and ignores `Host`; runic-server checks `Host` and passes the path to the host. No case yet.
   *Recommendation:* refuse an absolute-form target on a loopback listener with 400; browsers never send it to an
   origin server.
7. **A second implementation now:** the .NET host's default server (Kestrel) could run this corpus as `dotnet-kestrel`.
   *Recommendation:* later. Kestrel sends whole files (no ranges), so the range cases would need a decision first.

## Test application

The adapter runs the server under test with this application. A request with the header `X-Rax-Conformance: <conn>`
is recorded for the driver as an event of the connection `<conn>`; others (other processes probing a fresh port) are
answered but not recorded.

| Path | Answer |
| --- | --- |
| `/text` | 200, `Content-Type: text/plain; charset=utf-8`, the in-memory body `hello` |
| `/stream` | 200, `text/plain; charset=utf-8`, a streamed body of the chunks `one `, `two ` and `three` |
| `/empty` | 204, no headers, no body |
| `/file` | a file body of the 10 bytes `0123456789`, `Content-Type: application/octet-stream`, status 200 |
| `/missing-file` | a file body of a file that does not exist, `application/octet-stream`, status 200 |
| `/frame-deny` | 204, `X-Frame-Options: DENY` |
| `/csp-ancestors` | 204, `Content-Security-Policy: default-src 'self'; frame-ancestors http://127.0.0.1:8080` |
| `/csp-ancestors-upper` | 204, `Content-Security-Policy: FRAME-ANCESTORS 'self'` |
| `/csp-other` | 204, `Content-Security-Policy: default-src 'self'` |
| `/cookies` | 204, `Set-Cookie: a=1` and `Set-Cookie: b=2` |
| `/upload` | reads the whole body, then 200, `text/plain; charset=utf-8`, `<length> <SHA-1 in lower-case hex>` |
| `/slow` | never answered |
| `/ws` | a WebSocket upgrade is accepted, with the subprotocol `rax.test` when the client offers it and none otherwise; with the query `hello`, the host sends the text message `hello` as it accepts. Any other request: 400 |
| `/ws-reject` | 403 |
| anything else | 404, no body |

On a WebSocket the application consumes every message at once, sends and closes only when the driver asks, and
releases the connection when it closes. The server runs with its defaults except where a case's `config` sets
`maxMessageBytes`, `ipv6Loopback`, or `admit` (peer admission that admits or refuses every peer).

## Vectors

A file has the format `rax.desktop.server-vector/1`, an `id` `server.{file}`, and `cases`. A case has an `id` that
starts with the file's, a `level`, a `rule`, optional `requires` (adapter capabilities: `ipv6-loopback`,
`peer-admission`, `stop`) and `config`, and `steps`, run in order. Strings in requests, headers and expected values
may use `{authority}` (the listener's `Host` value), `{port}` and `{port+1}`.

### Steps

Each step has one kind. Connections are named by the case (`a`, `b`, …).

| Step | Meaning |
| --- | --- |
| `{"connect": "a"}` | open a TCP connection to the listener |
| `{"send": "a", "request": {method, target, version, headers, body, tag}}` | write a request: `GET`, `HTTP/1.1` and `[["Host", "{authority}"]]` unless given, then `X-Rax-Conformance: a` unless `tag` is false, then the body bytes as given (no framing is added) |
| `{"send": "a", "upgrade": {method, target, headers, omit}}` | a WebSocket upgrade: the request above with `Connection: Upgrade`, `Upgrade: websocket`, `Sec-WebSocket-Version: 13` and the key of RFC 6455 §1.3, each replaced by a header of the same name in `headers` or left out when named in `omit` |
| `{"send": "a", "raw": {…, methods}}` | write these bytes; `{tag}` in `text` becomes the tag header line. `methods` are the requests they start, for reading their responses (default `["GET"]`) |
| `{"send": "a", "frames": [frame, …]}` | write client frames, below |
| `{"halfClose": "a"}`, `{"close": "a"}` | end the client's sending side; drop the connection |
| `{"response": "a", "expect": {…}, "orEnd": true}` | read the next response (skipping 1xx other than 101) and compare: `status` (a number, a list, or `"4xx"`), `headers` (lower-case name to a value, `null` for absent, a list for every value in order, `{"token"}` for a list member, `{"ignoreCase"}`), `body` (below), `closes` (the server then ends the connection). With `orEnd`, the server may end the connection instead of answering |
| `{"frame": "a", "expect": {opcode, payload, code, reason}}` | read the next message: a control frame, or a data message with its fragments joined |
| `{"closeOrEnd": "a"}` | the server ends the connection, writing at most a close frame first (which the driver answers by ending its side) |
| `{"end": "a", "shutdown": true}` | the server ends the connection without writing anything more; with `shutdown`, the driver first ends its own side |
| `{"host": "a", "expect": [event, …]}` | the host's next events for the connection, in order: `request` (`method`, `target`, `websocket`, `headers`), `message` (`kind`, `data`), `closed` (`code`, `null` when the peer sent no close frame, and `reason`), `aborted`. An empty list: none within 250 ms |
| `{"hostSend": "a", "kind", "data"}`, `{"hostClose": "a", "code", "reason"}` | the host sends a message on, or closes, the WebSocket of the connection |
| `{"stop": true}` | stop the server |

Bytes (`body`, `payload`, `data`) are `{"text"}`, `{"hex"}`, `{"base64"}`, `{"repeat": {"byte", "count"}}`; an
expectation may also be `{"empty": true}`, `{"length"}` or `{"sha1"}`. A client frame has `opcode` (a name or a
number), `fin` (default true), `rsv`, `mask` (default true), `payload` or `close: {code, reason}`, and to break the
rules on purpose a declared `length`, a `lengthForm` (`"7"`, `"16"`, `"64"`) and `truncate` (send only the first
bytes).

Every step that reads waits at most the driver's receive timeout (5 s, scaled by `--timeout-scale`). Every server
frame the driver reads is checked against [`ws-server-frames`](#ws-server-frames).

## Running a case

The [driver](../../../eng/conformance/README.md) runs this corpus as `desktop-server/1`, one case per vector case, with
its file's `id` as its set and the role `server`. It starts the server with the case's `config` through the adapter
(`server.start`), runs the steps against the port it reports, and stops it ([PROTOCOL.md](../../../eng/conformance/PROTOCOL.md#desktop-server-desktop-server1)):

```sh
bun run conformance rax-server                # every case through runic-server's adapter
bun run conformance rax-server --set server.frames
bun run conformance rax-server --only range
bun run conformance cases                     # cases.json is current
```

runic-server's adapter is [`native/crates/server-conformance`](../../../native/crates/server-conformance), a test tool
on runic-server's Rust API that is never shipped. It lives outside `native/runic-server` so that a corpus change does
not run the native server's workflow (and its macOS jobs). `status/rax-server.json` records its outcomes: all 114
pass.

## Recorded differences

An implementation's `deviation` status names a row of this table as `specs/desktop/server/README.md#<anchor>`, only
for a `should` or `may` case. `bun run test eng/conformance.test.mjs` checks both. No implementation has one yet; the
C++ server (R7 C1) adds its rows with its status file.

| Implementation | Case | Difference | Why |
| --- | --- | --- | --- |

## Changing the vectors

Edit the JSON, run `bun run conformance cases --write`, and give every changed case a status in every file of
`status/` (`bun run conformance <implementation> --write-status` records a run). A new case checks one rule at one
level and names it.
