// The C symbols the native server's shared library exports, against the committed list (R1 §5, "C ABI baseline"):
//
//   node eng/native/symbols.mjs check <library or directory>...   fails when a library exports a symbol the list does
//                                                                not have, or lacks one it has
//   node eng/native/symbols.mjs write <library>                  writes the list from a built library
//
// The list is native/runic-server/abi/rax_server.symbols, one name per line, sorted. A symbol only the library has is
// an unplanned export; one only the list has is a removed function, which needs a new RAX_SERVER_ABI_VERSION
// (include/rax_server.h) and the list updated. The exports are read from the file itself (ELF .dynsym, the Mach-O
// symbol table, the PE export directory), so one runner can check every runtime identifier's library, as each
// library job does for its own and the package job for all of them.
import { readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
export const listPath = "native/runic-server/abi/rax_server.symbols";
const shared = /^(lib)?rax_server\.(so|dylib|dll)$/;

function elf(data) {
  const wide = data[4] === 2;
  const little = data[5] === 1;
  const u16 = offset => little ? data.readUInt16LE(offset) : data.readUInt16BE(offset);
  const u32 = offset => little ? data.readUInt32LE(offset) : data.readUInt32BE(offset);
  const word = offset => wide ? Number(little ? data.readBigUInt64LE(offset) : data.readBigUInt64BE(offset)) : u32(offset);
  const sectionOffset = word(wide ? 0x28 : 0x20);
  const sectionSize = u16(wide ? 0x3a : 0x2e);
  const sectionCount = u16(wide ? 0x3c : 0x30);
  const section = index => {
    const at = sectionOffset + index * sectionSize;
    return {
      type: u32(at + 4),
      offset: word(at + (wide ? 0x18 : 0x10)),
      size: word(at + (wide ? 0x20 : 0x14)),
      link: u32(at + (wide ? 0x28 : 0x18)),
      entry: word(at + (wide ? 0x38 : 0x24)),
    };
  };
  const names = new Set();
  for (let index = 0; index < sectionCount; index++) {
    const symbols = section(index);
    if (symbols.type !== 11) continue; // SHT_DYNSYM
    const strings = section(symbols.link);
    for (let at = symbols.offset + symbols.entry; at < symbols.offset + symbols.size; at += symbols.entry) {
      const info = data[at + (wide ? 4 : 12)];
      const other = data[at + (wide ? 5 : 13)];
      const index = u16(at + (wide ? 6 : 14));
      const binding = info >> 4;
      const type = info & 0xf;
      const visible = (other & 3) === 0 || (other & 3) === 3; // default or protected
      // Defined, global, weak or unique, and neither a section nor a file symbol.
      if (index !== 0 && [1, 2, 10].includes(binding) && visible && type !== 3 && type !== 4)
        names.add(text(data, strings.offset + u32(at)));
    }
  }
  return names;
}

function macho(data, base = 0) {
  const magic = data.readUInt32BE(base);
  if (magic === 0xcafebabe) {
    // A universal file: the union of its architectures.
    const names = new Set();
    for (let index = 0; index < data.readUInt32BE(base + 4); index++)
      for (const name of macho(data, data.readUInt32BE(base + 8 + index * 20 + 8))) names.add(name);
    return names;
  }
  const wide = data.readUInt32LE(base) === 0xfeedfacf;
  const count = data.readUInt32LE(base + 16);
  let at = base + (wide ? 32 : 28);
  const names = new Set();
  for (let index = 0; index < count; index++) {
    const command = data.readUInt32LE(at);
    if (command === 0x2) { // LC_SYMTAB
      const symbols = base + data.readUInt32LE(at + 8);
      const strings = base + data.readUInt32LE(at + 16);
      const size = wide ? 16 : 12;
      for (let symbol = 0; symbol < data.readUInt32LE(at + 12); symbol++) {
        const entry = symbols + symbol * size;
        const type = data[entry + 4];
        // External, not private, not a debugging entry, and defined (N_UNDF is 0).
        if ((type & 0xe0) === 0 && (type & 0x01) && !(type & 0x10) && (type & 0x0e) !== 0)
          names.add(text(data, strings + data.readUInt32LE(entry)).replace(/^_/, ""));
      }
    }
    at += data.readUInt32LE(at + 4);
  }
  return names;
}

function pe(data) {
  const header = data.readUInt32LE(0x3c);
  const sectionCount = data.readUInt16LE(header + 6);
  const optional = header + 24;
  const optionalSize = data.readUInt16LE(header + 20);
  const directories = optional + (data.readUInt16LE(optional) === 0x20b ? 112 : 96);
  const exportRva = data.readUInt32LE(directories);
  const sections = Array.from({ length: sectionCount }, (_, index) => {
    const at = optional + optionalSize + index * 40;
    return {
      address: data.readUInt32LE(at + 12),
      size: Math.max(data.readUInt32LE(at + 8), data.readUInt32LE(at + 16)),
      raw: data.readUInt32LE(at + 20),
    };
  });
  const offset = rva => {
    const section = sections.find(item => rva >= item.address && rva < item.address + item.size);
    if (!section) throw new Error(`RVA 0x${rva.toString(16)} is in no section`);
    return rva - section.address + section.raw;
  };
  const names = new Set();
  if (exportRva === 0) return names;
  const directory = offset(exportRva);
  const nameTable = offset(data.readUInt32LE(directory + 32));
  for (let index = 0; index < data.readUInt32LE(directory + 24); index++)
    names.add(text(data, offset(data.readUInt32LE(nameTable + index * 4))));
  return names;
}

function text(data, at) {
  const end = data.indexOf(0, at);
  return data.toString("latin1", at, end);
}

// The symbols a shared library exports, sorted.
export function exported(path) {
  const data = readFileSync(path);
  let names;
  if (data.readUInt32BE(0) === 0x7f454c46) names = elf(data);
  else if ([0xfeedfacf, 0xfeedface].includes(data.readUInt32LE(0)) || data.readUInt32BE(0) === 0xcafebabe) names = macho(data);
  else if (data.readUInt16LE(0) === 0x5a4d) names = pe(data);
  else throw new Error(`${path} is neither an ELF, a Mach-O nor a PE file`);
  return [...names].sort();
}

// The names of a symbol list's text. A checkout may have CRLF line ends (Windows, before .gitattributes' eol=lf).
export const names = text => text.split(/\r?\n/).map(line => line.trim()).filter(line => line && !line.startsWith("#"));
export const listed = () => names(readFileSync(resolve(root, listPath), "utf8"));

// The differences between a library's exports and the list, as messages.
export function compare(library, exports, expected = listed()) {
  const actual = new Set(exports);
  const wanted = new Set(expected);
  return [
    ...exports.filter(name => !wanted.has(name)).map(name =>
      `${library} exports ${name}, which ${listPath} does not list: make it non-exported, or add it to the list and the header`),
    ...expected.filter(name => !actual.has(name)).map(name =>
      `${library} does not export ${name}: a removed function needs a new RAX_SERVER_ABI_VERSION and the list updated`),
  ];
}

function libraries(path) {
  if (!statSync(path).isDirectory()) return [path];
  return readdirSync(path, { recursive: true }).map(String).filter(file => shared.test(file.split(/[\\/]/).pop()))
    .map(file => join(path, file)).sort();
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [command, ...paths] = process.argv.slice(2);
  if (command === "write" && paths.length === 1) {
    const names = exported(paths[0]);
    writeFileSync(resolve(root, listPath), `${names.join("\n")}\n`);
    console.log(`${names.length} symbols written to ${listPath}`);
  } else if (command === "check" && paths.length) {
    const files = paths.flatMap(libraries);
    if (!files.length) {
      console.error(`No rax_server shared library under ${paths.join(", ")}`);
      process.exit(1);
    }
    const problems = files.flatMap(file => compare(file, exported(file)));
    if (problems.length) {
      for (const problem of problems) console.error(`::error title=Exported symbols::${problem}`);
      process.exit(1);
    }
    console.log(`${files.length} libraries export exactly the ${listed().length} symbols of ${listPath}: ${files.join(", ")}`);
  } else {
    console.error("Usage: node eng/native/symbols.mjs check <library or directory>... | write <library>");
    process.exit(2);
  }
}
