import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";

const build = new URL("../build/", import.meta.url);
const manifest = JSON.parse(await readFile(new URL("runic-toolkit.sveltekit.json", build), "utf8"));
assert.deepEqual(manifest, {
  schema: "runic-toolkit.sveltekit/1",
  mode: "spa",
  entrypoint: "index.html",
  fallback: "index.html",
  routes: ["/", "/[...fallback]"],
});

const scripts = [];
await collectScripts(build, scripts);
assert.doesNotMatch(scripts.join("\n"), /vite-devtools|@vitejs\/devtools/);
console.log("SvelteKit native manifest and production DevTools exclusion passed.");

async function collectScripts(directory, output) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) await collectScripts(child, output);
    else if (entry.name.endsWith(".js")) output.push(await readFile(child, "utf8"));
  }
}
