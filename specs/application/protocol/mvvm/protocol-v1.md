# WebUIToolkit MVVM protocol v1

## 1. Status and identity

This document defines the normative wire and session behavior for registered
protocol identity `webuitoolkit.mvvm/1`. The envelope field `v` is the JSON
integer `1`. Every requirement using **MUST**, **MUST NOT**, **SHOULD**, or
**MAY** is normative.

The protocol is a closed MVVM binding protocol, not a remote object system.
Only registered numeric member identifiers cross the wire. CLR and TypeScript
type names, assembly names, arbitrary method names, filesystem paths, stack
traces, and exception representations MUST NOT cross the boundary.

## 2. Framing and primitive invariants

One transport frame contains exactly one UTF-8 JSON document. A receiver MUST
reject an invalid UTF-8 sequence, byte-order mark, duplicate object key,
trailing non-whitespace data, comment, trailing comma, non-finite number, or a
document deeper or larger than the negotiated limits. JSON object order is not
semantic.

Framing validation is performed on the received bytes, before construction of
wire model objects or invocation of registration, binding, or consumer code.
The UTF-8 decoder MUST use strict error handling: it MUST NOT replace malformed
bytes, a lone surrogate escape, or an invalid surrogate pair with U+FFFD.
Duplicate detection compares the decoded property-name scalar sequence, so
`"kind"` and `"\u006bind"` are the same key. Limits apply to the complete
decoded JSON tree, including application values nested below `value`,
`argument`, `items`, and command results.

All envelope and payload property names are the camelCase spellings in the
schemas. Unknown properties and unknown `kind` values are invalid. A receiver
MUST validate the applicable direction schema before dispatching consumer
code. If a request UUID can be recovered safely, a schema-invalid request
produces `request.invalid`; otherwise the receiver closes or ignores the frame
without returning attacker-controlled content.

Identifiers have these invariants:

- `contract` is a non-empty, C0/C1-control-free UTF-8 string of at most 128 encoded
  bytes. Comparison is ordinal and case-sensitive. Implementations MUST NOT
  case-fold, culture-transform, or Unicode-normalize it.
- `session`, `view`, and `request` are non-nil RFC 4122 UUIDs serialized as 36
  lowercase ASCII characters with hyphens. UUID comparison is by its 128-bit
  value. Uppercase or alternate textual forms are invalid.
- A request UUID is unique for the lifetime of its session. The host retains
  every admitted request UUID and terminal classification until the session
  tombstone is released. While the session is active it rejects a replay as
  `request.invalid` without invoking consumer code; after closure, the close
  rules in section 4 take precedence. It MUST NOT evict individual entries from
  a live session; the finite session request budget in section 7 bounds this
  state.
- `member` is an integer from 1 through 2,147,483,647. It is scoped by the
  opened contract and never derived from a display name.
- A revision is a non-negative JSON integer from 0 through
  9,223,372,036,854,775,807. Parsers MUST preserve the exact signed 64-bit
  value and MUST NOT round through binary64. JavaScript implementations
  therefore require a lossless integer decoder before converting a revision to
  `bigint`; ordinary `JSON.parse` numbers are insufficient above
  9,007,199,254,740,991.
- Encoding, number parsing, ordering, and comparison are culture-invariant.

A conforming encoder emits valid UTF-8 without a BOM and never emits an
unpaired surrogate or non-finite number. For a given wire model and projected
JSON value it MUST emit the same bytes regardless of process culture, current
thread culture, operating system, hash-map iteration order, or prior requests.
It emits envelope and protocol payload properties in schema declaration order,
snapshot arrays in the order defined in section 8, and capability arrays in
ordinal order. Application JSON object properties are emitted in ordinal name
order recursively; arrays retain their application order. Integer tokens use
the shortest base-10 spelling with no leading zero, plus sign, or negative
zero. Other numbers use the shortest finite round-trippable invariant spelling.
Escaping choices MUST be stable within an implementation; receivers MUST NOT
depend on an emitter's escaping choice.

