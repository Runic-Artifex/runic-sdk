// Writes the Runic Assets archive vectors (README.md in this directory): crafted.json, archives that this script builds
// byte by byte, one broken rule each, and packer.json, the archives in packer/ that the .NET packer wrote. `--check`
// compares instead of writing and exits 1 when a file is stale.
//
//   bun specs/assets/vectors/generate.mjs [--check]
//
// The crafted cases started as the R4c lab's corpus (42 archives); the reader rules they test are in ../archive-v1.md.
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { inflateRawSync } from "node:zlib";

const here = dirname(fileURLToPath(import.meta.url));
const sha = bytes => createHash("sha256").update(bytes).digest("hex");
const utf8 = text => Buffer.from(text, "utf8");

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc32 = bytes => {
  let c = 0xffffffff;
  for (const b of bytes) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};

// A ZIP of stored entries, enough to craft manifests and directories; deflated entries come from the packer archives,
// except a few hand-written deflate streams. An entry's options override what its record says:
// - method: the compression method (0 by default) in both headers, and payload: the bytes written as its data;
// - crc: the CRC-32 (that of the entry's bytes by default), and size: the uncompressed size (their length);
// - localName and localMethod: the name and the method in the local header, when they differ from the central record's;
// - within: an earlier entry's name. The entry has no local record of its own: its central record gives the offset of
//   that entry's data, which must hold the local record (localRecord below).
// A Zip's own options: prefix, bytes before the first entry, with offsets from the start of the file unless
// relativeOffsets; zip64: "end-record" (a ZIP64 end record and locator, the classic fields at their markers) or
// "extra-fields" (ZIP64 extra fields for every entry's sizes and offset, the classic fields at their markers).
class Zip {
  entries = [];
  flags = 0;
  crcOverride = null; // [entry name, CRC-32]
  comment = Buffer.alloc(0);
  zip64Marker = false;
  prefix = Buffer.alloc(0);
  relativeOffsets = false;
  zip64 = null;
  add(name, bytes, options = {}) {
    this.entries.push([name, typeof bytes === "string" ? utf8(bytes) : Buffer.from(bytes), options]);
    return this;
  }
  bytes() {
    const out = [this.prefix];
    const central = [];
    let offset = this.relativeOffsets ? 0 : this.prefix.length;
    const u16 = v => { const b = Buffer.alloc(2); b.writeUInt16LE(v); return b; };
    const u32 = v => { const b = Buffer.alloc(4); b.writeUInt32LE(v >>> 0); return b; };
    const u64 = v => { const b = Buffer.alloc(8); b.writeBigUInt64LE(BigInt(v)); return b; };
    const extra = this.zip64 === "extra-fields";
    const version = this.zip64 ? 45 : 20;
    const dataOffsets = new Map();
    for (const [name, data, options] of this.entries) {
      const { method = 0, payload = data, crc: entryCrc, size = data.length, localName = name, localMethod = method, within } = options;
      const nameBytes = utf8(name);
      const localNameBytes = utf8(localName);
      const entryOffset = within === undefined ? offset : dataOffsets.get(within);
      const crc = this.crcOverride?.[0] === name ? this.crcOverride[1] : entryCrc ?? crc32(data);
      const flags = this.flags | (/^[\x00-\x7f]*$/.test(name) ? 0 : 0x0800);
      const field = value => (extra ? 0xffffffff : value);
      const localExtra = extra ? Buffer.concat([u16(1), u16(16), u64(size), u64(payload.length)]) : Buffer.alloc(0);
      const centralExtra = extra ? Buffer.concat([u16(1), u16(24), u64(size), u64(payload.length), u64(entryOffset)]) : Buffer.alloc(0);
      const local = Buffer.concat([
        u32(0x04034b50), u16(version), u16(flags), u16(localMethod), u16(0), u16(0x0021), u32(crc), u32(field(payload.length)),
        u32(field(size)), u16(localNameBytes.length), u16(localExtra.length), localNameBytes, localExtra, payload,
      ]);
      central.push(Buffer.concat([
        u32(0x02014b50), u16(version), u16(version), u16(flags), u16(method), u16(0), u16(0x0021), u32(crc),
        u32(field(payload.length)), u32(field(size)), u16(nameBytes.length), u16(centralExtra.length), u16(0), u16(0),
        u16(0), u32(0), u32(field(entryOffset)), nameBytes, centralExtra,
      ]));
      if (within !== undefined) continue;
      dataOffsets.set(name, offset + 30 + localNameBytes.length + localExtra.length);
      out.push(local);
      offset += local.length;
    }
    const directory = Buffer.concat(central);
    const count = this.zip64Marker || this.zip64 === "end-record" ? 0xffff : this.entries.length;
    const records = [];
    if (this.zip64 === "end-record") {
      const n = this.entries.length;
      records.push(
        u32(0x06064b50), u64(44), u16(45), u16(45), u32(0), u32(0), u64(n), u64(n), u64(directory.length), u64(offset),
        u32(0x07064b50), u32(0), u64(offset + directory.length), u32(1),
      );
    }
    const end64 = this.zip64 === "end-record";
    return Buffer.concat([
      ...out, directory, ...records,
      u32(0x06054b50), u16(0), u16(0), u16(count), u16(count), u32(end64 ? 0xffffffff : directory.length),
      u32(end64 ? 0xffffffff : offset), u16(this.comment.length), this.comment,
    ]);
  }
}

