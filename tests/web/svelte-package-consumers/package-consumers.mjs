import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { access, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";

const execFile = promisify(execFileCallback);
const root = await mkdtemp(join(tmpdir(), "runic-svelte-package-"));
const suppliedArchives = process.argv.slice(2);
if (suppliedArchives.length !== 0 && suppliedArchives.length !== 3) {
  throw new Error("Usage: node test/package-consumers.mjs [<application-bridge.tgz> <svelte.tgz> <vite-plugin-runic.tgz>]");
}

try {
  if (suppliedArchives.length === 0) {
    await buildWorkspace("@runic-artifex/svelte");
  }
  const [applicationBridge, svelte, vite] = suppliedArchives.length === 3
    ? await Promise.all(suppliedArchives.map(archive))
    : await Promise.all([packDependency(), pack("@runic-artifex/svelte"), packFixture()]);
  await buildWorkspace("@runic-artifex/sveltekit");
  const sveltekit = await pack("@runic-artifex/sveltekit");
  const packages = [applicationBridge, svelte, sveltekit, vite];
  for (const packagePath of [svelte, sveltekit]) {
    const files = (await execFile("tar", ["-tf", packagePath])).stdout.split("\n").filter(Boolean);
    assert.equal(files.some((file) => file.startsWith("package/src/") || file.startsWith("package/test/")), false);
  }
  const bridgeManifest = JSON.parse((await execFile("tar", ["-xOf", applicationBridge, "package/package.json"])).stdout);
  assert.equal(bridgeManifest.name, "@runic-artifex/application-bridge");
  const svelteManifest = JSON.parse((await execFile("tar", ["-xOf", svelte, "package/package.json"])).stdout);
  assert.equal(svelteManifest.name, "@runic-artifex/svelte");
  const viteManifest = JSON.parse((await execFile("tar", ["-xOf", vite, "package/package.json"])).stdout);
  assert.equal(viteManifest.name, "@runic-artifex/vite-plugin-runic");
  assert.equal(svelteManifest.peerDependencies["@runic-artifex/vite-plugin-runic"], ">=1.0.0-preview.1 <2");
  assert.equal(svelteManifest.peerDependenciesMeta["@runic-artifex/vite-plugin-runic"].optional, true);
  const rootEntry = (await execFile("tar", ["-xOf", svelte, "package/dist/index.js"])).stdout;
  const translationsEntry = (await execFile("tar", ["-xOf", svelte, "package/dist/translations/index.js"])).stdout;
  assert.equal(rootEntry.includes("vite-plugin-runic"), false);
  assert.equal(translationsEntry.includes("vite-plugin-runic"), false);

  await writeFile(join(root, "package.json"), JSON.stringify({ private: true, type: "module" }), "utf8");
  // The Runic packages are supplied as explicit local archives. Their public peer
  // dependencies (such as Effect and Svelte) are deliberately resolved from npm:
  // a clean CI runner cannot assume its npm cache contains every peer package.
  await execFile("npm", [
    "install", "--ignore-scripts", "--no-audit", "--no-fund", "--package-lock=false", "--legacy-peer-deps",
    ...packages,
  ], { cwd: root });
  await writeFile(
    join(root, "consumer.mjs"),
    [
      'import { createViteApplicationBridgeObserver, disposeViteHmrResource, preserveViteHmrResource } from "@runic-artifex/svelte/vite";',
      'import { localizationStressCases, pluralStressCounts, visualAccessibilityStressScenarios } from "@runic-artifex/svelte/translations/testing";',
      'import { runicToolkitSpaPageOptions } from "@runic-artifex/sveltekit/page-options";',
      'import { defineApplicationContract } from "@runic-artifex/application-bridge";',
      'const observer = createViteApplicationBridgeObserver();',
      'observer.state({ connection: { state: "connected", transport: "consumer" } });',
      'const resource = preserveViteHmrResource("consumer", () => ({ disposed: false }));',
      'await disposeViteHmrResource("consumer", (value) => { value.disposed = true; });',
      'if (!resource.disposed || runicToolkitSpaPageOptions.ssr !== false || typeof defineApplicationContract !== "function" || localizationStressCases.length !== 3 || pluralStressCounts.at(-1) !== 1000 || visualAccessibilityStressScenarios.length !== 3) throw new Error("package boundary failed");',
    ].join("\n"),
    "utf8",
  );
  await execFile(process.execPath, ["consumer.mjs"], { cwd: root });
} finally {
  await rm(root, { recursive: true, force: true });
}

async function pack(workspace) {
  const packed = await execFile("npm", ["pack", "--json", "--workspace", workspace, "--pack-destination", root]);
  const [{ filename }] = JSON.parse(packed.stdout);
  return join(root, filename);
}

async function buildWorkspace(workspace) {
  await execFile("npm", ["run", "build", "--workspace", workspace]);
}

async function archive(path) {
  await access(path);
  return path;
}

async function packDependency() {
  const packed = await execFile("npm", ["pack", "--json", "--ignore-scripts", "--pack-destination", root], {
    cwd: "node_modules/@runic-artifex/application-bridge",
  });
  const [{ filename }] = JSON.parse(packed.stdout);
  return join(root, filename);
}

async function packFixture() {
  const packed = await execFile("npm", ["pack", "--json", "--pack-destination", root], {
    cwd: "test/fixtures/vite-plugin-runic",
  });
  const [{ filename }] = JSON.parse(packed.stdout);
  return join(root, filename);
}
