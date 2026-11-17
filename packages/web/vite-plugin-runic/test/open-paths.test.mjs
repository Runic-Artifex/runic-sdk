// The paths of /__runic/open's frontend locations on Windows and POSIX, judged on any platform with `path.win32` and
// `path.posix` (WIN-02, WIN-03): Vite and Runic Inspect name Windows files with `/` (`C:/p/src/App.vue`), Vite's
// `isFileLoadingAllowed` compares that form, and only absolute drive or POSIX paths without `.`/`..` segments, UNC,
// device or stream paths reach it. The component entry's module ID is Vite's form too.
import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import { canonicalFrontendFile, frontendLocation } from "../dist/sources.js";
import { fsPath, resolveFrame, sourceFile } from "../dist/inspect-frameworks.js";
import { componentEntryId, componentHooks } from "../dist/component.js";

// Vite's check on Windows, roughly: below an allowed directory in Vite's form, without regard to case.
const below = (directory) => (file) => file.toLowerCase().startsWith(`${directory.toLowerCase()}/`);

test("Windows: a frontend file is accepted with either separator and opened in Windows' form", () => {
  const judged = [];
  const accepts = (file) => { judged.push(file); return below("C:/p/Frontend")(file); };
  for (const file of ["C:/p/Frontend/src/App.vue", "C:\\p\\Frontend\\src\\App.vue", "C:/p/Frontend\\src/App.vue"]) {
    assert.deepEqual(frontendLocation({ file, line: 3 }, accepts, path.win32),
      { file: "C:\\p\\Frontend\\src\\App.vue", line: 3, column: 1 }, file);
  }
  assert.deepEqual(judged, Array(3).fill("C:/p/Frontend/src/App.vue"), "Vite judges its own form");
  // Case is the file system's, as Vite compares it on Windows; a lower-case drive, as some stack frames name it.
  assert.deepEqual(frontendLocation({ file: "c:/P/frontend/src/App.svelte", line: 1, column: 9 }, accepts, path.win32),
    { file: "c:\\P\\frontend\\src\\App.svelte", line: 1, column: 9 });
  assert.deepEqual(canonicalFrontendFile("D:/repo/packages/ui/Button.tsx", path.win32),
    { file: "D:\\repo\\packages\\ui\\Button.tsx", vite: "D:/repo/packages/ui/Button.tsx" });
});

test("Windows: nothing outside what Vite's check judges is opened", () => {
  const accepts = below("C:/p/Frontend");
  const everything = () => true; // server.fs.strict: false
  for (const file of [
    // Another drive, or outside the allowed directory.
    "D:/p/Frontend/src/App.vue", "C:/p/secret.txt",
    // `..` and `.` segments with either separator: refused before Vite's check, which expects normalized paths.
    "C:/p/Frontend/src/../../secret.txt", "C:\\p\\Frontend\\src\\..\\..\\secret.txt", "C:/p/Frontend\\..\\secret.txt",
    "C:/p/Frontend/./src/App.vue", "C:/p/Frontend//src/App.vue", "C:/p/Frontend/src/",
  ]) {
    assert.equal(frontendLocation({ file, line: 1 }, accepts, path.win32), undefined, file);
  }
  for (const file of [
    // UNC and device paths, which Windows would open over the network or as a device.
    "\\\\server\\share\\Frontend\\src\\App.vue", "//server/share/Frontend/src/App.vue", "\\\\server/share\\x.ts",
    "\\\\?\\C:\\p\\Frontend\\src\\App.vue", "\\\\.\\C:\\p\\Frontend\\src\\App.vue", "//?/C:/p/Frontend/src/App.vue",
    // Rooted without a drive, drive-relative, and the `/C:/…` that a `/@fs/` URL path left before WIN-03.
    "\\p\\Frontend\\src\\App.vue", "/p/Frontend/src/App.vue", "C:p\\Frontend\\src\\App.vue", "/C:/p/Frontend/src/App.vue",
    // Relative, an alternate data stream, segments that Windows trims, NUL.
    "src/App.vue", "C:/p/Frontend/src/App.vue:secret", "C:/p/Frontend/src/App.vue.", "C:/p/Frontend/src /App.vue",
    "C:/p/Frontend/src\0/App.vue", "C:/p/Frontend/.../x.ts", "",
  ]) {
    assert.equal(canonicalFrontendFile(file, path.win32), undefined, JSON.stringify(file));
    assert.equal(frontendLocation({ file, line: 1 }, everything, path.win32), undefined, JSON.stringify(file));
  }
  for (const message of [{ file: "C:/p/Frontend/src/App.vue", line: 0 }, { file: "C:/p/Frontend/src/App.vue", line: 1, column: 0 },
    { file: "C:/p/Frontend/src/App.vue", line: 1, id: "x" }, { file: 1, line: 1 }, ["C:/p/Frontend/src/App.vue"], null]) {
    assert.equal(frontendLocation(message, everything, path.win32), undefined, JSON.stringify(message));
  }
});

