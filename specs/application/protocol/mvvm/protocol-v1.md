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

All envelope and payload property names are the camelCase spellings in the
schemas. Unknown properties and unknown `kind` values are invalid. A receiver
MUST validate the applicable direction schema before dispatching consumer
code. If a request UUID can be recovered safely, a schema-invalid request
produces `request.invalid`; otherwise the receiver closes or ignores the frame
without returning attacker-controlled content.

Identifiers have these invariants:

- `contract` is a non-empty, control-free UTF-8 string of at most 128 encoded
  bytes. Comparison is ordinal and case-sensitive. Implementations MUST NOT
  case-fold, culture-transform, or Unicode-normalize it.
- `session`, `view`, and `request` are non-nil RFC 4122 UUIDs serialized as 36
  lowercase ASCII characters with hyphens. UUID comparison is by its 128-bit
  value. Uppercase or alternate textual forms are invalid.
- A request UUID is unique for the lifetime of its session. The host retains
  completed request UUIDs until the session tombstone is released and rejects
  a replay as `request.invalid` without invoking consumer code.
- `member` is an integer from 1 through 2,147,483,647. It is scoped by the
  opened contract and never derived from a display name.
- A revision is a non-negative JSON integer from 0 through
  9,223,372,036,854,775,807. Parsers MUST preserve the exact signed 64-bit
  value and MUST NOT round through binary64. JavaScript implementations
  therefore require a lossless integer decoder before converting a revision to
  `bigint`; ordinary `JSON.parse` numbers are insufficient above
  9,007,199,254,740,991.
- Encoding, number parsing, ordering, and comparison are culture-invariant.

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
If consumer code changes projected state and then throws, times out, or observes
cancellation, the host MUST publish one patch, advance exactly once, and then
send the winning sanitized fault at the new revision. It MUST NOT leave changed
state at the old revision. Changes occurring outside a request are serialized
as their own one-step patch transactions.

A snapshot is authoritative and replaces all prior local members, command
state, collections, and validation. Reconnect uses a new handshake followed by
`requestSnapshot`; v1 does not require patch replay. `ack` is advisory
backpressure information, never mutates state, and cannot acknowledge beyond
the host revision. A successful `ack`, `cancel`, or repeated `close` does not
advance revision.

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

The first terminal condition wins:

1. Completion committed before cancellation or deadline remains successful;
   later cancellation returns `accepted: false`.
2. Cancellation observed before the deadline produces
   `request.cancelled`; the cancel result has `accepted: true`.
3. Reaching the deadline before cancellation produces `request.timeout`;
   later cancellation has `accepted: false`.
4. A consumer exception wins only if completion, cancellation, and timeout have
   not already won. Because v1 has no general exception fault code, it is
   exposed as `request.invalid` with a generic sanitized message.

Cancellation is cooperative. If work ignores its token, the host still emits
only the winning terminal fault and discards a late return value. Any projected
state observed before that terminal condition follows the committed-change rule
in section 4.

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

Fault messages MUST be non-empty, control-free UTF-8 of at most 256 bytes.
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
| Members per snapshot | 4,096 | 4,096 |
| Changes per patch | 1,024 | 1,024 |
| Items per projected collection | 10,000 | 10,000 |
| Inserted/replaced items per patch | 10,000 total | 10,000 total |
| Command timeout | 300,000 ms hard maximum | 30,000 ms |

Schema `maxLength` is only an early character-count rejection. Implementations
MUST additionally enforce every UTF-8 byte limit. Counts are checked before
allocation where possible. The pending-request count includes queued and
executing requests but excludes completed request tombstones.

## 8. Snapshots, patches, and deterministic encoding

Snapshot member arrays are sorted by ascending numeric member ID and contain no
duplicate `(type, member)` pair. Patch changes preserve transaction order;
coalescing is allowed only when the resulting change sequence is observably
equivalent. Collection indices refer to the collection state produced by all
earlier changes in the same patch. A reset uses index zero and the complete new
collection. Collection moves use the pre-move `from`, post-removal insertion
`to`, and positive `count`.

Canonical corpus output uses the schema property order, lowercase UUIDs,
shortest valid base-10 JSON number spelling, no insignificant whitespace, and a
single trailing newline when stored as a file. Wire receivers MUST NOT depend
on property order or whitespace.

## 9. Compatibility and conformance

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