// A deflate stream (RFC 1951) of one final stored block: deterministic, unlike a compressor's output.
const storedBlock = bytes => {
  const header = Buffer.alloc(5);
  header[0] = 1;
  header.writeUInt16LE(bytes.length, 1);
  header.writeUInt16LE(~bytes.length & 0xffff, 3);
  return Buffer.concat([header, bytes]);
};

// The local record (header and data) of a stored entry `name` with `bytes`, as Zip writes it.
const localRecord = (name, bytes) => new Zip().add(name, bytes).bytes().subarray(0, 30 + utf8(name).length + bytes.length);

const INDEX = utf8("<!doctype html><title>corpus</title>\n");
const A = utf8("same content\n");
const C = utf8("other content!\n");
const EMPTY = Buffer.alloc(0);

// One asset's manifest object, written in the packer's member order; `extra` members follow.
const asset = (path, bytes, entryPoint, { cacheMode = "Revalidate", mediaType = "text/plain", ...extra } = {}) =>
  JSON.stringify({ path, mediaType, length: bytes.length, sha256: sha(bytes), entryPoint, cacheMode, ...extra });
const manifest = (version, assets) => `{"version":${JSON.stringify(version)},"assets":[${assets.join(",")}]}`;
const v1 = assets => manifest("runic.assets.archive/1", assets);
const v2 = assets => manifest("runic.assets.archive/2", assets);
const index = () => asset("index.html", INDEX, true, { mediaType: "text/html" });
const a = asset("a.txt", A, false);
// A schema-1 archive with index.html and a.txt and the manifest text `m`.
const simple = m => new Zip().add("runic-assets.json", m).add("assets/index.html", INDEX).add("assets/a.txt", A);
const replaceOnce = (text, from, to) => {
  if (!text.includes(from)) throw new Error(`'${from}' is not in ${text}`);
  return text.replace(from, to);
};

// What a reader reports for a valid archive: each asset's normalized metadata, in UTF-16 code unit order of the path.
const described = (path, bytes, entryPoint, mediaType = "text/plain", cacheMode = "Revalidate") =>
  ({ path, mediaType, length: bytes.length, sha256: sha(bytes), entryPoint, cacheMode });
const byPath = (x, y) => (x.path < y.path ? -1 : x.path > y.path ? 1 : 0);
const valid = assets => ({ result: "valid", assets: [...assets].sort(byPath) });
const baseline = () => [described("a.txt", A, false), described("index.html", INDEX, true, "text/html")];
const invalid = error => ({ result: "invalid", error });

const crafted = [];
const add = (name, description, zip, expect, expectLazy) =>
  crafted.push({ name, description, bytes: Buffer.isBuffer(zip) ? zip : zip.bytes(), expect, expectLazy });

add("valid-schema-1", "A schema-1 archive: the baseline the other cases change.", simple(v1([index(), a])), valid(baseline()));
add("valid-unsorted-manifest", "Readers sort the assets by path; the manifest's order does not matter.",
  simple(v1([a, index()])), valid(baseline()));
add("valid-uppercase-digest", "A digest in upper case is read and reported in lower case.",
  simple(v1([index(), replaceOnce(a, sha(A), sha(A).toUpperCase())])), valid(baseline()));
