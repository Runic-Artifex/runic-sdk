import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import { basename, dirname } from "node:path";
import { fileURLToPath } from "node:url";

// Bundles the plain frontend with its Editor replaced by src/editor.ts, as the
// Effect variant does. The binding is the workspace source of @runic-artifex/vue;
// Vue itself resolves from that package's install, so there is one copy.
const frontend = fileURLToPath(new URL("../Frontend/", import.meta.url));
const editor = fileURLToPath(new URL("./src/editor.ts", import.meta.url));
const binding = fileURLToPath(new URL("../../../packages/web/vue/", import.meta.url));
const result = await Bun.build({
  entrypoints: [`${frontend}src/app.ts`],
  outdir: "dist",
  target: "browser",
  format: "esm",
  naming: "app.js",
  define: { "process.env.NODE_ENV": JSON.stringify("production") },
  plugins: [{
    name: "vue-editor",
    setup(build) {
      build.onResolve({ filter: /^\.\/editor\.js$/ }, ({ importer }) =>
        basename(importer) === "document.ts" && dirname(importer) === `${frontend}src` ? { path: editor } : undefined);
      build.onResolve({ filter: /^(vue)(\/.*)?$/ }, ({ path }) => ({ path: Bun.resolveSync(path, binding) }));
    },
  }],
});
if (!result.success) {
  for (const log of result.logs) console.error(log);
  process.exit(1);
}
await mkdir("dist", { recursive: true });
const html = await readFile(`${frontend}index.html`, "utf8");
await writeFile("dist/index.html", html.replace('src="/src/app.ts"', 'src="/app.js"').replace("Plain web component", "Vue component"));
await copyFile(`${frontend}public/runic-cswebui.js`, "dist/runic-cswebui.js");
