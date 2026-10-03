import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";

// Generated clients import the shared @runic-artifex/views runtime, so the
// browser loads one bundled module.
const result = await Bun.build({
  entrypoints: ["src/app.ts"],
  outdir: "dist",
  target: "browser",
  format: "esm",
  naming: "app.js",
});
if (!result.success) {
  for (const log of result.logs) console.error(log);
  process.exit(1);
}
await mkdir("dist", { recursive: true });
const html = await readFile("index.html", "utf8");
await writeFile("dist/index.html", html.replace('src="/src/app.ts"', 'src="/app.js"'));
await copyFile("public/runic-cswebui.js", "dist/runic-cswebui.js");
