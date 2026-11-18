# Runic Assets archive versions 1 and 2

Identities: `runic.assets.archive/1` and `runic.assets.archive/2` (since 0.22)

A Runic Assets archive is a ZIP file with two kinds of entries:

- `runic-assets.json` is the canonical UTF-8 JSON manifest.
- `assets/<relative-path>` contains each manifest-declared asset.

The manifest object contains `version` and an `assets` array. Each asset records
`path`, `mediaType`, `length`, lowercase `sha256`, `entryPoint`, and `cacheMode`.
Assets are ordered by ordinal path and exactly one is the entry point.

Schema 2 is schema 1 plus shared content: an asset may record `contentPath`,
the path of another declared asset with the same `length` and `sha256` whose
ZIP entry holds its bytes. Such an asset has no `assets/` entry of its own. The
named asset must have its own entry (no chains) and may differ in every other
field, so each path keeps its media type, entry-point flag and cache mode, and
its entity tag stays derived from its digest. Writers store each distinct
content (length and digest) once, under the first of its paths in ordinal
order, and name that path in the others' `contentPath`. A writer declares
`runic.assets.archive/2` only when some asset has `contentPath`; an archive
without duplicate content is written as schema 1, byte for byte as before, so
older readers still load it. Readers reject `contentPath` in a schema-1
manifest.

Readers reject unsupported versions, unsafe or duplicate paths, missing or
undeclared ZIP entries, length or digest mismatches, invalid cache modes, and
configured file/archive/manifest/uncompressed-size limit violations. Writers use the ZIP
epoch timestamp and deterministic entry ordering. Already-compressed media is
stored; other content uses ZIP deflate compression.

The Linux-only directory compiler pins a no-follow root directory handle for
the whole scan and write, so a root or ancestor replacement cannot redirect an
archive build. Other platforms can consume portable archives and embedded
sources, but must not compile a directory archive until an equivalent
handle-validated implementation exists.

The format deliberately uses standard ZIP rather than CS-WebUI's historical
private optimized VFS format. Host adapters consume the same manifest and source
contracts without becoming part of the archive.

`AssetArchive.CurrentVersion` remains `runic.assets.archive/1`, the schema of
an archive without shared content. `AssetArchive.Inspect` provides a
deterministic validated manifest report of the canonical form: the schema and
`contentPath` fields the writer would produce for the same assets, so a
schema-1 archive that stores duplicate content twice reports schema 2. Readers
accept schemas 1 and 2 and reject other versions. Readers before 0.22 accept
only schema 1.
