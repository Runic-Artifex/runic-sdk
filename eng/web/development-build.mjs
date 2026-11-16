// Writes a web package's two builds from one TypeScript compilation (packages/web/views/README.md, "Development build").
// A package's scripts/build.mjs calls it after `tsc -p tsconfig.build.json`, which emits dist/*.js and dist/*.d.ts:
//
// - dist/development/*.js, the `development` export condition (Vite's `vite dev`, the Angular CLI's `ng serve`):
//   tsc's output with `__RUNIC_DEVELOPMENT__` (the package's src/development.d.ts) set to `true`;
// - dist/*.js, every other condition: `__RUNIC_DEVELOPMENT__` is `false`, and esbuild removes the code it guards from
//   the files that use it (the other files are tsc's output). A module that no production entry reaches any more is
//   left out.
//
// Both builds share dist/*.d.ts: one public API. The build fails when the production build still contains one of the
// package's development markers, or when a shipped file of either build names the flag.
import { transform } from "esbuild";
import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, posix, relative } from "node:path";

export const developmentFlag = "__RUNIC_DEVELOPMENT__";

/**
 * Builds dist/development and the production dist of the package at `root` (a directory path). `markers` are strings
 * that only the development build may contain. Returns the modules only the development build has.
 */
export async function buildDevelopment({ root, markers }) {
  const dist = join(root, "dist");
  const development = join(dist, "development");
  const flag = developmentFlag;

  // The .js files below a directory; dist/development only when asked for.
  function files(directory, inDevelopment = false) {
    return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) return path === development && !inDevelopment ? [] : files(path, inDevelopment);
      return entry.name.endsWith(".js") ? [path] : [];
    });
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
    for (const marker of [flag, ...markers])
      if (code.includes(marker)) throw new Error(`The production build dist/${name} contains ${marker}.`);
    writeFileSync(join(dist, name), code);
  }
  // No shipped file of either build may name the flag: a bundler of an application does not define it, so a module that
  // still references it throws a ReferenceError when it runs (#958).
  for (const path of [...files(dist), ...files(development, true)]) {
    if (readFileSync(path, "utf8").includes(flag)) throw new Error(`${relative(root, path)} still references ${flag}.`);
  }
  const left = modules.filter(({ name }) => !reached.has(name)).map(({ name }) => name);
  console.log(`Built dist/development (${modules.length} modules) and dist (${reached.size}; development only: ${left.join(", ") || "none"}).`);
  return left;
}

// The relative modules a module imports or re-exports, statically or with `import()`.
function imports(code) {
  const found = new Set();
  for (const match of code.matchAll(/(?:\bfrom\s*|\bimport\s*\(?\s*)"(\.{1,2}\/[^"]+)"/g)) found.add(match[1]);
  return found;
}

// esbuild keeps the imports of the removed code, in full or as `import "./calls.js";`. The packages have no side effects
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
