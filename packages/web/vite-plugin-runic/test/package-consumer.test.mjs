import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { nodeCompatibility } from "../../../../eng/node-compatibility.mjs";

const execute = promisify(execFileCallback);
const execFile = (command, args, options) => {
  if (["node", "npm", "pnpm"].includes(command)) {
    const compatibility = nodeCompatibility();
    return execute(command === "node" ? compatibility.executable : command, args,
      { ...options, env: { ...compatibility.env, ...options?.env } });
  }
  return execute(command, args, options);
};

test("packed package is source-free and works from an isolated consumer", { timeout: 120000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-vite-package-"));
  try {
    const packed = await execFile("npm", ["pack", "--json", "--pack-destination", root], { cwd: process.cwd() });
    const result = JSON.parse(packed.stdout);
    const [{ filename }] = Array.isArray(result) ? result : Object.values(result);
    const tarball = join(root, filename);
    const files = (await execFile("tar", ["-tf", tarball])).stdout.split("\n").filter(Boolean);
    assert.ok(files.includes("package/package.json"));
    assert.ok(files.includes("package/dist/index.js"));
    assert.ok(files.includes("package/virtual.d.ts"));
    assert.equal(files.some((file) => file.startsWith("package/src/") || file.startsWith("package/test/")), false);

    await writeFile(
      join(root, "package.json"),
      JSON.stringify({
        name: "runic-vite-package-consumer",
        version: "0.0.0",
        private: true,
        type: "module",
        dependencies: {
          "@runic-artifex/vite-plugin-runic": `file:${tarball}`,
          vite: "8.2.2",
          "@vitejs/devtools": "0.5.2",
          typescript: "6.0.3",
        },
      }),
      "utf8",
    );
    await execFile("npm", [
      "install", "--ignore-scripts", "--no-audit", "--no-fund", "--package-lock=false", "--omit=peer",
    ], { cwd: root });
    await writeFile(join(root, "index.html"), '<script type="module" src="/entry.js"></script>', "utf8");
    await writeFile(
      join(root, "vite-env.d.ts"),
      '/// <reference types="@runic-artifex/vite-plugin-runic/virtual" />\n',
      "utf8",
    );
    await writeFile(
      join(root, "tsconfig.json"),
      JSON.stringify({
        compilerOptions: {
          target: "ES2022",
          module: "NodeNext",
          moduleResolution: "NodeNext",
          strict: true,
          noEmit: true,
        },
        include: ["consumer.ts", "vite-env.d.ts"],
      }),
      "utf8",
    );
    await writeFile(
      join(root, "consumer.ts"),
      [
        'import {',
        '  createRunicDevtoolsObserver, createRunicDiagnosticReporter, disposeRunicHmrResource, preserveRunicHmrResource, reportRunicDiagnostic, reportRunicState, traceRunicEvent,',
        '  type RunicDevtoolsObserver, type RunicDiagnosticDetail, type RunicDiagnosticDetailValue, type RunicDiagnosticEntry, type RunicDiagnosticReporter, type RunicDiagnosticSource, type RunicRuntimeState, type RunicTraceEntry, type RunicTraceKind,',
        '} from "virtual:runic/client";',
        'const value: RunicDiagnosticDetailValue = true;',
        'const detail: RunicDiagnosticDetail = { value };',
        'const source: RunicDiagnosticSource = "assets";',
        'const kind: RunicTraceKind = "event";',
        'const state: RunicRuntimeState = { connection: { state: "connected", transport: "consumer", revision: 1 } };',
        'const trace: RunicTraceEntry = { kind, label: "ready", detail };',
        'const diagnostic: RunicDiagnosticEntry = { source, kind, label: "ready", detail };',
        'const observer: RunicDevtoolsObserver = createRunicDevtoolsObserver();',
        'const reporter: RunicDiagnosticReporter = createRunicDiagnosticReporter(source);',
        'reportRunicState(state); traceRunicEvent(trace); reportRunicDiagnostic(diagnostic); reporter.report({ kind, label: "ready", detail }); observer.state(state); observer.trace(trace);',
        'const resource = preserveRunicHmrResource("type-consumer", () => ({ dispose() {} }));',
        'void disposeRunicHmrResource("type-consumer", (value) => { (value as typeof resource).dispose(); });',
      ].join("\n"),
      "utf8",
    );
    await execFile(process.execPath, ["node_modules/typescript/bin/tsc", "--noEmit"], { cwd: root });
    await writeFile(
      join(root, "entry.js"),
      'import { reportRunicDiagnostic } from "virtual:runic/client"; reportRunicDiagnostic({ source: "assets", kind: "event", label: "ready" });',
      "utf8",
    );
    await writeFile(
      join(root, "build.mjs"),
      'import { build } from "vite"; import { runic } from "@runic-artifex/vite-plugin-runic"; await build({ configFile: false, logLevel: "silent", plugins: [runic({ devtools: false })] });',
      "utf8",
    );
    await execFile(process.execPath, ["build.mjs"], { cwd: root });
    // Exercise the installed dock under both supported development runtimes.
    await writeFile(join(root, "devtools.mjs"), "import assert from \"node:assert/strict\";\nimport { createServer } from \"vite\";\nimport { DevTools } from \"@vitejs/devtools\";\nimport { runic } from \"@runic-artifex/vite-plugin-runic\";\nconst server = await createServer({ configFile: false, logLevel: \"silent\",\n  plugins: [DevTools({ embeddedVisibility: \"passive\" }), runic({ devtools: true,\n    contract: { identity: \"sample\", version: \"1\", fingerprint: \"abc\" } })],\n  server: { host: \"127.0.0.1\", port: 0, strictPort: false } });\ntry {\n  await server.listen();\n  const address = server.httpServer.address();\n  const response = await fetch(`http://127.0.0.1:${address.port}/__runic/state`);\n  assert.equal(response.status, 200);\n  assert.equal((await response.json()).contract.identity, \"sample\");\n} finally { await server.close(); }\n");
    await execFile(process.execPath, ["devtools.mjs"], { cwd: root, timeout: 30_000 });
    await execFile("node", ["devtools.mjs"], { cwd: root, timeout: 30_000 });
    const manifest = JSON.parse(await readFile(join(root, "node_modules", "@runic-artifex", "vite-plugin-runic", "package.json"), "utf8"));
    assert.equal(manifest.name, "@runic-artifex/vite-plugin-runic");

    await writeFile(join(root, "entry.js"), 'import "virtual:runic-toolkit/client";', "utf8");
    await assert.rejects(
      execFile(process.execPath, ["build.mjs"], { cwd: root }),
      /RUNICP001: "virtual:runic-toolkit\/client" was removed in v0\.2\. Import "virtual:runic\/client" instead\./,
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
