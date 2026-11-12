import { nodeCompatibility } from "../../../eng/node-compatibility.mjs";
import { npmReleaseAgeArguments } from "../../../eng/dependencies/release-age.mjs";
import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { readFileSync } from "node:fs";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";

const execute = promisify(execFileCallback);
const execFile = (command, args, options) => {
  const compatibility = nodeCompatibility();
  return execute(command === process.execPath ? compatibility.executable : command, args,
    { ...options, env: { ...compatibility.env, ...options?.env } });
};
const workspaceRoot = resolve(import.meta.dirname, "../../..");
const readFileSyncText = path => readFileSync(path, "utf8");
const root = await mkdtemp(join(tmpdir(), "runic-svelte-package-"));
try {
  // The experimental entries (docs/experimental.md) are checked here for @runic-artifex/views, react, vue and svelte;
  // the Angular package consumer builds its own.
  const workspaces = ["@runic-artifex/svelte", "@runic-artifex/sveltekit", "@runic-artifex/vite-plugin-runic",
    "@runic-artifex/views", "@runic-artifex/react", "@runic-artifex/vue"];
  for (const workspace of workspaces) {
    await execFile("npm", ["run", "build", "--workspace", workspace]);
  }
  const archives = await Promise.all(workspaces.map(pack));
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
  assert.deepEqual(Object.keys(svelteManifest.exports), [".", "./experimental", "./package.json"]);
  for (const file of ["package/dist/experimental.js", "package/dist/experimental.d.ts", "package/dist/views/RunicHostedRegion.svelte",
    "package/dist/views/RunicHostedRegion.svelte.d.ts"]) assert.ok(viewFiles.includes(file), `${file} is not packed`);
  for (const archive of archives.slice(3)) {
    const manifest = JSON.parse((await execFile("tar", ["-xOf", archive, "package/package.json"])).stdout);
    assert.deepEqual(Object.keys(manifest.exports["./experimental"]), ["types", "import", "default"], manifest.name);
  }
  await writeFile(join(root, "package.json"), JSON.stringify({ private: true, type: "module" }), "utf8");
  await execFile("npm", [
    "install", "--ignore-scripts", "--no-audit", "--no-fund", "--package-lock=false", "--legacy-peer-deps", ...npmReleaseAgeArguments(),
    ...archives,
    ...frameworks(),
  ], { cwd: root });
  await writeFile(join(root, "consumer.mjs"), [
    'import { runicSpaPageOptions } from "@runic-artifex/sveltekit/page-options";',
    'import { createRequire } from "node:module";',
    'const require = createRequire(import.meta.url);',
    'for (const name of ["@runic-artifex/svelte", "@runic-artifex/sveltekit", "@runic-artifex/vite-plugin-runic"]) require.resolve(name + "/package.json");',
    'if (runicSpaPageOptions.ssr !== false) throw new Error("package boundary failed");',
  ].join("\n"), "utf8");
  await execFile(process.execPath, ["consumer.mjs"], { cwd: root });

  // Each ./experimental entry imports in Node, where there is no DOM, and its types check in a TypeScript consumer.
  await writeFile(join(root, "experimental.mjs"), [
    'import { defineHostedRegion, hostedRegionTag } from "@runic-artifex/views/experimental";',
    'import { RunicHostedRegion as ReactRegion } from "@runic-artifex/react/experimental";',
    'import { RunicHostedRegion as VueRegion } from "@runic-artifex/vue/experimental";',
    'defineHostedRegion();',
    'if (hostedRegionTag !== "runic-hosted-region" || typeof ReactRegion !== "function" || typeof VueRegion !== "object") throw new Error("experimental entries failed");',
    'import.meta.resolve("@runic-artifex/svelte/experimental");',
  ].join("\n"), "utf8");
  await execFile(process.execPath, ["experimental.mjs"], { cwd: root });
  await writeFile(join(root, "experimental.ts"), [
    'import { defineHostedRegion, type HostedRegionState, type RunicHostedRegionElement } from "@runic-artifex/views/experimental";',
    'import { RunicHostedRegion as ReactRegion, type RunicHostedRegionProps } from "@runic-artifex/react/experimental";',
    'import { RunicHostedRegion as VueRegion } from "@runic-artifex/vue/experimental";',
    'import { RunicHostedRegion as SvelteRegion } from "@runic-artifex/svelte/experimental";',
    'defineHostedRegion();',
    'const state: HostedRegionState = "attached";',
    'const props: RunicHostedRegionProps = { name: "check", sizing: "content-height", fallback: (current: HostedRegionState) => current };',
    'declare const element: RunicHostedRegionElement;',
    'export const checked = [state, props, element.state, ReactRegion, VueRegion, SvelteRegion] as const;',
  ].join("\n"), "utf8");
  await writeFile(join(root, "tsconfig.json"), JSON.stringify({ compilerOptions: {
    target: "ES2022", module: "NodeNext", moduleResolution: "NodeNext", lib: ["ES2022", "DOM"], strict: true, noEmit: true,
    // svelte declares its .svelte modules, as a Svelte project loads them.
    types: ["svelte"], skipLibCheck: false,
  }, files: ["experimental.ts"] }), "utf8");
  await execFile(process.execPath, [resolve(workspaceRoot, "node_modules/typescript/bin/tsc"), "-p", "tsconfig.json"], { cwd: root });
} finally {
  await rm(root, { recursive: true, force: true });
}

// The framework versions the workspace develops against, for the consumer of the experimental entries.
function frameworks() {
  const read = name => JSON.parse(readFileSyncText(resolve(workspaceRoot, "packages/web", name, "package.json"))).devDependencies;
  const react = read("react");
  return [`react@${react.react}`, `@types/react@${react["@types/react"]}`, `vue@${read("vue").vue}`, `svelte@${read("svelte").svelte}`];
}

async function pack(workspace) {
  const packed = await execFile("npm", ["pack", "--json", "--workspace", workspace, "--pack-destination", root]);
  const result = JSON.parse(packed.stdout);
  const { filename } = Array.isArray(result) ? result[0] : Object.values(result)[0];
  return join(root, filename);
}
