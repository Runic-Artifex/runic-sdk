# Presentation host contract

The key words **must**, **must not**, **should**, and **may** describe normative
requirements for `runic.desktop.presentation/1`.

## Scope

Runic Desktop owns browser and embedded-WebView presentation hosting, local UI
transport, connected presentation sessions, request-scoped content delivery,
and platform-window adapters. It carries application-owned payloads without
interpreting their domain meaning.

Runic Desktop does not define application commands, domain events, revisions,
asset manifests, localization, build or HMR behavior, framework state, or
WebUI API compatibility. Those boundaries are fixed in the
[ownership map](ownership.md).

## Vocabulary and ownership graph

```text
host
├── listener (one or more)
└── surface (zero or more)
    ├── window (zero or more)
    ├── presentation session (zero or more)
    │   └── invocation (zero or more)
    └── request (zero or more)
```

- A **host** owns listener resources, immutable default policy, surfaces, and
  terminal shutdown.
- A **listener** accepts network requests. A listener may serve multiple
  isolated surfaces.
- A **surface** is an isolated presentation namespace containing content
  routes, low-level capabilities, sessions, and optional windows.
- A **window** is one installed-browser or embedded-WebView presentation of a
  surface. A server-only surface has no window.
- A **presentation session** is one authenticated, ordered, bidirectional
  transport connection to one surface. A stable client identity, when enabled,
  is not a session identity.
- A **request** is one inbound content request. It ends at response completion,
  requester disconnect, rejection, failure, or enclosing-scope shutdown.
- An **invocation** is one correlated low-level presentation operation over a
  session and has exactly one terminal outcome.
- A **frame** is one logical transport unit after reassembly and before
  profile-specific encoding.
- A **stream** is an ordered response body whose producer observes
  backpressure and cancellation.
- A **capability** is a named, bounded presentation operation advertised or
  admitted for a session.
- A **policy** is immutable configuration evaluated at an explicit scope. It
  is not mutable process-global state.

Every resource has an implementation-independent identifier in diagnostics and
conformance scenarios. Identifiers are opaque and need not share a representation
across languages.

## Isolation

- A host must support more than one surface without requiring one listener per
  surface.
- Routes, credentials, sessions, capabilities, content, and window operations
  of one surface must not be observable through another surface unless an
  explicit application-owned integration connects them.
- An isolated-listener option may exist for policy or platform requirements.
  Per-window listeners must not be the accidental default architecture.
- Closing one surface on a shared listener must not interrupt another surface.

## Lifecycle

Host, surface, window, session, request, and invocation creation and terminal
operations are asynchronous in the semantic model. A language may provide a
synchronous convenience only when it preserves the same ordering,
cancellation, and UI-thread safety.

Starting an already-running resource with the same immutable configuration may
be idempotent. Starting it with different immutable configuration must fail
with `conflict`; implementations must not silently mutate a live listener or
security boundary.

Closing a surface must:

1. prevent new surface requests, sessions, windows, and invocations;
2. signal `surfaceClosing` cancellation to active requests and invocations;
3. close its sessions and request its windows to close;
4. detach its routes and release surface-owned resources; and
5. reach `closed` exactly once without stopping a shared listener used by
   another surface.

Closing a host must:

1. prevent new listener admission and resource creation;
2. close every surface according to the surface sequence;
3. stop and release listener and host-owned resources; and
4. reach `closed` exactly once.

Natural browser, WebView, transport, and application shutdown must converge on
the same scoped close operations. Disposing or closing from inside an active
callback must not wait on that callback in a cycle.

## Windows and platform capabilities

A surface may be opened in an installed browser, an embedded WebView, or no
window. Window capabilities such as native handles, focus, framing, geometry,
visibility, minimization, maximization, and drag regions are explicitly
reported. Unsupported capabilities return `unavailable`; they must not appear
to succeed.

Closing a window closes only resources owned by that presentation. It does not
implicitly terminate its surface when other windows, sessions, or a server-only
owner keep the surface alive. The application or host adapter decides whether
the last window closing should close the surface.

## Requests and streaming responses

Content resolution produces response metadata and exactly one body mode:
empty, fixed bytes, or stream. Asset identity, archive lookup, cache policy,
localization, and development content remain external product inputs.

- Status and headers become immutable when the response is committed.
- A `HEAD` request emits the same applicable metadata as `GET` and no body.
- Stream chunks preserve producer order. Transport packet boundaries are not
  semantic chunk boundaries.
- A producer must not be asked for another chunk until the consumer has
  accepted the previous chunk or granted bounded capacity.
- Requester disconnect, caller cancellation, deadline, surface close, and host
  close cancel the producer with distinct reasons.
- After commitment, a stream failure terminates the body and is reported
  diagnostically; it must not replace the committed status with a second
  response.
