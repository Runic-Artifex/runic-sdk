// Reads NuGet packages (zip archives) without external tools.
import { readFileSync } from "node:fs";
import { inflateRawSync } from "node:zlib";

// Returns the archive's entries, with names as stored and a reader for each.
export function zipEntries(file) {
  const data = readFileSync(file);
  let end = data.length - 22;
  while (end >= 0 && data.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0) throw new Error(`${file} is not a zip archive`);
  const count = data.readUInt16LE(end + 10);
  let offset = data.readUInt32LE(end + 16);
  const entries = [];
  for (let index = 0; index < count; index++) {
    if (data.readUInt32LE(offset) !== 0x02014b50) throw new Error(`${file} has a corrupt central directory`);
    const method = data.readUInt16LE(offset + 10);
    const compressedSize = data.readUInt32LE(offset + 20);
    const nameLength = data.readUInt16LE(offset + 28);
    const extraLength = data.readUInt16LE(offset + 30);
    const commentLength = data.readUInt16LE(offset + 32);
    const local = data.readUInt32LE(offset + 42);
    const name = data.toString("utf8", offset + 46, offset + 46 + nameLength);
    entries.push({
      name,
      read() {
        const start = local + 30 + data.readUInt16LE(local + 26) + data.readUInt16LE(local + 28);
        const raw = data.subarray(start, start + compressedSize);
        if (method === 0) return Buffer.from(raw);
        if (method === 8) return inflateRawSync(raw);
        throw new Error(`${file}: ${name} uses unsupported compression ${method}`);
      },
    });
    offset += 46 + nameLength + extraLength + commentLength;
  }
  return entries;
}

// Package files, without the OPC metadata that NuGet adds to every package.
export function nupkgFiles(file) {
  return zipEntries(file).map(entry => decodeURIComponent(entry.name))
    .filter(name => name !== "[Content_Types].xml" && !name.startsWith("_rels/") && !name.startsWith("package/"))
    .sort();
}

function nuspecText(file) {
  const nuspec = zipEntries(file).find(entry => !entry.name.includes("/") && entry.name.endsWith(".nuspec"));
  if (!nuspec) throw new Error(`${file} has no nuspec`);
  return nuspec.read().toString("utf8");
}

// The package ids that the nuspec lists as dependencies, across all target frameworks.
export function nupkgDependencies(file) {
  return [...new Set([...nuspecText(file).matchAll(/<dependency\s+id="([^"]+)"/g)].map(([, id]) => id))].sort();
}

// The version ranges of each dependency id, across all target frameworks.
export function nupkgDependencyVersions(file) {
  const versions = new Map();
  for (const [element] of nuspecText(file).matchAll(/<dependency\s[^>]*>/g)) {
    const id = /\sid="([^"]+)"/.exec(element)?.[1];
    if (!id) continue;
    const version = /\sversion="([^"]*)"/.exec(element)?.[1] ?? "";
    versions.set(id, [...new Set([...(versions.get(id) ?? []), version])].sort());
  }
  return versions;
}