test("POSIX: absolute paths without `.` or `..` segments, as before", () => {
  const accepts = (file) => file.startsWith("/p/Frontend/");
  assert.deepEqual(frontendLocation({ file: "/p/Frontend/src/App.vue", line: 2, column: 4 }, accepts, path.posix),
    { file: "/p/Frontend/src/App.vue", line: 2, column: 4 });
  for (const file of ["/p/Frontend/src/../../secret.txt", "/p/Frontend/./src/App.vue", "/p/Frontend//src/App.vue",
    "src/App.vue", "C:/p/Frontend/src/App.vue", "C:\\p\\Frontend\\src\\App.vue", "/p/Frontend/src/App.vue\0"]) {
    assert.equal(frontendLocation({ file, line: 1 }, () => true, path.posix), undefined, JSON.stringify(file));
  }
});

test("/@fs/ URL paths with a drive are Windows files, as Vite's fsPathFromId reads them", async () => {
  assert.equal(fsPath("/@fs/C:/repo/lib/B.tsx"), "C:/repo/lib/B.tsx");
  assert.equal(fsPath("/@fs/c:/repo/lib/B.tsx"), "c:/repo/lib/B.tsx");
  assert.equal(fsPath("/@fs/repo/lib/B.tsx"), "/repo/lib/B.tsx");
  // A source map's source relative to a module served below /@fs/, and an absolute one.
  assert.equal(sourceFile("../../lib/B.tsx", "http://h/@fs/C:/repo/lib/x/A.tsx", "C:/p/Frontend"), "C:/repo/lib/B.tsx");
  assert.equal(sourceFile("/@fs/C:/repo/lib/B.tsx", "http://h/src/A.tsx", "C:/p/Frontend"), "C:/repo/lib/B.tsx");
  assert.equal(sourceFile("C:/p/Frontend/src/A.tsx", "http://h/src/A.tsx", "C:/p/Frontend"), "C:/p/Frontend/src/A.tsx");
  // A frame without a source map keeps the served file.
  const frame = await resolveFrame({ name: "B", url: "http://h/@fs/C:/repo/lib/B.tsx", line: 4, column: 2 }, "C:/p/Frontend", async () => undefined);
  assert.deepEqual(frame, { file: "C:/repo/lib/B.tsx", line: 4, column: 2, editor: "vite" });
  // And /__runic/open opens it on Windows.
  assert.deepEqual(frontendLocation({ file: frame.file, line: frame.line, column: frame.column }, below("C:/repo"), path.win32),
    { file: "C:\\repo\\lib\\B.tsx", line: 4, column: 2 });
});

test("the component entry's module ID is the file as Vite names it, with `/` on Windows", () => {
  // Vite's root is `C:/p/Frontend`; a configuration may name the entry with either separator, or absolutely.
  for (const [entry, root] of [["src/component.ts", "C:/p/Frontend"], ["src\\component.ts", "C:\\p\\Frontend"],
    ["./src/../src/component.ts", "C:/p/Frontend"], ["C:\\p\\Frontend\\src\\component.ts", "C:/elsewhere"],
    ["C:/p/Frontend/src/component.ts", "C:/elsewhere"]]) {
    assert.equal(componentEntryId(entry, root, path.win32), "C:/p/Frontend/src/component.ts", `${entry} in ${root}`);
  }
  assert.equal(componentEntryId("src/component.ts", "/p/Frontend", path.posix), "/p/Frontend/src/component.ts");
  assert.equal(componentEntryId("/p/Frontend/src/component.ts", "/elsewhere", path.posix), "/p/Frontend/src/component.ts");
  // The generated entry module imports it by that ID.
  const hooks = componentHooks({ entry: "src/component.ts" }, () => "/p/Frontend");
  assert.match(hooks.load(hooks.resolveId("virtual:runic/component-entry")), /^import mount from "\/p\/Frontend\/src\/component\.ts";$/mu);
});
