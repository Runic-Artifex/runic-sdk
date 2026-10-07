import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import { basename, dirname } from "node:path";
import { fileURLToPath } from "node:url";

// Bundles the plain frontend with its Editor replaced by src/editor.ts, so this
// variant shares every other module and the plain bundle stays unchanged.
const frontend = fileURLToPath(new URL("../Frontend/", import.meta.url));
const editor = fileURLToPath(new URL("./src/editor.ts", import.meta.url));
const result = await Bun.build({
  entrypoints: [`${frontend}src/app.ts`],
  outdir: "dist",
  target: "browser",
  format: "esm",
  naming: "app.js",
  plugins: [{
    name: "effect-editor",
    setup(build) {
      build.onResolve({ filter: /^\.\/editor\.js$/ }, ({ importer }) =>
        basename(importer) === "document.ts" && dirname(importer) === `${frontend}src` ? { path: editor } : undefined);
    },
  }],
});
if (!result.success) {
  for (const log of result.logs) console.error(log);
  process.exit(1);
}
await mkdir("dist", { recursive: true });
const html = await readFile(`${frontend}index.html`, "utf8");
await writeFile("dist/index.html", html.replace('src="/src/app.ts"', 'src="/app.js"').replace("Plain web component", "Effect web component"));
await copyFile(`${frontend}public/runic-cswebui.js`, "dist/runic-cswebui.js");
