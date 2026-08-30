# Archive schema 2 decision

Schema 1 remains the writer and reader contract for v0.2.  It already provides
the durable archive obligations that are representable without a second asset
model: ordinal entry order, fixed ZIP timestamps, content SHA-256 identities,
strong entity tags, cache mode, byte-for-byte reproducible writes, bounded
reads, deterministic inspection, an explicit compatibility report, and a
validated byte-preserving migration command.

The schema-2 candidates do not yet justify a serialized break:

- Content-addressed identity already exists in each descriptor's SHA-256 and
  `SubresourceIntegrity` is derived from that authority.
- Precompressed variants need a variant-selection contract, `Vary` semantics,
  and an actual producer. None exists yet, so serializing speculative variants
  would create dead metadata.
- CSP describes an application's execution/origin policy, not an asset. Hosts
  own it; the archive must not fabricate allowed origins or script policy.
- Conditional requests and byte ranges are transport behavior. The ASP.NET Core
  adapter consumes the schema-1 entity tag and length without changing the
  archive, while the current CS-WebUI callback has no request-header or finite
  response-stream primitive to implement them faithfully.

Schema 2 may be proposed only with a real canonical producer and consumer for a
new asset fact. Before it becomes the default writer, retain the current
inspector, compatibility report, and executable migration path, and add an
older-reader compatibility decision to this specification.
