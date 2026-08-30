# Runic Desktop presentation contract

Contract identity: `runic.desktop.presentation/1`

This directory is the language-neutral authority for Runic Desktop observable
behavior. It defines the presentation-host concepts that every implementation
maps into its own language and runtime idioms. Public .NET types, Effect types,
ASP.NET Core, browser APIs, and the WebUI compatibility packet are mappings or
profiles; none of them defines the product contract.

The contract is normative for M5 and later. M0-M4 behavior remains retained
compatibility evidence and is classified against this contract as retained,
intentionally divergent, or not applicable.

## Normative artifacts

- [Presentation host](presentation-host.md) defines vocabulary, ownership,
  lifecycle, transport, streaming, security, and error semantics.
- [Ownership map](ownership.md) assigns every cross-product capability to one
  Runic product.
- [Language mappings](language-mappings.md) maps the semantics to .NET and
  TypeScript+Effect without making either API normative.
- [Milestone gates](milestones.md) binds M5-M8 completion to the shared
  contract.
- [M7 wire-profile decision](wire-profile.md) selects and isolates the v1
  browser framing profile.
- [Conformance](conformance/README.md) selects portable scenario and codec
  vector formats.

The schemas and committed examples under `conformance/` are normative test
inputs. Test reports, platform receipts, performance measurements, WebUI
differential results, and language-specific API baselines are evidence, not
contract sources.

Run `npm run verify:contract` to validate the contract identity and version,
the committed schemas, every scenario and vector fixture, Markdown artifact
links, and the closed authored fixture set. This is repository-local format
verification; language implementations still supply their own conformance and
platform evidence.

## Change rules

Contract version `1` may gain clarifications and new optional capabilities that
do not change existing observable outcomes. A change to required lifecycle,
ordering, cancellation, security, serialization, or error behavior requires a
new contract version and explicit migration guidance.

Wire profiles version independently. In particular,
`webui-compat/52f9e75` describes the retained WebUI framing implemented through
M4; it does not constrain the Runic-owned transport selected for M7.
