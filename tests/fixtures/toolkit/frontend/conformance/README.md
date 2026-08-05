# MVVM conformance fixtures

This directory contains the framework-neutral fixtures exercised by
`@runic-artifex/mvvm-conformance`.

`manifest.json` is the semantic entry point: it identifies the protocol and
declares the canonical suite IDs, fixture documents, and case counts. The
protocol corpus under `protocol/v1` is kept byte-identical to
`protocol/mvvm/corpus/v1`; tests derive that file set from the protocol manifest
and compare the files directly.

The repository deliberately does not maintain a second SHA-256/byte-length
catalog. Git and package management already preserve artifact bytes, while the
conformance tests validate the structure and behavior that matter to consumers.

When adding a scenario, update its suite document and the corresponding
`caseCount` and totals in `manifest.json`, then run `npm run verify`.
