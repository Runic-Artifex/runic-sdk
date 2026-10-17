# SDK specifications

Normative schemas, protocol contracts, and cross-language conformance corpora live
here, grouped by the component that owns them. Managed and web tests consume these
inputs. Translation CLI packages embed their schemas directly from this tree.

- [Desktop](desktop): native transport contract and conformance vectors.
- [Assets](assets): archive format decisions.
- [Application](application): current Window and View contract ownership and its package documentation.
- [Platform](platform): cross-platform designs of native services, such as [privileged operations](platform/privileged-operations.md), [secret storage](platform/secret-storage.md), [opening files and URLs](platform/open-activation.md), the [trash](platform/trash.md), [file transfers](platform/file-transfers.md), [volumes](platform/volumes.md) and [desktop applications](platform/desktop-applications.md).

Runic Application Views selects contracts from explicit .NET Window and View
types and generates clients during the build. Its generated contract is not a
separately versioned protocol under this directory; see the
[Views package guide](../packages/dotnet/Runic.Application.Views/README.md).

Change versioned contracts and all affected implementations together. Preserve
schema identifiers and public package identities when changing repository paths.
