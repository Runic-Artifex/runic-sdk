# M7 wire-profile decision

Runic Desktop v1 retains `webui-compat/52f9e75` as its internal browser wire
codec. The decision is deliberately about framing, not product identity or API
ownership.

No v1 requirement justifies a second packet format. The retained profile
already provides an authenticated binary WebSocket, correlation identifiers,
large-frame reassembly, capability discovery, opaque byte arguments, and
session-scoped host-to-browser delivery. Replacing it for naming alone would
add two codecs and a migration problem without improving security,
cancellation, error classification, or capability semantics.

The retained behavior is isolated as follows:

- `/runic-desktop.js` installs only the immutable `runicDesktop` bootstrap.
  The public TypeScript package and its errors use Runic Desktop identity.
- `@runic-artifex/desktop` owns the internal codec, authenticates the physical
  session, negotiates `runic.desktop.application-bridge/1`, and publishes
  opaque frames through a structural `FrameChannel`.
- Application Bridge owns its envelopes, commands, events, revisions, and
  resynchronization. Runic Desktop neither parses nor duplicates them.
- `/webui.js` and the `webui` global remain an optional compatibility surface.
  New Runic consumers do not load or reference them.

A future profile requires a concrete contract need that cannot be added safely
to this boundary—for example protocol-level multiplexing, negotiated wire
versions, or cancellation that must be observed before Application Bridge
decoding. Such a change must introduce a new versioned profile and retain
explicit negotiation; it must not silently reinterpret this one.
