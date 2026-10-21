// Copies the pages to dist/. With --watch it stays running and copies a page again when it changes, as
// `vite build --watch` rebuilds into dist/ without serving it.
import { copyFileSync, mkdirSync, readdirSync, watch } from "node:fs";

const pages = readdirSync(".").filter(name => name.endsWith(".html"));
const copy = page => copyFileSync(page, `dist/${page}`);
mkdirSync("dist", { recursive: true });
pages.forEach(copy);
if (process.argv.includes("--watch")) {
  console.log(`Watching ${pages.join(", ")}`);
  watch(".", (_, name) => { if (pages.includes(name)) copy(name); });
}
