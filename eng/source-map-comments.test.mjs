import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { packageSourceMapProblems, straySourceMapComments } from "./source-map-comments.mjs";

// Assembled, so that this file is no example of what it tests.
const directive = (marker = "//#") => `${marker} source${"MappingURL"}=`;

test("a trailing source map comment is a file's own", () => {
  assert.deepEqual(straySourceMapComments(`export const a = 1;\n${directive()}a.js.map\n`), []);
  assert.deepEqual(straySourceMapComments(`export const a = 1;\n${directive("//@")}a.js.map\n\n  \n`), []);
  assert.deepEqual(straySourceMapComments(`export const a = 1;\n`), []);
});

test("a source map comment anywhere else is stray (#1050)", () => {
  // The doc comment that vite-plugin-runic 0.21.0's candidate shipped.
  assert.deepEqual(straySourceMapComments(`/** A module's map (\`${directive()}data:application/json;base64,…\`). */\nexport {};\n`), [1]);
  assert.deepEqual(straySourceMapComments(`const a = "${directive()}x";\n${directive()}a.js.map\n`), [1]);
  assert.deepEqual(straySourceMapComments(`export {};\n${directive("/*#")}a.js.map */\nexport const b = 1;\n`), [2]);
  assert.deepEqual(straySourceMapComments(`export {}; ${directive()}a.js.map\n`), [1]);
});

test("a package's shipped JavaScript is scanned, not its other files", () => {
  const directory = mkdtempSync(join(tmpdir(), "runic-source-map-comments-"));
  try {
    mkdirSync(join(directory, "dist", "nested"), { recursive: true });
    writeFileSync(join(directory, "dist", "index.js"), `export {};\n${directive()}index.js.map\n`);
    writeFileSync(join(directory, "dist", "nested", "bad.mjs"), `// ${directive()}x\nexport {};\n`);
    writeFileSync(join(directory, "dist", "index.d.ts"), `// ${directive()}x\nexport {};\n`);
    assert.deepEqual(packageSourceMapProblems(directory), [`${join(directory, "dist", "nested", "bad.mjs")}:1`]);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
