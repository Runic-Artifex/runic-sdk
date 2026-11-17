- **Size: the asset archive stores identical files once.** When two paths of a frontend have the same content, such as
  the fonts and stylesheet that two frontend entries share, `RunicAssetsDist` and `AssetArchive.Write*` now store one
  ZIP entry for it, and the other paths name that entry in `runic-assets.json` with `contentPath` (archive schema
  `runic.assets.archive/2`). Every path keeps its own media type, entry-point flag and cache mode; lengths, SHA-256
  digests, entity tags and Subresource Integrity tokens are unchanged, and an embedded archive inflates shared content
  once. A fixture with two entries that share three fonts and a stylesheet goes from a 618,913-byte archive to 354,901
  bytes, and its NativeAOT executable on linux-x64 from 3,066,248 to 2,800,008 bytes (−0.25 MiB). An archive without
  duplicate content is still written as `runic.assets.archive/1`, byte for byte as before, and 0.22 reads archives
  from earlier releases unchanged. Runic.Assets 0.21 and earlier cannot read a schema-2 archive: if you pack an archive
  with 0.22 and embed it through `RunicAssetsEmbeddedArchive` in an application that uses an older Runic.Assets,
  update that application as well. `AssetArchive.Inspect` reports the canonical form, so a schema-1 archive that holds
  duplicate content reports schema 2. The packer's RAS1005 warning counts source maps by path, as before.
