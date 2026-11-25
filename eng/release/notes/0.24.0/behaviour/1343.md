- **Runic Assets archive readers follow the archive specification's new reader rules.** `AssetArchive.Read`,
  `ReadAsync`, `ReadEmbedded` and `Inspect`, and so the packer's `verify`, now reject an archive in which an entry's
  bytes do not match its ZIP CRC-32, an entry is marked encrypted, the manifest is not UTF-8 or has two members of the
  same name in one object, or a `cacheMode` is not one of the names `NoStore`, `Revalidate` and `Immutable` (a number
  such as `"2"` or a padded name was accepted). `ReadEmbedded` checks each asset's CRC-32 when it is first opened, so a
  damaged embedded entry fails to open instead of being served. An archive whose manifest has no assets, no entry point
  or two now fails with `InvalidDataException`, as documented, instead of `ArgumentException`. Archives that
  `AssetArchive.Write*` and the packer write are unaffected. See the
  [reader rules](../../../specs/assets/archive-v1.md#reader-rules) and the
  [archive vectors](../../../specs/assets/vectors/README.md).