add("valid-backslash-path", "A backslash in a manifest path is read as a slash.",
  new Zip().add("runic-assets.json", v1([index(), asset("sub\\a.txt", A, false)]))
    .add("assets/index.html", INDEX).add("assets/sub/a.txt", A),
  valid([described("index.html", INDEX, true, "text/html"), described("sub/a.txt", A, false)]));
add("valid-directory-entry", "Directory entries (names ending in /) are skipped.",
  new Zip().add("runic-assets.json", v1([index(), a])).add("assets/", "").add("assets/index.html", INDEX)
    .add("assets/a.txt", A),
  valid(baseline()));
add("valid-zip-comment", "An archive comment is allowed.", (() => {
  const zip = simple(v1([index(), a]));
  zip.comment = utf8("lab");
  return zip;
})(), valid(baseline()));
add("valid-schema-2", "Schema 2: b.txt shares the stored content of a.txt through contentPath.",
  new Zip().add("runic-assets.json", v2([index(), a, asset("b.txt", A, false, { contentPath: "a.txt" })]))
    .add("assets/index.html", INDEX).add("assets/a.txt", A),
  valid([...baseline(), described("b.txt", A, false)]));
add("valid-cache-modes", "The three cache modes by name.",
  new Zip().add("runic-assets.json", v1([
    index(), asset("a.txt", A, false, { cacheMode: "NoStore" }), asset("c.txt", C, false, { cacheMode: "Immutable" }),
  ])).add("assets/index.html", INDEX).add("assets/a.txt", A).add("assets/c.txt", C),
  valid([
    described("a.txt", A, false, "text/plain", "NoStore"), described("c.txt", C, false, "text/plain", "Immutable"),
    described("index.html", INDEX, true, "text/html"),
  ]));
add("valid-manifest-bom", "One leading UTF-8 byte order mark before the manifest is ignored.",
  new Zip().add("runic-assets.json", Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), utf8(v1([index(), a]))]))
    .add("assets/index.html", INDEX).add("assets/a.txt", A),
  valid(baseline()));

add("valid-zip64-end-record", "A ZIP64 end of central directory record and locator, with the classic end record's fields at their markers.",
  (() => {
    const zip = simple(v1([index(), a]));
    zip.zip64 = "end-record";
    return zip;
  })(), valid(baseline()));
add("valid-zip64-extra-fields", "ZIP64 extra fields hold every entry's sizes and offset, the classic fields at their markers.",
  (() => {
    const zip = simple(v1([index(), a]));
    zip.zip64 = "extra-fields";
    return zip;
  })(), valid(baseline()));
add("valid-leading-data", "Bytes before the first entry, such as a self-extracting stub, with offsets from the start of the file.",
  (() => {
    const zip = simple(v1([index(), a]));
    zip.prefix = utf8("#!/bin/sh\nexit 0\n");
    return zip;
  })(), valid(baseline()));
add("valid-length-minus-zero", "A length of -0 has no fraction and no exponent part: it is 0.",
  new Zip().add("runic-assets.json", v1([index(), replaceOnce(asset("empty.txt", EMPTY, false), '"length":0', '"length":-0')]))
    .add("assets/index.html", INDEX).add("assets/empty.txt", EMPTY),
  valid([described("empty.txt", EMPTY, false), described("index.html", INDEX, true, "text/html")]));
add("valid-path-not-white-space", "U+180E and U+200B are not white space (Unicode White_Space), so a path may start and end with them.",
  new Zip().add("runic-assets.json", v1([index(), asset("\u180ea\u200b", A, false)]))
    .add("assets/index.html", INDEX).add("assets/\u180ea\u200b", A),
  valid([described("index.html", INDEX, true, "text/html"), described("\u180ea\u200b", A, false)]));

add("version-unknown", "An unsupported version.", simple(manifest("runic.assets.archive/3", [index(), a])),
  invalid("manifest-version"));
