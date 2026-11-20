import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";
import { root } from "../../eng/run.mjs";

// docs/environment.md lists every RUNIC_* name that shipped code reads or writes: the settings, the handshakes between
// Runic processes, and the names that are not environment variables (line prefixes, compile-time symbols), API-22 and
// API-27 of the 0.23 API review. This test scans the tracked sources of packages/ and tools/, which ship, for RUNIC_*
// literals and fails for a name the page does not mention, as tests/engineering/msbuild-properties.test.mjs does for
// docs/msbuild.md. Tests, test fixtures and documentation are not scanned; the repository's own build and test
// variables (eng/, tests/) are listed on the page by hand.
const page = readFileSync(resolve(root, "docs/environment.md"), "utf8");

// Names in shipped code that are neither variables nor line prefixes nor symbols that the page describes.
const notOnThePage = new Map([
  ["RUNIC_INSTANCE", "the Angular package's injection token, an exported identifier"],
]);

// The runic-app template's placeholders (__RUNIC_NPM_VERSION__ and the like), which `dotnet new` replaces.
function templatePlaceholders() {
  const template = JSON.parse(readFileSync(
    resolve(root, "tools/Runic.Application.Templates/content/runic-app/.template.config/template.json"), "utf8"));
  const names = new Set();
  for (const symbol of Object.values(template.symbols ?? {})) {
    for (const text of [symbol.replaces, symbol.parameters?.value]) {
      if (typeof text === "string") for (const [name] of text.matchAll(/RUNIC_[A-Z0-9_]*[A-Z0-9]/g)) names.add(name);
    }
  }
  return names;
}

function shippedSources() {
  return execFileSync("git", ["ls-files", "-z", "--", "packages", "tools"], { cwd: root, encoding: "utf8" })
    .split("\0")
    .filter(Boolean)
    .filter(file => !/(^|\/)(tests?|__tests__)\//.test(file) && !/\.(test|spec)\.[^/]+$/.test(file))
    .filter(file => !/\.(md|png|ico|icns|jpg|gif|webp|svg|woff2?|ttf|dll|so|dylib|nupkg|zip)$/i.test(file));
}

test("docs/environment.md names every RUNIC_* name that the shipped sources use", () => {
  const files = shippedSources();
  assert.ok(files.length > 500, `expected the shipped sources, found ${files.length}`);
  const placeholders = templatePlaceholders();
  assert.ok(placeholders.has("RUNIC_NPM_VERSION"), "expected the template's version placeholders");
  const uses = new Map();
  for (const file of files) {
    const text = readFileSync(resolve(root, file), "utf8");
    // The leading underscores of __RUNIC_DEVELOPMENT__ stay out of the name; the page's `__RUNIC_DEVELOPMENT__`
    // contains it.
    for (const [name] of text.matchAll(/(?<![A-Za-z0-9])RUNIC_[A-Z0-9_]*[A-Z0-9]/g)) {
      if (!uses.has(name)) uses.set(name, new Set());
      uses.get(name).add(file);
    }
  }
  assert.ok(uses.has("RUNIC_APPLICATION_SERVE_ONLY"), "expected the scan to find the documented names");
  const missing = [...uses]
    .filter(([name]) => !notOnThePage.has(name) && !placeholders.has(name))
    .filter(([name]) => !new RegExp(`\`_*${name}(?![A-Z0-9_]*[A-Z0-9])`).test(page))
    .map(([name, where]) => `${name} (${[...where].sort().slice(0, 3).join(", ")})`)
    .sort();
  assert.deepEqual(missing, [], "add these names to docs/environment.md, or to this test's notOnThePage with a reason");
});

test("the names this test leaves out are still in the shipped sources", () => {
  const files = shippedSources();
  const text = files.map(file => readFileSync(resolve(root, file), "utf8")).join("\n");
  for (const name of notOnThePage.keys()) assert.ok(text.includes(name), `${name} is gone; remove it from notOnThePage`);
});
