# Runic Assets archive version 1

Identity: `runic.assets.archive/1`

A Runic Assets archive is a ZIP file with two kinds of entries:

- `runic-assets.json` is the canonical UTF-8 JSON manifest.
- `assets/<relative-path>` contains each manifest-declared asset.

The manifest object contains `version` and an `assets` array. Each asset records
`path`, `mediaType`, `length`, lowercase `sha256`, `entryPoint`, and `cacheMode`.
Assets are ordered by ordinal path and exactly one is the entry point.

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

Schema 1 remains the current schema. `AssetArchive.Inspect` provides a
deterministic validated manifest report, while `GetCompatibilityReport` reports
that schema-1 migration is not required. `MigrateAsync` validates a compatible
schema-1 archive and copies it byte-for-byte rather than rewriting it.