add("version-missing", "No version member.", simple(`{"assets":[${index()},${a}]}`), invalid("manifest-version"));
add("not-json", "The manifest is not JSON.", simple("{"), invalid("manifest-not-json"));
add("manifest-missing", "No runic-assets.json.", new Zip().add("assets/index.html", INDEX), invalid("manifest-missing"));
add("assets-empty", "No assets.", new Zip().add("runic-assets.json", v1([])), invalid("manifest-invalid"));
add("content-path-in-schema-1", "contentPath in a schema-1 manifest.",
  new Zip().add("runic-assets.json", v1([index(), a, asset("b.txt", A, false, { contentPath: "a.txt" })]))
    .add("assets/index.html", INDEX).add("assets/a.txt", A),
  invalid("content-path-invalid"));
add("content-path-chain", "contentPath names an asset that has a contentPath itself: the named asset must have its own entry.",
  new Zip().add("runic-assets.json", v2([
    index(), a, asset("b.txt", A, false, { contentPath: "a.txt" }), asset("c.txt", A, false, { contentPath: "b.txt" }),
  ])).add("assets/index.html", INDEX).add("assets/a.txt", A),
  invalid("content-path-invalid"));
add("content-path-length-differs", "contentPath names an asset of another length.",
  new Zip().add("runic-assets.json", v2([index(), a, asset("c.txt", C, false, { contentPath: "a.txt" })]))
    .add("assets/index.html", INDEX).add("assets/a.txt", A),
  invalid("content-path-invalid"));
add("content-path-with-own-entry", "An asset with contentPath also has an entry, which is left over.",
  new Zip().add("runic-assets.json", v2([index(), a, asset("b.txt", A, false, { contentPath: "a.txt" })]))
    .add("assets/index.html", INDEX).add("assets/a.txt", A).add("assets/b.txt", A),
  invalid("entry-undeclared"));
add("duplicate-path", "Two assets with the same path.", simple(v1([index(), a, a])), invalid("manifest-invalid"));
add("two-entry-points", "Two entry points.", simple(v1([index(), asset("a.txt", A, true)])), invalid("manifest-invalid"));
add("no-entry-point", "No entry point.",
  simple(v1([asset("index.html", INDEX, false, { mediaType: "text/html" }), a])), invalid("manifest-invalid"));
add("missing-entry", "A declared asset has no entry.",
  new Zip().add("runic-assets.json", v1([index(), a])).add("assets/index.html", INDEX), invalid("entry-missing"));
add("undeclared-entry", "An entry under assets/ that the manifest does not declare.",
  simple(v1([index(), a])).add("assets/extra.txt", C), invalid("entry-undeclared"));
add("undeclared-outside-assets", "An entry outside assets/.", simple(v1([index(), a])).add("README", C),
  invalid("entry-undeclared"));
add("duplicate-zip-entry", "Two entries with the same name.", simple(v1([index(), a])).add("assets/a.txt", A),
  invalid("zip-duplicate-entry"));
add("length-mismatch", "The declared length differs from the entry's.",
  simple(v1([index(), replaceOnce(a, '"length":13', '"length":12')])), invalid("length-mismatch"));
add("digest-mismatch", "The declared digest is another content's. A lazy reader does not check digests.",
  simple(v1([index(), replaceOnce(a, sha(A), sha(C))])), invalid("digest-mismatch"),
  valid([described("a.txt", A, false), described("index.html", INDEX, true, "text/html")].map(entry =>
    entry.path === "a.txt" ? { ...entry, sha256: sha(C) } : entry)));
add("crc-mismatch", "The digest is right and the entry's CRC-32 is wrong: rejected in both modes.", (() => {
  const zip = simple(v1([index(), a]));
  zip.crcOverride = ["assets/a.txt", 0x12345678];
  return zip;
})(), invalid("crc-mismatch"));
add("crc-mismatch-manifest", "The manifest entry's CRC-32 is wrong.", (() => {
  const zip = simple(v1([index(), a]));
  zip.crcOverride = ["runic-assets.json", 0x12345678];
  return zip;
})(), invalid("crc-mismatch"));
add("path-traversal", "A parent-directory segment.",
  new Zip().add("runic-assets.json", v1([index(), asset("../a.txt", A, false)])).add("assets/index.html", INDEX)
    .add("assets/../a.txt", A),
  invalid("path-invalid"));
add("path-absolute", "A rooted path.",
  new Zip().add("runic-assets.json", v1([index(), asset("/a.txt", A, false)])).add("assets/index.html", INDEX)
    .add("assets//a.txt", A),
  invalid("path-invalid"));
