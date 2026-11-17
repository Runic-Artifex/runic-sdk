import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import vue from "@vitejs/plugin-vue";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import { build, createServer, normalizePath } from "vite";
import { runic } from "../dist/index.js";
import { absoluteSourceMapSources } from "../dist/dev-source-paths.js";
import { inlineSourceMap, resolveFrame } from "../dist/inspect-frameworks.js";

test("makes relative sources absolute against the module's directory", () => {
  const posixMap = { sources: ["App.tsx", "../shared/util.ts", "pages\\CounterPage.tsx"] };
  assert.equal(absoluteSourceMapSources(posixMap, "/home/me/app/Frontend/src/App.tsx"), true);
  assert.deepEqual(posixMap.sources,
    ["/home/me/app/Frontend/src/App.tsx", "/home/me/app/Frontend/shared/util.ts", "/home/me/app/Frontend/src/pages/CounterPage.tsx"]);

  // Vite's module paths on Windows have forward slashes, its relative sources the platform's backslashes.
  const windowsMap = { sources: ["App.tsx", "..\\shared\\util.ts", "pages/CounterPage.tsx"] };
  assert.equal(absoluteSourceMapSources(windowsMap, "C:/Users/me/app/Frontend/src/App.tsx"), true);
  assert.deepEqual(windowsMap.sources,
    ["C:/Users/me/app/Frontend/src/App.tsx", "C:/Users/me/app/Frontend/shared/util.ts", "C:/Users/me/app/Frontend/src/pages/CounterPage.tsx"]);
});

test("leaves absolute sources, URLs, virtual modules and maps with a sourceRoot as they are", () => {
  const sources = ["/home/me/lib/a.ts", "C:/lib/b.ts", "D:\\lib\\c.ts", "webpack://app/d.ts", "data:text/plain,e", "\0virtual:f", null, ""];
  const map = { sources: [...sources] };
  assert.equal(absoluteSourceMapSources(map, "/home/me/app/src/main.ts"), false);
  assert.deepEqual(map.sources, sources);

  const rooted = { sources: ["App.tsx"], sourceRoot: "/src/" };
  assert.equal(absoluteSourceMapSources(rooted, "/home/me/app/src/App.tsx"), false);
  assert.deepEqual(rooted.sources, ["App.tsx"]);

  // A module without a file path (a virtual module) has no directory to resolve against.
  const virtual = { sources: ["client.ts"] };
  assert.equal(absoluteSourceMapSources(virtual, "\0virtual:runic/client"), false);
  assert.deepEqual(virtual.sources, ["client.ts"]);
  assert.equal(absoluteSourceMapSources({}, "/home/me/app/src/App.tsx"), false);
});

const files = {
  "index.html": '<!doctype html><div id="app"></div><script type="module" src="/src/main.ts"></script>\n',
  "src/main.ts": 'import { greet } from "./pages/greet";\nexport const message: string = greet("Runic");\n',
  "src/pages/greet.ts": "export function greet(name: string): string {\n  return `Hello, ${name}`;\n}\n",
  "src/App.tsx": "export function App(props: { name: string }) {\n  return <p>{props.name}</p>;\n}\n",
  "src/Counter.vue": '<script setup lang="ts">\nimport { ref } from "vue";\nconst count = ref<number>(0);\n</script>\n\n<template>\n  <button @click="count++">{{ count }}</button>\n</template>\n',
  "src/Counter.svelte": '<script lang="ts">\n  let count: number = $state(0);\n</script>\n\n<button onclick={() => count++}>{count}</button>\n',
};

async function fixture() {
  const root = await mkdtemp(join(tmpdir(), "runic-dev-sources-"));
  for (const [path, text] of Object.entries(files)) {
    await mkdir(join(root, path, ".."), { recursive: true });
    await writeFile(join(root, path), text);
  }
  await mkdir(join(root, "node_modules", "@runic-artifex"), { recursive: true });
  await symlink(process.cwd(), join(root, "node_modules", "@runic-artifex", "vite-plugin-runic"), "dir");
  for (const name of ["vue", "svelte", "react"]) {
    await symlink(join(process.cwd(), "node_modules", name), join(root, "node_modules", name), "dir");
  }
  return root;
}

const plugins = () => [vue(), svelte(), runic({ devtools: false })];

test("the development server sends module maps with absolute sources, for TS, TSX, Vue and Svelte", { timeout: 60_000 }, async () => {
  const root = await fixture();
  const server = await createServer({ root, configFile: false, logLevel: "silent", plugins: plugins(),
    server: { host: "127.0.0.1", port: 0, strictPort: false } });
  try {
    await server.listen();
    const address = server.httpServer.address();
    const base = `http://127.0.0.1:${address.port}`;
    const sourceRoot = normalizePath(server.config.root);
    for (const [url, file] of [
      ["/src/main.ts", "src/main.ts"], ["/src/pages/greet.ts", "src/pages/greet.ts"], ["/src/App.tsx", "src/App.tsx"],
      ["/src/Counter.vue", "src/Counter.vue"], ["/src/Counter.svelte", "src/Counter.svelte"],
    ]) {
      const code = await fetch(`${base}${url}`).then((response) => response.text());
      const map = inlineSourceMap(code);
      assert.ok(map !== undefined, `${url} has an inline map`);
      assert.ok(map.sources.includes(`${sourceRoot}/${file}`), `${url}: ${JSON.stringify(map.sources)}`);
      assert.ok(map.sources.every((source) => typeof source !== "string" || source.startsWith("/") || /^[a-z]:\//iu.test(source)),
        `${url}: every source is absolute: ${JSON.stringify(map.sources)}`);
      // The same map at <module>.map.
      const separate = await fetch(`${base}${url}.map`).then((response) => response.json());
      assert.deepEqual(separate.sources, map.sources, `${url}.map`);
    }
    // A second request (Vite's cached transform result) keeps the absolute sources.
    const again = inlineSourceMap(await fetch(`${base}/src/main.ts`).then((response) => response.text()));
    assert.ok(again.sources.includes(`${sourceRoot}/src/main.ts`));
    // Runic Inspect maps a stack frame through such a map to the original file, as with Vite's relative sources.
    const fetchText = (url) => fetch(url).then((response) => response.text());
    const location = await resolveFrame({ url: `${base}/src/pages/greet.ts`, line: 2, column: 3 }, sourceRoot, fetchText);
    assert.equal(location.file, `${sourceRoot}/src/pages/greet.ts`);
    assert.equal(location.line, 2);
  } finally {
    await server.close();
    await rm(root, { recursive: true, force: true });
  }
});

test("a production build's source maps keep Vite's relative sources", { timeout: 60_000 }, async () => {
  const root = await fixture();
  try {
    const sources = async (outDir, withRunic) => {
      await build({ root, configFile: false, logLevel: "silent", base: "./",
        plugins: withRunic ? plugins() : [vue(), svelte()],
        build: { outDir, sourcemap: true, rolldownOptions: { input: [join(root, "index.html")] } } });
      const assets = join(root, outDir, "assets");
      const maps = (await readdir(assets)).filter((name) => name.endsWith(".map")).sort();
      return Promise.all(maps.map(async (name) => JSON.parse(await readFile(join(assets, name), "utf8")).sources));
    };
    const withRunic = await sources("with-runic", true);
    const without = await sources("without-runic", false);
    assert.ok(withRunic.length > 0);
    assert.deepEqual(withRunic, without);
    for (const source of withRunic.flat()) assert.doesNotMatch(source, /^(?:\/|[a-z]:)/iu, source);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
