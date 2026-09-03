import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(fileURLToPath(new URL("..", import.meta.url)));
const generated = await readFile(resolve(root, "protocol/application-bridge/counter/generated/bridge.ir.json"), "utf8");
const templates = ["angular", "react", "svelte", "vue"];
const stale = [];
for (const template of templates) {
  const path = resolve(root, `templates/RunicToolkit.Templates/content/${template}/Contract/bridge.ir.json`);
  if (await readFile(path, "utf8") !== generated) stale.push(path.slice(root.length + 1));
}
if (stale.length > 0) throw new Error(`Application Bridge template artifacts are stale: ${stale.join(", ")}`);