add("path-percent", "A percent-encoded octet.",
  new Zip().add("runic-assets.json", v1([index(), asset("a%2etxt", A, false)])).add("assets/index.html", INDEX)
    .add("assets/a%2etxt", A),
  invalid("path-invalid"));
add("path-colon", "A colon.",
  new Zip().add("runic-assets.json", v1([index(), asset("a:b.txt", A, false)])).add("assets/index.html", INDEX)
    .add("assets/a:b.txt", A),
  invalid("path-invalid"));
add("cache-mode-lowercase", "Cache modes are case-sensitive.",
  simple(v1([index(), replaceOnce(a, '"Revalidate"', '"revalidate"')])), invalid("asset-invalid"));
add("cache-mode-numeric", "A number in place of a cache mode's name.",
  simple(v1([index(), replaceOnce(a, '"Revalidate"', '"2"')])), invalid("asset-invalid"));
add("cache-mode-combined", "A list of cache modes.",
  simple(v1([index(), replaceOnce(a, '"Revalidate"', '"Revalidate, Immutable"')])), invalid("asset-invalid"));
add("cache-mode-padded", "A cache mode's name with surrounding whitespace.",
  simple(v1([index(), replaceOnce(a, '"Revalidate"', '" Revalidate"')])), invalid("asset-invalid"));
add("length-fractional", "13.0 has a fraction part.",
  simple(v1([index(), replaceOnce(a, '"length":13', '"length":13.0')])), invalid("asset-invalid"));
add("length-exponent", "1.3e1 has an exponent.",
  simple(v1([index(), replaceOnce(a, '"length":13', '"length":1.3e1')])), invalid("asset-invalid"));
add("length-string", "A length in a string.",
  simple(v1([index(), replaceOnce(a, '"length":13', '"length":"13"')])), invalid("asset-invalid"));
add("media-type-blank", "A blank media type.", simple(v1([index(), replaceOnce(a, "text/plain", " ")])),
  invalid("asset-invalid"));
add("sha256-short", "A digest of 63 characters.", simple(v1([index(), replaceOnce(a, sha(A), sha(A).slice(0, 63))])),
  invalid("asset-invalid"));
add("entry-point-string", "entryPoint in a string.", simple(v1([replaceOnce(index(), "true", '"true"'), a])),
  invalid("asset-invalid"));
add("manifest-duplicate-member", "Two version members: duplicate member names are invalid, whichever comes last.",
  simple(`{"version":"runic.assets.archive/3","version":"runic.assets.archive/1","assets":[${index()},${a}]}`),
  invalid("manifest-not-json"));
add("manifest-duplicate-member-nested", "Two cacheMode members in one asset.",
  simple(v1([index(), replaceOnce(a, '"cacheMode":"Revalidate"', '"cacheMode":"Immutable","cacheMode":"Revalidate"')])),
  invalid("manifest-not-json"));
add("manifest-invalid-utf8", "The manifest is not UTF-8: an unknown member's value has a lone continuation byte.",
  new Zip().add("runic-assets.json", Buffer.concat([
    utf8(v1([index(), a]).slice(0, -1) + ',"note":"'), Buffer.from([0x80]), utf8('"}'),
  ])).add("assets/index.html", INDEX).add("assets/a.txt", A),
  invalid("manifest-not-json"));
add("not-a-zip", "Not a ZIP archive.", utf8("PK not really"), invalid("zip-invalid"));
add("encrypted-flag", "Every entry has the encryption flag (bit 0) although its data is plain.", (() => {
  const zip = simple(v1([index(), a]));
  zip.flags = 1;
  return zip;
})(), invalid("zip-encrypted"));
add("zip64-marker", "An entry count of 0xFFFF in the end record without a ZIP64 end record.", (() => {
  const zip = simple(v1([index(), a]));
  zip.zip64Marker = true;
  return zip;
})(), invalid("zip-invalid"));

add("leading-data-relative-offsets", "Bytes before the first entry, with offsets counted from the first entry instead of the start of the file.",
  (() => {
    const zip = simple(v1([index(), a]));
    zip.prefix = utf8("#!/bin/sh\nexit 0\n");
    zip.relativeOffsets = true;
    return zip;
  })(), invalid("zip-invalid"));
