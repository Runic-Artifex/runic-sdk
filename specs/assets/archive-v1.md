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
configured file/archive/uncompressed-size limit violations. Writers use the ZIP
epoch timestamp and deterministic entry ordering. Already-compressed media is
stored; other content uses ZIP deflate compression.

The format deliberately uses standard ZIP rather than CsWebUi's historical
private optimized VFS format. Host adapters consume the same manifest and source
contracts without becoming part of the archive.
