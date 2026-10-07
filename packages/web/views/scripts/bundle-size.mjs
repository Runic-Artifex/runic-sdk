// Reports the minified and gzip size of checked-in generated example clients
// bundled against this package's dist output. Run `bun run build` first.
//
//   bun scripts/bundle-size.mjs [--json]
//
// Each case bundles one generated module with every export kept, as an
// application that imports its connect function and page references does.
import { build } from "esbuild";
import { gzipSync } from "node:zlib";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

const root = fileURLToPath(new URL("../../../../", import.meta.url));
const cases = [
  ["Counter: state, checked field, command (first-window)", "examples/first-window/Frontend/src/generated/counter.ts"],
  ["Editor: operations, interactions (notes-reactive-views)", "examples/notes-reactive-views/Frontend/src/generated/editor.ts"],
  ["Rows: keyed collection, operations (dynamicdata)", "examples/dynamicdata/Frontend/src/generated/rows.ts"],
];

const results = [];
for (const [name, path] of cases) {
  const output = await build({
    stdin: { contents: `export * from ${JSON.stringify(resolve(root, path))};`, resolveDir: root, loader: "ts" },
    bundle: true, minify: true, format: "esm", target: "es2023", platform: "browser", write: false, logLevel: "silent",
    nodePaths: [resolve(root, "node_modules")],
  });
  const code = output.outputFiles[0].contents;
  results.push({ name, minified: code.length, gzip: gzipSync(code, { level: 9 }).length });
}

if (process.argv.includes("--json")) console.log(JSON.stringify(results, null, 2));
else {
  console.log("| Bundle | minified bytes | gzip bytes |\n| --- | ---: | ---: |");
  for (const { name, minified, gzip } of results) console.log(`| ${name} | ${minified.toLocaleString("en")} | ${gzip.toLocaleString("en")} |`);
}