`capability` is a per-session bearer secret containing exactly 32 random bytes,
encoded as 43 unpadded base64url ASCII characters. `opened` is the only host
message that exposes it. Every later client message for that session MUST carry
it. Host messages, faults, logs, metrics, and traces MUST NOT echo it. Token
comparison MUST be constant-time. Corpus tokens are fixed non-secret test data.

## 3. Envelope and closed message kinds

Every message contains `v`, `kind`, and a typed `payload`. Other top-level
fields are present only where the following table and schemas allow them.

### 3.1 Client to host

| Kind | Required envelope fields | Payload | Effect |
|---|---|---|---|
| `handshake` | `request` | `supportedVersions`, `capabilities` | Offers v1 and optional capabilities. |
| `open` | `contract`, `view`, `request` | empty | Opens one registered contract and creates one session. |
| `setProperty` | `session`, `view`, `request`, `baseRevision`, `capability` | `member`, `value` | Performs one generated setter mutation. |
| `execute` | `session`, `view`, `request`, `baseRevision`, `capability` | `member`, optional `argument` | Executes one generated command mutation. Absence and JSON `null` are distinct. |
| `cancel` | `session`, `view`, `request`, `capability` | `targetRequest` | Signals cancellation for one pending request. |
| `ack` | `session`, `view`, `request`, `capability` | `revision` | Confirms the highest contiguous applied revision. |
| `requestSnapshot` | `session`, `view`, `request`, `capability` | empty | Requests authoritative current state. |
| `close` | `session`, `view`, `request`, `capability` | optional sanitized `reason` | Idempotently tears down the session. |

### 3.2 Host to client

| Kind | Required envelope fields | Payload | Effect |
|---|---|---|---|
| `handshakeResult` | `request` | `selectedVersion`, `capabilities`, `limits` | Selects v1, the capability intersection, and effective limits. |
| `opened` | `contract`, `session`, `view`, `request`, `capability` | `snapshot` | Atomically returns the new session, token, and revision-zero state. |
| `result` | `session`, `view`, `request` | operation-specific result and authoritative `revision` | Terminates `setProperty`, `execute`, `cancel`, or `ack`. |
| `snapshot` | `session`, `view`, `request` | `revision`, `members` | Replaces all client state for the session. |
| `patch` | `session`, `view` | `fromRevision`, `toRevision`, `changes` | Applies one atomic consecutive revision transition. |
| `fault` | `request`, plus `session` and `view` when known | stable code, sanitized message and bounded recovery fields | Terminates a failed request. |
| `closed` | `session`, `view`, `request` | final `revision`, sanitized `reason` | Confirms close and session teardown. |

The allowed capability names in v1 are `cancellation`, `collections`,
`commandResults`, `patches`, and `validation`. Both peers sort capability output
by ordinal name for deterministic serialization. A host returns only the
intersection it implements. A client MUST NOT rely on an unselected capability.
A new capability name requires a revised schema and corpus; a breaking message
change requires a new protocol major and schema directory.

Capabilities gate vocabulary as follows. `cancel` is accepted only when
`cancellation` was selected. Collection snapshot members and collection patch
changes require `collections`; command result `value` requires
`commandResults`; `patch` messages require `patches`; and validation snapshot
members and changes require `validation`. Command snapshot members and command
changes themselves do not require `commandResults`. If `patches` is not
selected, mutations still advance revision and their terminal result exposes
the new revision; the client recovers changed projection with
`requestSnapshot`. Using unselected vocabulary is `request.invalid` in the
client direction and a protocol violation in the host direction.

## 4. Session and revision rules

An `open` allocates one session and returns an `opened` snapshot at revision 0.
The session identity, view identity, contract, and capability are immutable.
Messages with mismatched identity or capability are rejected without revealing
which field failed. Sessions are isolated. Host execution is serialized within
a session; unrelated sessions MAY progress concurrently.

