# SDK specifications

Normative schemas, protocol contracts, and cross-language conformance corpora live
here, grouped by the component that owns them. Managed and web tests consume these
inputs. Translation CLI packages embed their schemas directly from this tree.
The .NET designs of the native platform services are not contracts; they are in
[docs/design/platform](../docs/design/platform/README.md).

- [Desktop](desktop): native transport contract and conformance vectors, and the [host protocol](desktop/host-protocol.md)
  between a host application and the guest application whose windows it shows.
- [Assets](assets): archive format decisions.
- [Application](application): the Runic Application Views protocol between a producer (the backend of a window) and
  a client (the page), its value types and canonical form, and its [conformance corpus](application/conformance).

Runic Application Views selects contracts from explicit .NET Window and View
types and generates clients during the build. The messages between them follow
the [Views protocol](application/README.md), a versioned, language-neutral
protocol; the generated contract of an application (its members, commands and
types) is not versioned separately. See the
[Views package guide](../packages/dotnet/Runic.Application.Views/README.md).

Change versioned contracts and all affected implementations together. Preserve
schema identifiers and public package identities when changing repository paths.
