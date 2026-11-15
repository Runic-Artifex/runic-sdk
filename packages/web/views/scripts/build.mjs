// Writes the package's two builds from one TypeScript compilation (README, "Development build"). Run after
// `tsc -p tsconfig.build.json`, which emits dist/*.js and dist/*.d.ts:
//
// - dist/development/*.js, the `development` export condition (Vite's `vite dev`, the Angular CLI's `ng serve`):
//   tsc's output with `__RUNIC_DEVELOPMENT__` (src/development.d.ts) set to `true`;
// - dist/*.js, every other condition: `__RUNIC_DEVELOPMENT__` is `false`, and esbuild removes the code it guards from
//   the files that use it (the other files are tsc's output). A module that no production entry reaches any more, such
//   as calls.js, is left out.
//
// Both builds share dist/*.d.ts: one public API. The script fails when the production build still contains a
// development tool.
//
//   bun scripts/build.mjs
import { transform } from "esbuild";
import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, posix, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const dist = join(root, "dist");
const development = join(dist, "development");
const flag = "__RUNIC_DEVELOPMENT__";
// What only the development build may contain: the Bridge-call channel and its async stack tasks (calls.ts), with the
// development tools' Views protocol features and trace context (calls.ts, #958), and the Runic Translations text
// channel (translated-text.ts).
const developmentMarkers = [flag, "runic.views.calls", "createTask", "runic.translations.text", "trace-context/1", "handler-timing/1", "traceparent"];

function files(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return path === development ? [] : files(path);
    return entry.name.endsWith(".js") ? [path] : [];
  });
}

// The relative modules a module imports or re-exports, statically or with `import()`.
function imports(code) {
  const found = new Set();
  for (const match of code.matchAll(/(?:\bfrom\s*|\bimport\s*\(?\s*)"(\.{1,2}\/[^"]+)"/g)) found.add(match[1]);
  return found;
}

rmSync(development, { recursive: true, force: true });
const modules = files(dist).map(path => ({ name: relative(dist, path).split("\\").join("/"), code: readFileSync(path, "utf8") }));
const production = new Map();
for (const { name, code } of modules) {
  const target = join(development, name);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, code.replace(/\b__RUNIC_DEVELOPMENT__\b/g, "true"));
  if (!code.includes(flag)) { production.set(name, code); continue; }
  const result = await transform(code, {
    loader: "js", format: "esm", target: "es2022", platform: "neutral", sourcefile: name,
    define: { [flag]: "false" }, minifySyntax: true, treeShaking: true, legalComments: "none",
  });
  production.set(name, withoutUnusedImports(result.code));
}

// esbuild keeps the imports of the removed code, in full or as `import "./calls.js";`. The package has no side effects
// (`sideEffects: false`, and the source has no import for its effects only): an import binding that the module no
// longer names goes, and so does an import left without bindings.
function withoutUnusedImports(code) {
  const statement = /^import\s*(?:\{([^}]*)\}\s*from\s*)?"(\.{1,2}\/[^"]+)";\n/gm;
  const body = code.replace(statement, "");
  return code.replace(statement, (line, bindings, path) => {
    const used = (bindings ?? "").split(",").map(binding => binding.trim()).filter(Boolean).filter(binding => {
      const local = binding.split(/\s+as\s+/).pop();
      return new RegExp(`(?<![\\w$.])${local.replace(/\$/g, "\\$")}(?![\\w$])`).test(body);
    });
    return used.length === 0 ? "" : `import { ${used.join(", ")} } from "${path}";\n`;
  });
}

// The production entries, from the exports map; then every module they reach.
const manifest = JSON.parse(readFileSync(join(root, "package.json"), "utf8"));
const entries = Object.values(manifest.exports).flatMap(target =>
  typeof target === "object" && typeof target.default === "string" && target.default.startsWith("./dist/")
    ? [target.default.slice("./dist/".length)] : []);
const reached = new Set();
const pending = [...entries];
while (pending.length) {
  const name = pending.pop();
  if (reached.has(name)) continue;
  const code = production.get(name);
  if (code === undefined) throw new Error(`dist/${name} is not a built module.`);
  reached.add(name);
  for (const path of imports(code)) pending.push(posix.normalize(posix.join(posix.dirname(name), path)));
}
for (const [name, code] of production) {
  if (!reached.has(name)) { rmSync(join(dist, name)); continue; }
  for (const marker of developmentMarkers)
    if (code.includes(marker)) throw new Error(`The production build dist/${name} contains ${marker}.`);
  writeFileSync(join(dist, name), code);
}
const left = modules.filter(({ name }) => !reached.has(name)).map(({ name }) => name);
console.log(`Built dist/development (${modules.length} modules) and dist (${reached.size}; development only: ${left.join(", ") || "none"}).`);
