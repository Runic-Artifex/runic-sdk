# Runic Assets archive versions 1 and 2

Identities: `runic.assets.archive/1` and `runic.assets.archive/2` (since 0.22)

A Runic Assets archive is a ZIP file with two kinds of entries:

- `runic-assets.json` is the canonical UTF-8 JSON manifest.
- `assets/<relative-path>` contains each manifest-declared asset.

The manifest object contains `version` and an `assets` array. Each asset records
`path`, `mediaType`, `length`, lowercase `sha256`, `entryPoint`, and `cacheMode`.
Assets are ordered by ordinal path and exactly one is the entry point.
[`manifest.schema.json`](manifest.schema.json) gives the manifest's shape with its
closed value sets; the [reader rules](#reader-rules) below are normative.

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
undeclared ZIP entries, length, CRC-32 or digest mismatches, encrypted entries,
manifests outside the JSON profile, invalid cache modes, and configured
file/archive/manifest/uncompressed-size limit violations. Writers use the ZIP
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

Readers accept schemas 1 and 2 and reject other versions. Readers before 0.22
accept only schema 1.

## Reader rules

These rules decide whether an archive is valid and what a reader reports for it (since 0.24). An archive that breaks
one is invalid, and the reader serves none of it, except as [lazy reading](#eager-and-lazy-reading) allows. Each rule
names the error code that the [vectors](vectors/README.md) use for it. The vectors have an archive for every code
except `limit-exceeded`, whose limits are configurable. Split archives and nesting depth have none.

Archives that `runic.assets.archive` writers produce satisfy every rule. Some were added in 0.24 for archives that no
writer produces: the CRC-32 check, the encryption flag, cache modes by name only, the JSON profile (duplicate member
names, UTF-8 and lone surrogates), local headers that match the central directory, compression methods 0 and 8 only,
and deflate data that ends at `length`.

**Terms.** *White space* is a character with the Unicode `White_Space` property: U+0009 to U+000D, U+0020, U+0085,
U+00A0, U+1680, U+2000 to U+200A, U+2028, U+2029, U+202F, U+205F and U+3000. It is .NET's `char.IsWhiteSpace` and
Rust's `char::is_whitespace`; U+180E, U+200B and U+FEFF are not white space. A *control character* is one of general
category Cc (U+0000 to U+001F and U+007F to U+009F; .NET's `char.IsControl`).

### ZIP container

The archive is a ZIP file ([APPNOTE](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT)) on one disk.

- **Structure.** The reader finds the end of central directory record and reads the central directory. A file
  without one, a truncated one, a split archive, and an end record that says 0xFFFF entries without a ZIP64 end
  record are invalid (`zip-invalid`). ZIP64 end records and extra fields are valid; a writer adds them only when a
  count, size or offset does not fit the classic fields, which can happen above 65,535 entries.
- **Entries.** A reader takes each entry's name, compression method, sizes and CRC-32 from its central directory
  record. A name is decoded as UTF-8; writers set general-purpose bit 11 for a name that is not ASCII. A name that
  ends in `/` is a directory entry: the reader skips it.
- **Offsets.** Every offset in the end records and the central directory counts from the start of the file. Bytes
  before the first local header, such as a self-extracting stub, are valid when the offsets count them; offsets that
  count from the first local header instead do not lead to the records, and the archive is invalid (`zip-invalid`).
- **Local headers.** At the offset that an entry's central record gives there is a local header, with its signature
  and the same name and compression method as the central record. No two entries' bytes, each from its local header
  to the end of its data, overlap, and none overlaps the central directory (`zip-invalid`). So one entry's bytes are
  never read under two names, and a reader that walks the local headers reads the archive as the central directory
  says.
- **Encryption.** An entry whose general-purpose bit 0 (encrypted) is set is invalid, even when its data is plain
  text, and so is a directory entry with it (`zip-encrypted`).
- **Duplicates.** Two entries other than directory entries with the same name, compared ordinally, are invalid
  (`zip-duplicate-entry`).
- **Compression.** Writers use method 0 (stored) or 8 (deflate), and readers read both. Every other method is
  invalid (`zip-invalid`), including 9 (Deflate64), which no writer produces; a lazy reader may find it only when it
  opens the entry. Bit 3 (sizes in a data descriptor) is valid; the central directory's values count.
- **Deflate data.** An entry of method 8 whose data does not inflate, an invalid or truncated deflate stream, is
  invalid (`zip-invalid`). A complete stream that inflates to fewer or more bytes than the entry's uncompressed size
  breaks the content rule (`length-mismatch`, [Entries and content](#entries-and-content)).
- **CRC-32.** The CRC-32 of the bytes that a reader inflates from an entry, the manifest's and every asset's, must
  equal the CRC-32 of its central directory record (`crc-mismatch`). This holds in both reading modes, so a reader
  never serves a damaged entry.

An archive comment is valid and ignored.

### Manifest JSON profile

`runic-assets.json` must exist (`manifest-missing`). Its bytes are read under this profile, and anything outside it is
invalid (`manifest-not-json`):

- The bytes are UTF-8 (RFC 3629), all of them, including the strings of members that the reader ignores. One leading
  byte order mark (EF BB BF) is ignored.
- They are one JSON text (RFC 8259): no comments, no trailing commas, nothing after the value.
- No object has two members with the same name, compared after unescaping, at any depth. This is the I-JSON rule
  (RFC 7493, section 2.3); a reader does not pick the first or the last.
- Values nest at most 64 levels deep.
- No string, a member's name or a value, contains a lone surrogate escape (`"\ud800"`), in the members that the
  reader ignores too: the text is I-JSON (RFC 7493, section 2.1).

Members that this specification does not define are ignored, at every level.

### Manifest and assets

- **Shape and version.** The manifest is an object with a string `version` and an array `assets`. `version` is
  `runic.assets.archive/1` or `runic.assets.archive/2` (`manifest-version`).
- **Asset members.** Each element of `assets` is an object with these members (`asset-invalid`):

  | Member | Value |
  | --- | --- |
  | `path` | A string that the [path rules](#paths) accept (`path-invalid`) |
  | `mediaType` | A string that is not empty, has no leading or trailing white space, and contains no CR, LF or NUL |
  | `length` | A JSON number with no fraction and no exponent part (`13`, not `13.0` or `1.3e1`), from 0 to 2⁶³−1. `-0` is 0 |
  | `sha256` | A string of 64 hexadecimal digits, in either case; a reader reports it in lower case |
  | `entryPoint` | `true` or `false` |
  | `cacheMode` | Exactly one of the names `NoStore`, `Revalidate` and `Immutable`, compared ordinally: not a number such as `"2"`, a list, another case or a padded name |
  | `contentPath` | Optional, schema 2 only: a string that the path rules accept. In a schema-1 manifest it is invalid (`content-path-invalid`) |

- **The set.** There is at least one asset, no two assets have the same normalized path, and exactly one has
  `entryPoint` `true` (`manifest-invalid`).
- **Shared content.** A `contentPath` must name, after normalization, another declared asset that has its own entry
  (no `contentPath` itself) and the same `length` (`content-path-invalid`). Its `sha256` must equal the named asset's
  too; the content check below finds a difference.
- **Order.** The manifest's order does not matter. A reader reports the assets ordered by their normalized paths,
  compared by UTF-16 code units (ordinal comparison in .NET, `<` on strings in JavaScript), which is not code point
  order for characters outside the Basic Multilingual Plane.

### Paths

A `path` or `contentPath` is normalized and checked as follows (`path-invalid`):

1. It is not empty and has no leading or trailing white space.
2. Each backslash becomes a slash.
3. It does not start with `/`, nor with an ASCII letter followed by `:`.
4. Split at `/`, no segment is empty, `.` or `..`.
5. No character is a control character, `:`, `?` or `#`.
6. No segment contains `%` followed by two hexadecimal digits.

The result is the normalized path. Its ZIP entry is `assets/` followed by it, compared ordinally.

### Entries and content

- **Entries.** Each asset without `contentPath` has an entry named `assets/<normalized path>` (`entry-missing`), whose
  uncompressed size in the central directory equals `length` (`length-mismatch`). Every entry other than the manifest,
  the asset entries and directory entries is invalid, including an entry of an asset that has `contentPath`
  (`entry-undeclared`).
- **Content.** An entry inflates to exactly `length` bytes (`length-mismatch`) whose CRC-32 matches (see above,
  `crc-mismatch`) and whose SHA-256 digest equals `sha256` (`digest-mismatch`). A reader reads on past `length` to
  find that the data ends there: stored data or a deflate stream that holds more or fewer bytes is `length-mismatch`,
  even when the first `length` bytes match. An asset with `contentPath` has the named asset's bytes and is checked
  against its own `sha256`.

### Eager and lazy reading

A reader reads in one of two modes:

- **Eager**, for an archive that comes from outside the application, such as a file: the reader checks every rule,
  inflating every entry, before it returns. .NET: `AssetArchive.Read` and `ReadAsync`.
- **Lazy**, for an archive embedded in the application's own executable or assembly, which its build checked: the
  reader checks every rule except the content rules before it returns, and inflates each entry when an asset that uses
  it is first served. It checks that entry's length and CRC-32 then, and an asset that fails is never served. It does
  not check digests. .NET: `AssetArchive.ReadEmbedded`, whose `ValidateAsync` checks the digests on demand.

### Limits

A reader bounds its input. The defaults are 1 GiB of archive, 4 MiB of inflated manifest, 100,000 assets and 1 GiB of
declared asset lengths in total; a reader can be configured with others. A reader may also bound two things that
these do not:

- the entries of the central directory, which it reads before the manifest, at no fewer than twice the asset limit
  plus one: a valid archive has an entry for each asset that has no `contentPath`, the manifest, and at most one
  directory entry per folder;
- one asset's length, at no less than 2³¹−1 bytes, .NET's bound.

An archive over a limit is invalid (`limit-exceeded`). The vectors stay far below the defaults.

### Error codes

| Code | Rule |
| --- | --- |
| `zip-invalid` | [ZIP container](#zip-container): structure |
| `zip-encrypted` | ZIP container: encryption |
| `zip-duplicate-entry` | ZIP container: duplicates |
| `crc-mismatch` | ZIP container: CRC-32 |
| `manifest-missing` | [Manifest JSON profile](#manifest-json-profile): no `runic-assets.json` |
| `manifest-not-json` | Manifest JSON profile |
| `manifest-version` | [Manifest and assets](#manifest-and-assets): shape and version |
| `asset-invalid` | Manifest and assets: asset members |
| `path-invalid` | [Paths](#paths) |
| `manifest-invalid` | Manifest and assets: the set |
| `content-path-invalid` | Manifest and assets: `contentPath` in schema 1, or its target |
| `entry-missing` | [Entries and content](#entries-and-content): entries |
| `entry-undeclared` | Entries and content: entries |
| `length-mismatch` | Entries and content |
| `digest-mismatch` | Entries and content: content (eager only) |
| `limit-exceeded` | [Limits](#limits) |

Each invalid vector breaks one rule, so its code is unambiguous. When an archive breaks several, which one a reader
reports is not specified, and its two reading modes may report different ones: lazy reading checks the content rules
only when it opens an asset, and each mode may meet the broken rules in its own order. Both modes fail, except where
[lazy reading](#eager-and-lazy-reading) skips a rule (a digest). A reader reports a failure in its own terms; a
conformance adapter maps it to these codes.

## Notes for .NET

`AssetArchive.CurrentVersion` remains `runic.assets.archive/1`, the schema of
an archive without shared content. `AssetArchive.Inspect` provides a
deterministic validated manifest report of the canonical form: the schema and
`contentPath` fields the writer would produce for the same assets, so a
schema-1 archive that stores duplicate content twice reports schema 2.

`AssetArchive.Read`, `ReadAsync`, `ReadEmbedded` and `Inspect` report every broken rule as `InvalidDataException`,
with no error code; `AssetArchiveReadOptions` sets the limits. The reader reads the end records and the central
directory with `System.IO.Compression.ZipArchive`, reads them again for the local header offsets, which `ZipArchive`
does not expose, and checks the local headers, overlaps and compression methods before it returns, in both modes. It
inflates each entry from the archive's bytes itself, reading on past `length`.

The .NET reader bounds one asset at 2³¹−1 bytes, the largest .NET array, a fixed limit that `AssetArchiveReadOptions`
does not raise; a larger asset fails as a limit (`limit-exceeded`). It bounds the central directory only through the
archive size. It checks every rule above; until 0.24 it read entries whose local header had another name or method,
overlapping entries, method 9 (Deflate64), and deflate data longer than `length` as its first `length` bytes.
