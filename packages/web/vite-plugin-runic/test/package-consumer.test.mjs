import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import test from "node:test";

const execFile = promisify(execFileCallback);

test("packed package is source-free and works from an isolated consumer", async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-vite-package-"));
  try {
    const packed = await execFile("npm", ["pack", "--json", "--pack-destination", root], { cwd: process.cwd() });
    const [{ filename }] = JSON.parse(packed.stdout);
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
          vite: "8.2.1",
          typescript: "5.9.3",
        },
      }),
      "utf8",
    );
    await execFile("npm", [
      "install", "--ignore-scripts", "--no-audit", "--no-fund", "--package-lock=false",
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
}, 30_000);
