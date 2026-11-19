// sdk#1207: checks that a Windows executable or assembly carries the application icon that Runic.Platform's targets
// derive from the package icon, as its first icon group (RT_GROUP_ICON) with the generated sizes. Reads the PE file's
// resource directory itself, so it runs on any operating system:
//
//   bun eng/ci/windows-icon-check.mjs <file.exe|file.dll>
import { readFileSync } from "node:fs";

const expected = [16, 20, 24, 32, 40, 48, 64, 256];

/** The sizes of the first icon group of a PE file, or null when it has none. */
export function iconGroupSizes(image) {
  const pe = image.readUInt32LE(0x3c);
  if (image.toString("latin1", pe, pe + 4) !== "PE\0\0") throw new Error("not a PE file");
  const sections = image.readUInt16LE(pe + 6);
  const optional = pe + 24;
  const magic = image.readUInt16LE(optional);
  // The data directories follow the PE32 or PE32+ optional header; the resource table is the third.
  const directories = optional + (magic === 0x20b ? 112 : 96);
  const resourceRva = image.readUInt32LE(directories + 2 * 8);
  if (resourceRva === 0) return null;
  const sectionTable = optional + image.readUInt16LE(pe + 20);
  const fileOffset = rva => {
    for (let i = 0; i < sections; i++) {
      const section = sectionTable + i * 40;
      const virtualAddress = image.readUInt32LE(section + 12), size = Math.max(image.readUInt32LE(section + 8), image.readUInt32LE(section + 16));
      if (rva >= virtualAddress && rva < virtualAddress + size) return rva - virtualAddress + image.readUInt32LE(section + 20);
    }
    throw new Error(`RVA ${rva} is in no section`);
  };
  const base = fileOffset(resourceRva);
  // IMAGE_RESOURCE_DIRECTORY: named and ID entry counts, then 8-byte entries; the high bit marks a subdirectory.
  const entries = at => {
    const count = image.readUInt16LE(base + at + 12) + image.readUInt16LE(base + at + 14);
    return Array.from({ length: count }, (_, i) => ({
      name: image.readUInt32LE(base + at + 16 + i * 8),
      offset: image.readUInt32LE(base + at + 20 + i * 8),
    }));
  };
  const group = entries(0).find(entry => entry.name === 14);
  if (!group) return null;
  const name = entries(group.offset & 0x7fffffff)[0];
  const language = entries(name.offset & 0x7fffffff)[0];
  const data = fileOffset(image.readUInt32LE(base + language.offset));
  // GRPICONDIR: reserved, type 1, count, then 14-byte entries that start with the width (0 for 256).
  const count = image.readUInt16LE(data + 4);
  return Array.from({ length: count }, (_, i) => image[data + 6 + i * 14] || 256);
}

if (import.meta.main) {
  const file = process.argv[2];
  const sizes = iconGroupSizes(readFileSync(file));
  if (sizes?.join(",") !== expected.join(",")) {
    console.error(`${file}: the first icon group has ${sizes ? sizes.join(", ") + " px" : "no icon"}; expected ${expected.join(", ")} px.`);
    process.exit(1);
  }
  console.log(`${file}: the application icon, ${sizes.join(", ")} px.`);
}
