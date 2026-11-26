// The native server's C ABI in its four places: the committed symbol list that CI compares each built library with
// (eng/native/symbols.mjs), the header, the Rust functions that define it and the .NET P/Invokes that bind it.
import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { compare, listed, names } from "../../eng/native/symbols.mjs";

const root = resolve(import.meta.dir, "../..");
const read = path => readFileSync(resolve(root, path), "utf8");
const sorted = names => [...new Set(names)].sort();

test("the symbol list, the header and the Rust exports name the same functions", () => {
  const list = listed();
  expect(list).toEqual(sorted(list));
  expect(list.every(name => name.startsWith("rax_server_"))).toBe(true);
  const header = sorted([...read("native/runic-server/include/rax_server.h").matchAll(/^\w[\w\s*]*?\b(rax_server_\w+)\(/gm)]
    .map(match => match[1]));
  expect(header).toEqual(list);
  const ffi = read("native/runic-server/src/ffi.rs");
  const rust = sorted([...ffi.matchAll(/#\[unsafe\(no_mangle\)\]\s*pub (?:unsafe )?extern "C" fn (\w+)/g)].map(match => match[1]));
  expect(rust).toEqual(list);
});

test("the header's and .NET's ABI versions agree with the library's, and .NET binds only listed functions", () => {
  const version = /pub const ABI_VERSION: u32 = (\d+);/.exec(read("native/runic-server/src/ffi.rs"))[1];
  expect(/#define RAX_SERVER_ABI_VERSION (\d+)u/.exec(read("native/runic-server/include/rax_server.h"))[1]).toBe(version);
  const interop = read("packages/dotnet/Runic.Desktop/Internal/NativeServer/NativeServerInterop.cs");
  expect(/internal const uint AbiVersion = (\d+);/.exec(interop)[1]).toBe(version);
  expect(/internal const string Library = "(\w+)";/.exec(interop)[1]).toBe("rax_server");
  const bound = [...interop.matchAll(/EntryPoint = "(\w+)"/g)].map(match => match[1]);
  expect(bound.length).toBeGreaterThan(0);
  expect(bound.filter(name => !listed().includes(name))).toEqual([]);
});

test("a list with CRLF line ends, as a Windows checkout may have, reads the same names", () => {
  expect(names("rax_server_a\r\nrax_server_b\r\n")).toEqual(["rax_server_a", "rax_server_b"]);
  expect(compare("lib", ["rax_server_a", "rax_server_b"], names("rax_server_a\r\nrax_server_b\r\n"))).toEqual([]);
});

test("the check reports an unlisted export and a missing one", () => {
  expect(compare("lib", ["a", "b"], ["a", "b"])).toEqual([]);
  const problems = compare("lib", ["a", "extra"], ["a", "b"]);
  expect(problems).toHaveLength(2);
  expect(problems[0]).toContain("exports extra");
  expect(problems[1]).toContain("does not export b");
});