`setProperty` and `execute` are mutation requests. Their `baseRevision` MUST
equal the authoritative revision at admission. A mismatch produces
`revision.stale`, with `currentRevision` and `snapshotRequired: true`, before
consumer invocation. A rejected request never advances revision.

Each admitted, successfully committed mutation advances the revision exactly
once from N to N+1, even if it changes multiple members. At most one `patch`
describes that atomic transition, and the correlated terminal `result` follows
the patch. A successful mutation that changes no projected member still
advances once and returns a result; it need not emit an empty patch. A patch
MUST have `toRevision == fromRevision + 1`. Clients apply a patch only when its
`fromRevision` equals local revision; a duplicate is ignored only if its entire
content is byte-equivalent to the already applied transition. A gap or conflict
requires `requestSnapshot` and no speculative patch application.

Observable changes caused during a mutation are committed as one transaction.
A conforming binding adapter MUST stage them until mutation commit and
publication of its completed binding result (`MvvmBindingResult` in the .NET
runtime) can occur as one logical atomic act. The completed result contains the
complete projection change set and either success or a declared committed
failure. Once the runtime
observes that completed result, the commit wins: it advances exactly once,
publishes its patch when non-empty and selected, and then publishes the result
or sanitized committed fault at the new revision.

If cancellation, deadline, or an uncommitted consumer exception wins before
that atomic act, the adapter MUST discard or roll back staged changes and MUST
NOT subsequently mutate or commit them. The winning fault remains at the old
revision. Directly mutating projected state before completed-result publication,
or committing after another terminal condition won, violates the adapter
contract. The runtime discards the late result and quarantines the session as
described in section 5; it MUST NOT legitimize the violation by emitting a late
patch or advancing revision. Changes genuinely originating outside a request
are serialized as their own one-step patch transactions and are not a mechanism
for relabeling a violating late mutation.

A snapshot is authoritative and replaces all prior local members, command
state, collections, and validation. On a replacement, omitted members are
removed; clients MUST NOT merge the snapshot into old local state.

After transport loss, reconnect uses a new handshake and then an authenticated
`requestSnapshot` for the retained session. The session, view, and capability
are the original values; `requestSnapshot.request` is new and unique. A host
MAY expire a disconnected session according to a documented bounded retention
policy, in which case the request returns `session.closed` and the client must
`open` a new session. V1 does not require patch replay and a client MUST NOT
resume by assuming that its last locally applied revision is authoritative.

`ack` is advisory backpressure information and never mutates projected state.
It reports the highest contiguous revision the client has fully applied. An
acknowledgement greater than the host revision is `request.invalid` and does
not change acknowledgement state. An acknowledgement at or below the greatest
previously accepted value is a successful idempotent no-op. Otherwise the host
advances the acknowledged value monotonically. A successful `ack`, `cancel`,
or repeated `close` does not advance revision.

`close` is idempotent. The first accepted close stops admission, cancels pending
work, disposes session resources, and returns `closed`. While the session
tombstone is retained, an authenticated replay of that close returns the same
terminal state; other requests return `session.closed`. After tombstone expiry,
the host MUST still avoid disclosing whether a supplied session or capability
was ever valid.

## 5. Cancellation, timeout, and terminal outcomes

Each request has exactly one terminal `result`, `fault`, `snapshot`, `opened`,
or `closed` outcome. Transport receipt of `cancel` MAY signal a pending
cancellation source outside the consumer-dispatch queue, but it MUST NOT invoke
consumer code concurrently. Terminal publication remains session-serialized.

A `cancel` is itself authenticated, deduplicated, admitted, and counted against
both the pending and lifetime request bounds. Target lookup and cancellation
signalling MUST use bounded state and MUST NOT create an unbounded task,
callback, or queue entry per repeated cancel. The host makes the target winner
decision without awaiting target consumer completion. If cancellation wins, it
publishes the target's `request.cancelled` fault and then the cancel request's
`result`; `accepted` is `true`. Otherwise the cancel result has `accepted:
false`. Its `revision` is the authoritative revision at that serialized winner
decision. An atomic target commit would already have won instead; the cancel
itself never advances revision.

