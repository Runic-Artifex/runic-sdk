import { access, readFile } from "node:fs/promises";
import { resolve } from "node:path";

const framework = process.argv[2];
if (!framework) throw new Error("A framework name is required.");
const packageDirectory = process.cwd();
for (const file of [
  "dist/simple/index.html",
  "dist/advanced/index.html",
  "dist/webuitoolkit.assets.json",
]) {
  await access(resolve(packageDirectory, file));
}
const manifest = JSON.parse(await readFile(
  resolve(packageDirectory, "dist/webuitoolkit.assets.json"),
  "utf8",
));
if (manifest.schema !== "webuitoolkit.frontend-assets/1") {
  throw new Error("Generated frontend asset manifest has an unsupported schema.");
}
await access(resolve(packageDirectory, "dist", manifest.entrypoints.app));
await access(resolve(packageDirectory, "dist", manifest.entrypoints.styles));
const simple = await readFile(resolve(packageDirectory, "dist/simple/index.html"), "utf8");
const advanced = await readFile(resolve(packageDirectory, "dist/advanced/index.html"), "utf8");
if (!simple.includes(`Simple ToDo · ${framework}`) || !advanced.includes(`Advanced ToDo · ${framework}`)) {
  throw new Error("Generated Todo entry documents do not identify the selected framework.");
}
if (!simple.includes(`<base href="../">`) || !advanced.includes(`<base href="../">`)) {
  throw new Error("Generated Todo entry documents do not resolve root-owned toolkit assets.");
}
console.log(`${framework} Todo frontend build verified.`);
