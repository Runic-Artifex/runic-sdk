import { copyFileSync, mkdirSync } from "node:fs";

const result = await Bun.build({
  entrypoints: ["src/app.ts"],
  outdir: "dist",
  target: "browser",
  format: "esm",
  naming: "app.js",
});
if (!result.success) {
  for (const log of result.logs) console.error(log);
  process.exitCode = 1;
} else {
  mkdirSync("dist", { recursive: true });
  copyFileSync("index.html", "dist/index.html");
}
