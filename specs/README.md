# SDK specifications

Normative schemas, protocol contracts and cross-language conformance corpora live
here, grouped by the component that owns them. Managed and web tests consume these
same inputs. Translation CLI packages embed their schemas directly from this tree.
Generated bridge IR and facades are checked with `bun run verify:bridge`.

- [Application Bridge](application/protocol/application-bridge): protocol fixtures and generated contracts.
- [Desktop](desktop): native transport contract and conformance vectors.
- [Command line](command-line): serialized command contract corpus.
- [Translations](translations): schemas, compiler corpus, capabilities and CLDR inputs/licenses.
- [Assets](assets): archive format decisions.

Change a contract and all affected implementations together. Preserve versioned
schema identifiers and public package identities when changing repository paths.
