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

The schemas intentionally have no `$id`. An owned schema domain has not yet
been approved. Relative references are resolved from the schema files on disk.

## Conformance

A conforming implementation must validate both the schema fixtures and the
semantic traces. Schema validation alone cannot enforce encoded UTF-8 byte
limits, duplicate-key rejection, revision transitions, ordering, cancellation
races, capability secrecy, or error sanitization. Those requirements are
normative in the specification and represented in the semantic corpus.

The schemas and corpus are stable inputs to .NET and web SDK generation. A
consumer must not infer wire names from CLR, TypeScript, or framework type
names. Generated wire models may add implementation details, but may not
accept a message that the applicable closed schema rejects.
