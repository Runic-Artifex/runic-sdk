import { copyFile, readFile, writeFile } from "node:fs/promises";
const html = await readFile("index.html", "utf8");
await writeFile("dist/index.html", html.replace('src="/src/app.ts"', 'src="/app.js"'));
await copyFile("public/runic-cswebui.js", "dist/runic-cswebui.js");
