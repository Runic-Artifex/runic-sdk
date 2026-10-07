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
// The plain client row of the views-effect README: the generated Notes Editor
// used directly, with a declared-failure Save and an operation wait and cancel.
const editor = resolve(root, "examples/notes-view-first/Frontend/src/generated/editor.ts");
const usages = [
  ["Notes Editor: connect, subscribe, command, operation with wait timeout and cancel", `
    import { connectEditor } from ${JSON.stringify(editor)};
    const editor = await connectEditor(); editor.subscribe(state => console.log(state));
    console.log(await editor.save()); const op = await editor.startSave();
    console.log(await op.wait({ timeout: 1000 })); await op.cancel();`],
];

const results = [];
for (const [name, contents] of [
  ...cases.map(([name, path]) => [name, `export * from ${JSON.stringify(resolve(root, path))};`]),
  ...usages,
]) {
  const output = await build({
    stdin: { contents, resolveDir: root, loader: "ts" },
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