The first terminal condition wins:

1. An atomic commit and completed binding result observed before cancellation
   or deadline wins and is published as that result's success or committed
   fault; later cancellation returns `accepted: false`.
2. Cancellation observed before the deadline produces
   `request.cancelled`; the cancel result has `accepted: true`.
3. Reaching the deadline before cancellation produces `request.timeout`;
   later cancellation has `accepted: false`.
4. A consumer exception wins only if completion, cancellation, and timeout have
   not already won. Because v1 has no general exception fault code, it is
   exposed as `request.invalid` with a generic sanitized message.

Cancellation is cooperative. If work ignores its token, the host still emits
only the winning terminal fault and discards a late return value. Cancellation
or deadline winning before completed-result publication means there is no
conforming target commit and no target revision advance, regardless of work the
consumer performed privately or attempted to return later.

When cancellation or deadline wins while the consumer operation has not
returned, the host marks the session unusable before publishing the winning
fault. It admits no further consumer work, transitions the session to closed
teardown, and does not await the late operation for target terminal publication
or progress by other sessions. A late return, exception, patch, or attempted
commit is observed for resource cleanup and recorded only as a sanitized
consumer contract violation; it produces no wire message, revision change, or
session revival. Owned resources that cannot safely be released while the
operation is running are retained only by that closed session and released when
the operation returns; this retention is bounded by the host's session and
pending-operation limits. The client opens a new session after
`session.closed`.

## 6. Stable fault catalog

The v1 `code` value is exactly one of the following. Text is diagnostic only;
clients branch on the code and recovery fields.

| Code | Meaning and required behavior | Retry |
|---|---|---|
| `protocol.unsupported` | No offered protocol version is supported. Pre-session only. | Only after choosing a supported version. |
| `request.invalid` | Malformed, duplicate, unauthorized, unknown-contract, unavailable-member-operation, or consumer-failed request. No sensitive distinction is disclosed. | Only after correcting the request. |
| `member.unknown` | Numeric member is not in the opened contract or is the wrong registered member kind. | No. |
| `revision.stale` | `baseRevision` differs from current revision. `currentRevision` and `snapshotRequired: true` are required. | After snapshot recovery. |
| `limit.exceeded` | A hard or advertised effective limit was exceeded. | Only with reduced input/load. |
| `request.cancelled` | Cancellation won the target request. | No. |
| `request.timeout` | The negotiated command deadline won. | Yes, when retry is safe for the application. |
| `session.closed` | The session is closing, closed, expired, or otherwise unavailable. | Open a new session. |

`retryable` is exactly `true` for `revision.stale`, `limit.exceeded`, and
`request.timeout`, and exactly `false` for every other v1 code. Only
`revision.stale` carries `currentRevision` and `snapshotRequired`; there
`snapshotRequired` is exactly `true`. Those recovery fields are absent for all
other codes. `protocol.unsupported` is pre-session only. Pre-session faults are
limited to `protocol.unsupported`, `request.invalid`, and `limit.exceeded`.

Fault messages MUST be non-empty, C0/C1-control-free UTF-8 of at most 256 bytes.
They MUST be selected from bounded implementation-owned templates and MUST NOT
contain exception type names, stack traces, source locations, paths, secrets,
payload values, arbitrary consumer text, or serialized exception data. A host
MUST sanitize before serialization and before logging.

## 7. Exact limits

The following are v1 hard ceilings. A host MAY configure lower effective values
and advertises them in `handshakeResult`; it MUST NOT advertise or accept a
higher value. Limit accounting uses encoded UTF-8 bytes and parsed JSON values,
not UTF-16 code units. Exceeding any applicable limit produces
`limit.exceeded` when a safe correlated response is possible.

