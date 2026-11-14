// Runs after ng-packagr (`bun run build`). The package is published from this directory, not from dist/, so the
// Angular Package Format output sits under dist/ (fesm2022/, types/) and package.json's `exports` point into it.
// This step checks that those exports match the manifest ng-packagr generated for dist/, then removes that manifest
// and the files ng-packagr copied next to it: the published archive has one package.json, one README and one LICENSE.
// ng-packagr warns that it overrides the `exports` conditions of package.json; that applies only to the dist/
// manifest this step removes.
import assert from "node:assert/strict";
import { readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const dist = join(root, "dist");
const manifest = JSON.parse(readFileSync(join(root, "package.json"), "utf8"));
const generated = JSON.parse(readFileSync(join(dist, "package.json"), "utf8"));

// ng-packagr keeps the conditions it does not set (such as an `import` taken from package.json) ahead of its own; the
// APF conditions are `types` and `default`.
const expected = {};
for (const [subpath, target] of Object.entries(generated.exports)) {
  if (subpath === "./package.json") continue;
  assert.ok(target.types && target.default, `ng-packagr generated no types or default condition for ${subpath}`);
  expected[subpath] = {
    types: `./dist/${target.types.replace(/^\.\//, "")}`,
    default: `./dist/${target.default.replace(/^\.\//, "")}`,
  };
}
expected["./package.json"] = "./package.json";
assert.deepEqual(manifest.exports, expected,
  `package.json "exports" must match the entry points ng-packagr built:\n${JSON.stringify(expected, null, 2)}`);
assert.equal(manifest.main, expected["."].default, `package.json "main" must be ${expected["."].default}`);
assert.equal(manifest.types, expected["."].types, `package.json "types" must be ${expected["."].types}`);

// ng-packagr adds tslib for `importHelpers`; tsconfig.json leaves it off, so the bundles import no helpers and the
// package keeps no runtime dependencies.
assert.deepEqual(Object.keys(generated.dependencies ?? {}).filter(name => name !== "tslib"), []);
assert.equal(manifest.dependencies, undefined);
for (const subpath of Object.keys(expected).filter(subpath => subpath !== "./package.json")) {
  const bundle = readFileSync(join(root, expected[subpath].default), "utf8");
  assert.doesNotMatch(bundle, /from\s*["']tslib["']/, `${expected[subpath].default} imports tslib`);
}

// The typings bundler imports a Views type that one module uses and the entry point re-exports twice, once renamed
// (`import { FormState, FormState as FormState$1 } from "@runic-artifex/views"`), and declarations then read
// `Signal<FormState$1>` in editors and the API reference. Both names are the same import, so use the plain one.
const renamed = /(?<![\w$])([A-Za-z_][\w]*)\$\d+(?![\w$])/g;
for (const file of readdirSync(join(dist, "types"))) {
  const path = join(dist, "types", file);
  const aliases = new Map();
  let text = readFileSync(path, "utf8").replace(/^import \{([^}]*)\} from ("[^"]+");$/gm, (statement, list, from) => {
    const specifiers = list.split(",").map(specifier => specifier.trim()).filter(Boolean);
    const plain = new Set(specifiers.filter(specifier => !specifier.includes(" as ")));
    const kept = specifiers.filter(specifier => {
      const [, name, alias] = /^(\S+) as (\S+)$/.exec(specifier) ?? [];
      if (!alias || !plain.has(name) || alias.replace(renamed, "$1") !== name) return true;
      aliases.set(alias, name);
      return false;
    });
    return `import { ${kept.join(", ")} } from ${from};`;
  });
  text = text.replace(renamed, (match, name) => aliases.get(match) ?? match);
  const left = [...new Set(text.match(renamed))];
  assert.deepEqual(left, [], `dist/types/${file} declares renamed names; give them a single name: ${left.join(", ")}`);
  writeFileSync(path, text);
}

// A secondary entry point's directory holds only its nested package.json, which only resolution without `exports`
// reads.
for (const subpath of Object.keys(generated.exports).filter(subpath => !["./package.json", "."].includes(subpath))) {
  assert.deepEqual(readdirSync(join(dist, subpath)), ["package.json"], `dist/${subpath.slice(2)} has more than its package.json`);
  rmSync(join(dist, subpath), { recursive: true });
}
for (const file of ["package.json", ".npmignore", "README.md", "LICENSE"]) rmSync(join(dist, file), { force: true });