// a.txt with the entry options `options`, in the baseline archive.
const withA = options => new Zip().add("runic-assets.json", v1([index(), a])).add("assets/index.html", INDEX)
  .add("assets/a.txt", A, options);
add("compression-bzip2", "a.txt has compression method 12 (BZip2): readers read methods 0 and 8 only.",
  withA({ method: 12 }), invalid("zip-invalid"));
add("compression-deflate64", "a.txt has compression method 9 (Deflate64), whose stored block reads as deflate's: readers read methods 0 and 8 only.",
  withA({ method: 9, payload: storedBlock(A) }), invalid("zip-invalid"));
add("deflate-invalid", "a.txt's deflate data has the reserved block type 3, so it does not inflate.",
  withA({ method: 8, payload: Buffer.alloc(A.length, 0xff) }), invalid("zip-invalid"));
add("deflate-truncated", "a.txt's deflate stream ends inside its stored block.",
  withA({ method: 8, payload: storedBlock(A).subarray(0, 10) }), invalid("zip-invalid"));
add("deflate-short", "a.txt's complete deflate stream inflates to 12 bytes, whose CRC-32 and digest it records, not to its length 13.",
  new Zip().add("runic-assets.json", v1([index(), replaceOnce(a, sha(A), sha(A.subarray(0, 12)))])).add("assets/index.html", INDEX)
    .add("assets/a.txt", A, { method: 8, payload: storedBlock(A.subarray(0, 12)), crc: crc32(A.subarray(0, 12)) }),
  invalid("length-mismatch"));
add("deflate-long", "a.txt's complete deflate stream inflates to 14 bytes: its 13 bytes, which its CRC-32 and digest match, and one more.",
  withA({ method: 8, payload: storedBlock(Buffer.concat([A, utf8("!")])) }), invalid("length-mismatch"));
add("stored-size-short", "a.txt is stored with 12 bytes of data, whose CRC-32 and digest it records, and an uncompressed size of 13, its length.",
  new Zip().add("runic-assets.json", v1([index(), replaceOnce(a, sha(A), sha(A.subarray(0, 12)))])).add("assets/index.html", INDEX)
    .add("assets/a.txt", A, { payload: A.subarray(0, 12), crc: crc32(A.subarray(0, 12)) }),
  invalid("length-mismatch"));
add("local-name-differs", "a.txt's local header names assets/b.txt, not the central record's assets/a.txt.",
  withA({ localName: "assets/b.txt" }), invalid("zip-invalid"));
add("local-method-differs", "a.txt is stored (method 0) in its central record, and its local header says method 8.",
  withA({ localMethod: 8 }), invalid("zip-invalid"));
add("entries-overlap", "c.txt's local header and data lie inside a.txt's data, which holds them: two entries' bytes overlap.", (() => {
  const inner = localRecord("assets/c.txt", C);
  return new Zip().add("runic-assets.json", v1([index(), asset("a.txt", inner, false), asset("c.txt", C, false)]))
    .add("assets/index.html", INDEX).add("assets/a.txt", inner).add("assets/c.txt", C, { within: "assets/a.txt" });
})(), invalid("zip-invalid"));
add("length-negative", "A negative length.", simple(v1([index(), replaceOnce(a, '"length":13', '"length":-13')])),
  invalid("asset-invalid"));
add("path-no-break-space", "A path that starts with U+00A0, which is white space.",
  new Zip().add("runic-assets.json", v1([index(), asset("\u00a0a.txt", A, false)])).add("assets/index.html", INDEX)
    .add("assets/\u00a0a.txt", A),
  invalid("path-invalid"));
add("media-type-ideographic-space", "A media type that ends in U+3000, which is white space.",
  simple(v1([index(), replaceOnce(a, "text/plain", "text/plain\u3000")])), invalid("asset-invalid"));
add("manifest-lone-surrogate", "A path with a lone surrogate escape.",
  simple(v1([index(), replaceOnce(a, '"a.txt"', '"a\\ud800.txt"')])), invalid("manifest-not-json"));
add("manifest-lone-surrogate-ignored-name", "The name of a member that readers ignore has a lone surrogate escape.",
  simple(v1([index(), replaceOnce(a, '"cacheMode"', '"x\\udc00":1,"cacheMode"')])), invalid("manifest-not-json"));