| Resource | V1 hard ceiling | Default effective value |
|---|---:|---:|
| Encoded frame | 1,048,576 bytes | 1,048,576 bytes |
| JSON nesting depth | 32 | 32 |
| General string | 65,536 UTF-8 bytes | 65,536 UTF-8 bytes |
| JSON object property name | 128 UTF-8 bytes | 128 UTF-8 bytes |
| Properties per JSON object | 4,096 | 4,096 |
| Items per general JSON array | 10,000 | 10,000 |
| Contract identifier | 128 UTF-8 bytes | 128 UTF-8 bytes |
| Capability token | exactly 43 ASCII characters | exactly 43 |
| Capability names offered | 5 unique names | 5 |
| Sanitized message | 256 UTF-8 bytes | 256 UTF-8 bytes |
| Concurrent sessions per host | 16 | 16 |
| Pending requests per session | 64 | 64 |
| Distinct admitted requests per session lifetime | 65,536 | 65,536 |
| Members per snapshot | 4,096 | 4,096 |
| Changes per patch | 1,024 | 1,024 |
| Items per projected collection | 10,000 | 10,000 |
| Inserted/replaced items per patch | 10,000 total | 10,000 total |
| Command timeout | 300,000 ms hard maximum | 30,000 ms |

Schema `maxLength` is only an early character-count rejection. Implementations
MUST additionally enforce every UTF-8 byte limit. Counts are checked before
allocation where possible. The pending-request count includes queued and
executing requests but excludes completed request tombstones.

The lifetime request budget counts `open` as the first request and every
distinct admitted session-bound request thereafter, including control requests.
It is a fixed v1 lifecycle bound and is not a negotiated handshake limit. On
completion of request 65,536, the host stops admission, retains all UUID
tombstones, and moves the session to closed teardown; a later request observes
`session.closed` without consumer dispatch and the client opens a new session.
A request rejected before admission does not consume the budget. Hosts MUST NOT
use tombstone eviction to extend the budget.

### 7.1 Bounded admission and output

Every per-host and per-session work queue MUST have a finite configured bound;
an unbounded channel, task list, patch history, or completed-request cache is
not conforming. The admission bound is `maxPendingRequests`. Admission is an
atomic reservation made before queuing, and the reservation is held until the
request's terminal response is handed to the bounded transport writer. When no
reservation is available, the request is not queued or dispatched and receives
`limit.exceeded` if a correlated response can be safely reserved.

The transport writer MUST also be bounded and MUST reserve sufficient capacity
for the terminal response of every admitted request. A mutation can produce at
most one patch plus one terminal response. Implementations MAY stop reading or
pause admission when writer capacity is exhausted. They MUST NOT drop, reorder,
or replace an admitted request's terminal response. If the transport cannot
make progress within the host's documented bounded write timeout, the host
closes that transport and retains or expires the session under the reconnect
rule in section 4.

Retained patch history is optional and bounded. The host tracks acknowledgement
monotonically but MUST NOT retain a patch merely because it is unacknowledged.
When an implementation's unacknowledged-history bound is reached, it stops
replay retention and/or live patch publication; it continues publishing
terminal revisions. A client that observes a terminal revision ahead of its
local revision, a patch gap, or a conflicting transition requests a snapshot.
Ack therefore permits reclamation but never promises replay.

## 8. Snapshots, patches, and deterministic encoding

The complete v1 binding vocabulary is `property`, `collection`, `command`, and
`validation` snapshot members, plus those four patch change types and
`collectionMove`. A numeric member is registered with exactly one principal
kind: property, collection, or command. A validation entry uses the member ID
of the property or collection it describes; an empty `errors` array explicitly
clears validation for that member. Values and collection items are closed JSON
trees, never encoded framework objects or type metadata.

