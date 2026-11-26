## Assets archive conformance for any language

The Runic Assets archive vectors now run through the language-neutral conformance driver:
`bun run conformance dotnet-assets` reads every vector with the .NET reader in eager and lazy reading, through an
adapter on `Runic.Assets`' public API, and an archive reader in another language adds an adapter of its own. See the
[archive vectors](../../../specs/assets/vectors/README.md).

The [reader rules](../../../specs/assets/archive-v1.md#reader-rules) now settle what a second reader found open, with
15 more vectors: ZIP64 records, bytes before the first entry, compression methods other than stored and deflate,
deflate data that does not inflate or ends early, a `length` of `-0`, which characters are white space, lone
surrogates, limits on ZIP entries and on one asset's size, and that the two reading modes may report different codes
for an archive that breaks several rules. The .NET reader is unchanged; the rules it does not check yet are listed in
the specification's notes for .NET.
