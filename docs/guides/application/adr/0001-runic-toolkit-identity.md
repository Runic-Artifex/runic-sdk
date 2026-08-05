# ADR 0001: Runic Toolkit identity

- Status: Accepted
- Updated: 2026-08-05

## Decision

The clean-break successor to WebUIToolkit is named **Runic Toolkit**. Owned .NET
namespaces and NuGet package IDs use `RunicToolkit`; npm packages use the
`@runic-artifex` scope; diagnostics use `RTK`-prefixed ranges.

No compatibility facade is provided for the unshipped monorepo identity.
`CsWebUi` remains the identity of the external host dependency and is used only
in explicitly named adapter packages.
