import { nodeCompatibility } from "../../../eng/node-compatibility.mjs";
import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";

const execute = promisify(execFileCallback);
const execFile = (command, args, options) => {
  const compatibility = nodeCompatibility();
  return execute(command === process.execPath ? compatibility.executable : command, args,
    { ...options, env: { ...compatibility.env, ...options?.env } });
};
const root = await mkdtemp(join(tmpdir(), "runic-svelte-package-"));
try {
  for (const workspace of ["@runic-artifex/svelte", "@runic-artifex/sveltekit", "@runic-artifex/vite-plugin-runic"]) {
    await execFile("npm", ["run", "build", "--workspace", workspace]);
  }
  const archives = await Promise.all([
    "@runic-artifex/svelte",
    "@runic-artifex/sveltekit",
    "@runic-artifex/vite-plugin-runic",
  ].map(pack));
  for (const archive of archives) {
    const files = (await execFile("tar", ["-tf", archive])).stdout.split("\n").filter(Boolean);
    assert.equal(files.some((file) => file.startsWith("package/src/") || file.startsWith("package/test/")), false);
  }
  const [svelte] = archives;
  const viewFiles = (await execFile("tar", ["-tf", svelte])).stdout.split("\n");
  assert.ok(viewFiles.includes("package/dist/views/ViewOutlet.svelte"));
  assert.ok(viewFiles.includes("package/dist/views/ViewOutlet.svelte.d.ts"));
  assert.ok(viewFiles.includes("package/dist/views/view-registry.d.ts"));
  const svelteManifest = JSON.parse((await execFile("tar", ["-xOf", svelte, "package/package.json"])).stdout);
  assert.equal(svelteManifest.name, "@runic-artifex/svelte");
  assert.ok(Object.keys(svelteManifest.exports).includes("./views"));
  await writeFile(join(root, "package.json"), JSON.stringify({ private: true, type: "module" }), "utf8");
  await execFile("npm", [
    "install", "--ignore-scripts", "--no-audit", "--no-fund", "--package-lock=false", "--legacy-peer-deps",
    ...archives,
  ], { cwd: root });
  await writeFile(join(root, "consumer.mjs"), [
    'import { localizationStressCases, pluralStressCounts, visualAccessibilityStressScenarios } from "@runic-artifex/svelte/translations/testing";',
    'import { runicToolkitSpaPageOptions } from "@runic-artifex/sveltekit/page-options";',
    'import { preserveRunicHmrResource, disposeRunicHmrResource } from "@runic-artifex/vite-plugin-runic/client";',
    'const resource = preserveRunicHmrResource("consumer", () => ({ disposed: false }));',
    'await disposeRunicHmrResource("consumer", (value) => { value.disposed = true; });',
    'if (!resource.disposed || runicToolkitSpaPageOptions.ssr !== false || localizationStressCases.length !== 3 || pluralStressCounts.at(-1) !== 1000 || visualAccessibilityStressScenarios.length !== 3) throw new Error("package boundary failed");',
  ].join("\n"), "utf8");
  await execFile(process.execPath, ["consumer.mjs"], { cwd: root });
} finally {
  await rm(root, { recursive: true, force: true });
}

async function pack(workspace) {
  const packed = await execFile("npm", ["pack", "--json", "--workspace", workspace, "--pack-destination", root]);
  const result = JSON.parse(packed.stdout);
  const { filename } = Array.isArray(result) ? result[0] : Object.values(result)[0];
  return join(root, filename);
}
