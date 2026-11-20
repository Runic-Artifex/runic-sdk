// Stands in for a real frontend build: it requires the generated client in
// src/generated and writes the page and a nested asset to dist/.
import { mkdirSync, readdirSync, writeFileSync } from "node:fs";

const generated = readdirSync("src/generated").filter(name => name.endsWith(".ts"));
if (!generated.includes("notes.ts")) throw new Error(`src/generated has no notes.ts: ${generated.join(", ")}`);
mkdirSync("dist/assets", { recursive: true });
writeFileSync("dist/assets/app.js", "export {};\n");
writeFileSync("dist/index.html", "<!doctype html><title>Notes</title><script type=\"module\" src=\"assets/app.js\"></script>\n");