add("manifest-lone-surrogate-ignored-value", "The value of a member that readers ignore has a lone surrogate escape.",
  simple(v1([index(), replaceOnce(a, '"cacheMode"', '"note":"\\ud800","cacheMode"')])), invalid("manifest-not-json"));

// The packer's archives. What a reader reports is their manifest's metadata, once this script has inflated every entry
// and checked its CRC-32, its length and its digest against the manifest itself.
const unzip = bytes => {
  const end = bytes.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  const entries = new Map();
  let central = bytes.readUInt32LE(end + 16);
  for (let index = 0; index < bytes.readUInt16LE(end + 10); index++) {
    if (bytes.readUInt32LE(central) !== 0x02014b50) throw new Error("not a central directory record");
    const [method, crc, size, nameLength] = [10, 16, 20, 28].map(offset =>
      offset === 16 || offset === 20 ? bytes.readUInt32LE(central + offset) : bytes.readUInt16LE(central + offset));
    const name = bytes.subarray(central + 46, central + 46 + nameLength).toString("utf8");
    const local = bytes.readUInt32LE(central + 42);
    const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28);
    const data = bytes.subarray(start, start + size);
    const content = method === 0 ? Buffer.from(data) : method === 8 ? inflateRawSync(data) : null;
    if (!content || crc32(content) !== crc) throw new Error(`${name}: method ${method} or CRC-32`);
    entries.set(name, content);
    central += 46 + nameLength + bytes.readUInt16LE(central + 30) + bytes.readUInt16LE(central + 32);
  }
  return entries;
};
const packerDescriptions = {
  basic: "Schema 1 written by the packer: deflated and stored entries, an empty file, a non-ASCII path and nested folders.",
  shared: "Schema 2 written by the packer: b.txt has the content of a.txt, stored once.",
};
const packer = Object.entries(packerDescriptions).map(([name, description]) => {
  const bytes = readFileSync(join(here, "packer", `${name}.zip`));
  const entries = unzip(bytes);
  const { assets } = JSON.parse(entries.get("runic-assets.json").toString("utf8"));
  return {
    name,
    description,
    bytes,
    expect: valid(assets.map(declared => {
      const content = entries.get(`assets/${declared.contentPath ?? declared.path}`);
      if (content.length !== declared.length || sha(content) !== declared.sha256)
        throw new Error(`packer/${name}.zip: ${declared.path} does not match its manifest`);
      return described(declared.path, content, declared.entryPoint, declared.mediaType, declared.cacheMode);
    })),
  };
});

// One member per line, each value on one line, as the other corpora's files read.
const write = (id, description, cases) => {
  const lines = cases.map(entry => {
    const members = [
      ["id", `${id}.${entry.name}`],
      ["description", entry.description],
      ["operation", "read"],
      ["input", { encoding: "base64", value: entry.bytes.toString("base64") }],
      ["expect", entry.expect],
      ...(entry.expectLazy ? [["expectLazy", entry.expectLazy]] : []),
    ];
    return `    {\n${members.map(([key, value]) => `      ${JSON.stringify(key)}: ${JSON.stringify(value)}`).join(",\n")}\n    }`;
  });
  return `{
  "format": "runic.assets.vector/1",
  "id": ${JSON.stringify(id)},
  "contract": {"id":"runic.assets.archive","versions":[1,2]},
  "description": ${JSON.stringify(description)},
  "cases": [
${lines.join(",\n")}
  ]
}
`;
};

const outputs = {
  "crafted.json": write("assets.crafted",
    "Archives built byte by byte by generate.mjs, with stored entries: valid ones, and invalid ones that each break one rule.",
    crafted),
  "packer.json": write("assets.packer", "Archives that the .NET packer (runic-sdk de807a135) wrote: packer/*.zip.", packer),
};

const check = process.argv.includes("--check");
let stale = [];
for (const [file, text] of Object.entries(outputs)) {
  const path = join(here, file);
  let current = null;
  try { current = readFileSync(path, "utf8"); } catch {}
  if (current === text) continue;
  if (check) stale.push(file);
  else writeFileSync(path, text);
}
if (stale.length) {
  console.error(`Stale: ${stale.join(", ")}. Run bun specs/assets/vectors/generate.mjs.`);
  process.exit(1);
}
console.log(`${check ? "Checked" : "Wrote"} ${crafted.length} crafted and ${packer.length} packer cases.`);
