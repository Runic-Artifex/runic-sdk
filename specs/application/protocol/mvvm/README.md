# WebUIToolkit MVVM wire contract

This directory is the hand-reviewed, language-neutral source of truth for the
`webuitoolkit.mvvm/1` protocol identity. The integer `v` field in every v1
envelope is `1`. WebUIToolkit owns this protocol; lowercase `cs-webui` is the
external transport dependency and is not renamed or specified here.

## Contents

- [`protocol-v1.md`](protocol-v1.md) is the normative behavior, ordering,
  limits, fault, cancellation, and compatibility specification.
- [`schema/v1/common.schema.json`](schema/v1/common.schema.json) contains shared
  JSON Schema 2020-12 definitions.
- [`schema/v1/client-message.schema.json`](schema/v1/client-message.schema.json)
  is the closed client-to-host message union.
- [`schema/v1/host-message.schema.json`](schema/v1/host-message.schema.json) is
  the closed host-to-client message union.
- [`corpus/v1/manifest.json`](corpus/v1/manifest.json) declares valid, invalid,
  and semantic conformance cases with expected outcomes and reasons.
- [`g3/first-party-consumer-matrix.json`](g3/first-party-consumer-matrix.json)
  maps existing corpus cases to the mandatory CommunityToolkit, compiled HTMX,
  Hosting, and Flow consumption evidence. It is not a fixture registry and does
  not change the frozen protocol surface.
- [`g4/core-vertical-matrix.json`](g4/core-vertical-matrix.json) freezes the
  shared framework-neutral SDK, CommunityToolkit, and compiled-HTMX vertical.
- [`g5/framework-adapter-matrix.json`](g5/framework-adapter-matrix.json) maps
  the frozen SDK to mandatory React, Vue, and Svelte browser, lifecycle,
  package-consumer, type, and version evidence.

The schemas intentionally have no `$id`. An owned schema domain has not yet
been approved. Relative references are resolved from the schema files on disk.

## Conformance

A conforming implementation must validate both the schema fixtures and the
semantic traces. Schema validation alone cannot enforce encoded UTF-8 byte
limits, duplicate-key rejection, revision transitions, ordering, cancellation
races, capability secrecy, or error sanitization. Those requirements are
normative in the specification and represented in the semantic corpus.

Validation begins at the transport byte boundary. Parsing a fixture through a
permissive DOM first is not a substitute for the strict framing cases because a
DOM may already have replaced malformed UTF-8 or collapsed duplicate decoded
property names. Conformance runners therefore consume raw fixture bytes when a
case declares a byte-level encoding or duplicate-name expectation.

The v1 schema describes wire shape; the specification additionally closes the
binding vocabulary and state machine. In particular, snapshot ordering,
registered member-kind checks, collection index semantics, capability-gated
projection, monotonic acknowledgements, bounded admission/output, reconnect
replacement, and telemetry redaction are semantic requirements.

The schemas and corpus are stable inputs to .NET and web SDK generation. A
consumer must not infer wire names from CLR, TypeScript, or framework type
names. Generated wire models may add implementation details, but may not
accept a message that the applicable closed schema rejects.

Corpus files are test vectors, not production defaults. UUIDs and capability
tokens appearing in them are deliberately public and must never be reused as
credentials. Implementations should report the manifest case ID on a
conformance failure without logging the case's application values or token.
