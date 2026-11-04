import { copyFileSync, mkdirSync } from "node:fs";

// One page per Window type: the project, a document and the "choose account" dialog.
const result = await Bun.build({
  entrypoints: ["src/app.ts", "src/document.ts", "src/account.ts"],
  outdir: "dist",
  target: "browser",
  format: "esm",
  naming: "[name].js",
});
if (!result.success) {
  for (const log of result.logs) console.error(log);
  process.exitCode = 1;
} else {
  mkdirSync("dist", { recursive: true });
  for (const page of ["index.html", "document.html", "account.html"]) copyFileSync(page, `dist/${page}`);
}