- A response body and its resources are released exactly once after success,
  failure, or cancellation.

## Sessions, frames, and invocations

Authentication and origin admission occur before a session may use a
capability. Each direction preserves frame order. Implementations serialize
writes or provide equivalent ordering; correlated invocations may progress
concurrently without interleaving the bytes of logical frames.

A frame reaches a handler only after transport reassembly, authentication,
profile validation, and configured size-limit validation. Malformed,
unauthenticated, or oversized input must have a defined reject or close outcome
and must never invoke a capability.

Correlation identifiers are opaque. Every accepted invocation ends exactly
once as `succeeded`, `failed`, `cancelled`, `timedOut`, or `unavailable`. A late
terminal frame is ignored and recorded diagnostically.

An Application Bridge frame may be the opaque payload of a Desktop frame.
Desktop must not inspect or regenerate its command, event, schema, revision, or
controller semantics.

Transport loss ends the presentation session and completes every pending
invocation exactly once as `transportClosed`. Reconnection creates a new
session identifier and repeats origin, credential, and capability admission.
There is no implicit invocation resumption or ordering guarantee across
sessions. A stable client identity may correlate diagnostics, but it grants no
authority and does not turn the new connection into the old session. Higher
layers such as Application Bridge may resynchronize their own state after the
new session is authenticated.

The concrete wire profile is negotiated or selected by configuration. The
current 8-byte WebUI packet is the `webui-compat/52f9e75` profile. Whether M7
retains that profile or introduces a Runic-owned profile is a separate,
versioned decision; the lifecycle and error requirements above apply to both
where the profile can express them.

## Payloads and serialization

Opaque binary payloads carry an explicit content type. A Desktop-owned
structured envelope also carries a profile-owned schema identity and is encoded
as UTF-8 JSON unless its profile declares another versioned encoding.

Desktop validates only its own transport envelope and presentation-capability
schemas. Application Bridge and other owner-defined payloads remain opaque;
their schema identities and contents are validated only by the owning product.
Desktop-owned structured decoders must reject invalid UTF-8, duplicate object
keys, unknown required envelope schema identities, and configured depth,
string, collection, or frame limits. They must not infer structured data from
arbitrary bytes. Language implementations may expose richer native values only
through an explicit profile-owned schema mapping.

## Cancellation

Cancellation is a terminal semantic outcome, not a guarantee that a remote
peer received a cancellation message. The observable causes are:

- `callerCancelled`
- `requesterDisconnected`
- `deadlineExceeded`
- `sessionClosed`
- `surfaceClosing`
- `hostStopping`

The nearest cause already observed wins. An implementation must propagate the
cause to the owned handler or producer and release the resource exactly once.
Application-level cancellation carried inside an opaque Application Bridge
payload remains Application-owned.

## Security policy

The safe default policy is loopback-only binding, same-origin admission,
required session credentials with at least 128 bits of cryptographically secure
random entropy, deny-by-default capabilities, single-client admission,
scoped-root content access, redacted failures, and no cache for
credential-bearing bootstrap content.

Non-loopback binding, additional origins, anonymous sessions, multiple clients,
or relaxed content access require explicit policy. Loopback location alone
must not be treated as authentication. Credentials must not appear in URLs,
public errors, or retained diagnostics.

A credential is scoped to one surface and one host lifetime. It must not admit
a session on another surface, remains invalid after its surface or host closes,
and is replaced when policy requests rotation. Rotation prevents new use of the
old credential and closes sessions admitted by it before the rotation completes.
Credentials are not persisted by default.

Browser origins are compared as canonical `(scheme, ASCII host, effective
port)` tuples; paths, queries, fragments, and user information are not origin
components. A browser handshake with a missing, invalid, or unlisted origin is
rejected unless explicit non-browser policy applies. Credential, origin, and
capability rejection closes or refuses the attempted session/invocation before
an application handler runs and returns the corresponding stable error.

Path resolution must reject traversal, encoded traversal, and resolved targets
outside the configured root. Host, header, request, frame, message, collection,
and stream-buffer limits are explicit policy and fail closed.

## Errors and diagnostics

Public errors have a stable category, stable code, safe message, and
retryability. The contract categories are:

- `invalidArgument`
- `invalidState`
- `invalidFrame`
- `limitExceeded`
- `authenticationDenied`
- `originDenied`
- `capabilityDenied`
- `notFound`
- `conflict`
- `cancelled`
- `timedOut`
- `transportClosed`
- `hostStopping`
- `unavailable`
- `operationFailed`

Language exceptions, stack traces, platform error text, credentials, and
payload contents are not public error fields. They may appear only in
policy-controlled diagnostics. Compatibility profiles may collapse an error
when required by their wire contract, but evidence must classify that as an
intentional compatibility limitation.
