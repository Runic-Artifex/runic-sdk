import assert from "node:assert/strict";
import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { dirname, relative } from "node:path";
import { fileURLToPath } from "node:url";

const output = new URL("../.svelte-kit/output/", import.meta.url);
const metricsPath = new URL("../artifacts/bundle-metrics.json", import.meta.url);
const scripts = [];
await collectScripts(output, scripts);

const combined = scripts.map((script) => script.text).join("\n");
assert.doesNotMatch(combined, /RUNICTRANSLATIONS_UNUSED_MESSAGE_MUST_NOT_SHIP/);

const client = scripts.filter((script) => script.path.includes("/client/"));
const server = scripts.filter((script) => script.path.includes("/server/"));
const metrics = {
  schema: "runic-translations-bundle-metrics/1",
  clientJavaScriptBytes: sum(client),
  clientJavaScriptFiles: client.length,
  serverJavaScriptBytes: sum(server),
  serverJavaScriptFiles: server.length,
  unusedMessageRemoved: true,
};

assert.ok(metrics.clientJavaScriptBytes > 0, "The SvelteKit client build contains JavaScript.");
assert.ok(metrics.clientJavaScriptBytes < 150_000, "The reference client exceeded its 150 kB JavaScript budget.");
await mkdir(dirname(fileURLToPath(metricsPath)), { recursive: true });
await writeFile(metricsPath, `${JSON.stringify(metrics, null, 2)}\n`, "utf8");
console.log(JSON.stringify(metrics));

async function collectScripts(directory, result) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      await collectScripts(child, result);
    } else if (entry.name.endsWith(".js")) {
      const text = await readFile(child, "utf8");
      result.push({
        path: `/${relative(fileURLToPath(output), fileURLToPath(child)).replaceAll("\\", "/")}`,
        bytes: Buffer.byteLength(text),
        text,
      });
    }
  }
}

function sum(entries) {
  return entries.reduce((total, entry) => total + entry.bytes, 0);
}
