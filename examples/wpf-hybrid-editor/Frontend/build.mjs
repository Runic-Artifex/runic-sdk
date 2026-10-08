import { copyFile, mkdir } from "node:fs/promises";

const result = await Bun.build({ entrypoints: ["src/app.ts"], outdir: "dist", target: "browser", format: "esm", naming: "app.js" });
if (!result.success) {
  for (const log of result.logs) console.error(log);
  process.exit(1);
}
await mkdir("dist", { recursive: true });
await copyFile("index.html", "dist/index.html");
