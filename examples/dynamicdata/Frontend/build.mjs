import { mkdir, copyFile } from "node:fs/promises";
import { resolve } from "node:path";

const output = resolve("dist");
await mkdir(output, { recursive: true });
await copyFile("index.html", resolve(output, "index.html"));
const result = await Bun.build({ entrypoints: ["main.ts"], outdir: output, target: "browser", format: "esm", minify: true });
if (!result.success) throw new AggregateError(result.logs, "DynamicData example did not build.");