Snapshot member arrays contain exactly one entry for every currently projected
principal member and, when selected, exactly one validation entry for each
member having a validation projection (including an empty error list). They are
sorted first by ascending numeric member ID and then by type in the fixed order
`property`, `collection`, `command`, `validation`; no `(type, member)` pair may
repeat. Each member ID and change MUST match its registered kind. A malformed
adapter projection is an implementation error and MUST be rejected before it
reaches the wire.

Patch changes preserve transaction order;
coalescing is allowed only when the resulting change sequence is observably
equivalent. Collection indices refer to the collection state produced by all
earlier changes in the same patch. A reset uses index zero and the complete new
collection. Collection moves use the pre-move `from`, post-removal insertion
`to`, and positive `count`.

For `insert`, `items` is non-empty and is inserted immediately before `index`,
where `index` may equal the pre-change count. For `remove`, `items` is non-empty
and is the exact sequence removed beginning at `index`; a client MUST treat a
mismatch as a conflict. For `replace`, `items` is the non-empty replacement
sequence beginning at `index`; replacement removes the same number of existing
items. For `reset`, `items` is the complete replacement and may be empty.
Indices and counts MUST be in range at the instant the change is applied, and
the collection MUST remain within its negotiated bound after every change.
Command and validation changes replace the complete projected state for that
member. Property changes replace the complete property value.

Canonical corpus output uses the schema property order, lowercase UUIDs,
shortest valid base-10 JSON number spelling, no insignificant whitespace, and a
single trailing newline when stored as a file. Wire receivers MUST NOT depend
on property order or whitespace.

## 9. Observability and security boundaries

Observability is an implementation surface, not an extension of the wire
schema. Implementations SHOULD expose BCL-friendly tracing, metrics, and logs
(for example .NET `ActivitySource` and `Meter`) under a stable library-owned
name. At minimum operators SHOULD be able to observe active sessions, admitted
and rejected requests, pending work, request duration and outcome code, emitted
snapshots and patches, and admission/writer pressure. Instrument creation and
the disabled-listener path MUST be allocation-conscious and MUST NOT change
protocol ordering or outcomes.

Telemetry MUST NOT contain capability tokens, application JSON, contract values,
view/session/request identifiers, member values, exception text, or close
reasons by default. Suitable low-cardinality dimensions are protocol version,
message kind, operation kind, terminal fault code, and coarse queue outcome.
Contract or member identity MAY be exposed only through an explicit
application-provided redaction/classification hook; raw values remain forbidden.
Listener, exporter, and logging failures MUST NOT fail a request or session.

Authentication is performed before request replay lookup, revision disclosure,
member lookup, or consumer dispatch. Unknown session, wrong view, and wrong
capability failures use the same public fault shape and MUST NOT reveal which
comparison failed. Implementations SHOULD make those rejection paths
indistinguishable in observable timing within practical limits. Request UUID
tombstones and disconnected-session state are bounded and expire according to
documented host policy. Secret buffers are not pooled or logged and are cleared
when their lifetime ends where the platform permits. Corpus credentials are
test vectors only and MUST NOT be used in production.

## 10. Compatibility and conformance

Wire version 1 accepts only the message kinds, properties, capabilities, and
fault codes in these schemas. Optional behavior is used only after capability
intersection. A breaking discriminator, field, identity, revision, or ordering
change requires `schema/v2`, `corpus/v2`, and a new registered protocol major.

Conformance requires:

1. every `valid` manifest document validates against its direction schema;
2. every `invalid` document fails for the recorded reason;
3. semantic traces produce the recorded invocation count, revision count,
   message order, and exact terminal code;
4. byte, depth, count, culture, duplicate-key, and sanitization checks are run
   outside JSON Schema; and
5. test results are identical under different process cultures.

In addition, production conformance exercises malformed UTF-8 and surrogate
escapes, decoded duplicate names, deep and wide JSON, acknowledgement replay and
future acknowledgement rejection, slow/non-reading transports, reconnect after
patch loss, request UUID replay, wrong-capability probes, and telemetry redaction.
