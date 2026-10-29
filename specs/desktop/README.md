# Runic Desktop presentation contract

Contract identity: `runic.desktop.presentation/1`

This directory is the language-neutral authority for Runic Desktop observable
behavior. It defines the presentation-host concepts that every implementation
maps into its own language and runtime idioms. Public .NET types, Effect types,
ASP.NET Core, browser APIs, and the WebUI compatibility packet are mappings or
profiles; none of them defines the product contract.

The contract is the authority for current Runic Desktop behavior. Retained
compatibility behavior is classified against this contract as retained,
intentionally divergent, or not applicable.

## Normative artifacts

- [Presentation host](presentation-host.md) defines vocabulary, ownership,
  lifecycle, transport, streaming, security, and error semantics.
- [Ownership map](ownership.md) assigns every cross-product capability to one
  Runic product.
- [Language mappings](language-mappings.md) maps the semantics to .NET and
  TypeScript+Effect without making either API normative.
- [Conformance requirements](milestones.md) states the implementation and
  integration requirements for the shared contract.
- [Wire-profile decision](wire-profile.md) selects and isolates the v1
  browser framing profile.
- [Conformance](conformance/README.md) selects portable scenario and codec
  vector formats.
- [Shortcut cases](fixtures/shortcuts.json) are the keyboard shortcut text,
  display and key-event matching that `DesktopShortcut` and the page's
  `matchesShortcut` both check.
- [Menu entry cases](fixtures/menu-entries.json) are the title and command id
  rules that the Runic Desktop menu bar and status icon menus both check.
- [In-page menu bar routes](menu-bar-page.md) are the reserved Bridge routes a
  page uses to render the menu bar and run its items, with the shared
  [state cases](fixtures/menu-bar-page.json).

The schemas and committed examples under `conformance/` are normative test
inputs. Test reports, platform receipts, performance measurements, WebUI
differential results, and language-specific API baselines are evidence, not
contract sources.

Run `npm run verify:contract` to validate the contract identity and version,
the committed schemas and operation vocabulary, every scenario and vector
fixture, that every scenario creates each resource it uses, Markdown artifact
links, and the closed authored fixture set. The engineering CI job runs it
through `eng/verify-desktop-contract.test.mjs`. This is repository-local format
verification; language implementations still supply their own conformance and
platform evidence.

## Change rules

Contract version `1` may gain clarifications and new optional capabilities that
do not change existing observable outcomes. A change to required lifecycle,
ordering, cancellation, security, serialization, or error behavior requires a
new contract version and explicit migration guidance.

Wire profiles version independently. In particular,
`webui-compat/52f9e75` describes retained WebUI framing; it does not define the
Runic Desktop product contract.
