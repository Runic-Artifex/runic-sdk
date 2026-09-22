# Archive schema 2 decision

> Historical and superseded decision. The schema-2 candidates and the
> compatibility-report/migration APIs described here are not part of the current
> AssetArchive surface; those APIs were removed when the asset contract was
> reconciled. Keep this file as design history, not as a current roadmap or
> release requirement. The active contract is [archive schema 1](archive-v1.md).

At the time of this decision, schema 1 was the v0.2 writer and reader contract.
It provided the durable archive obligations that were representable without a
second asset model: ordinal entry order, fixed ZIP timestamps, content SHA-256
identities, strong entity tags, cache mode, byte-for-byte reproducible writes,
bounded reads, deterministic inspection, an explicit compatibility report, and
a validated byte-preserving migration command. The report and migration APIs
are historical facts; they are not shipped by the current AssetArchive surface.

At that time, the schema-2 candidates did not justify a serialized break:

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

A future schema-2 proposal would require a real canonical producer and consumer
for a new asset fact, an explicit older-reader compatibility decision, and a
newly verified migration plan. That is design history, not current release work.
