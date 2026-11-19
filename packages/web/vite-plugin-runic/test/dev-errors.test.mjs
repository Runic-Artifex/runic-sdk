import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { createServer } from "vite";
import { runic } from "../dist/index.js";
import { frontendErrorCode, frontendErrorLines, frontendErrorsClearedLine, printDevErrors } from "../dist/dev-errors.js";

// #1160: the regex that the runic-app template's .vscode/tasks.json and tools/dotnet-runic/README.md give for dev's
// output, $msCompile's after dev's optional label.
const matcher = /^(?:\[(?:host|app|vite)\] )?\s*(?:\s*\d+>)?(\S.*?)(?:\((\d+|\d+,\d+|\d+,\d+,\d+,\d+)\))?\s*:\s+(?:(\S+)\s+)?((?:fatal +)?error|warning|info)\s+(\w+\d+)?\s*:\s*(.*)$/;

test("a Vite error is one MSBuild-form line per diagnostic, with 1-based positions", () => {
  const project = "/home/me/App/App.csproj";
  // Oxc's diagnostics carry their positions in the message; Vite's loc for them can be a place in Vite's own code
  // (under Bun it takes the error's line and column).
  const oxc = {
    message: "Transform failed with 2 errors:\n\n[PARSE_ERROR] Expected corresponding JSX closing tag for 'p'.\n   ╭─[ ../../App/Frontend/src/App.tsx:2:23 ]\n   │\n" +
      " 2 │   return <main><p>hi</main>;\n───╯\n\n[PARSE_ERROR] Unexpected token.\n   ╭─[ ../../App/Frontend/src/App.tsx:3:1 ]\n───╯\n",
    id: "/home/me/App/Frontend/src/App.tsx?t=17", plugin: "vite:oxc", pluginCode: "a\nb\nc\n", loc: { line: 7581, column: 19 },
  };
  const lines = frontendErrorLines(oxc, project);
  assert.deepEqual(lines, [
    `/home/me/App/Frontend/src/App.tsx(2,23): error ${frontendErrorCode}: [vite:oxc] [PARSE_ERROR] Expected corresponding JSX closing tag for 'p'.`,
    "/home/me/App/Frontend/src/App.tsx(3,1): error RUNICP008: [vite:oxc] [PARSE_ERROR] Unexpected token.",
  ]);
  const match = matcher.exec(`[vite] ${lines[0]}`);
  assert.deepEqual([match?.[1], match?.[2], match?.[4], match?.[5]], ["/home/me/App/Frontend/src/App.tsx", "2,23", "error", "RUNICP008"]);

  // Rollup's loc otherwise: line 1-based, column 0-based; one outside the module's code is not taken.
  const resolve = { message: 'Failed to resolve import "./missing" from "src/type.ts". Does the file exist?', id: "/home/me/App/Frontend/src/type.ts",
    plugin: "vite:import-analysis", pluginCode: 'import { nothing } from "./missing";\n', loc: { file: "/home/me/App/Frontend/src/type.ts", line: 1, column: 24 } };
  assert.deepEqual(frontendErrorLines(resolve, project),
    ['/home/me/App/Frontend/src/type.ts(1,25): error RUNICP008: [vite:import-analysis] Failed to resolve import "./missing" from "src/type.ts". Does the file exist?']);
  assert.deepEqual(frontendErrorLines({ ...resolve, loc: { line: 7581, column: 19 } }, project),
    ['/home/me/App/Frontend/src/type.ts: error RUNICP008: [vite:import-analysis] Failed to resolve import "./missing" from "src/type.ts". Does the file exist?']);
  // The file and position again at the start of the message are left out.
  assert.deepEqual(frontendErrorLines({ message: '/home/me/App/Frontend/src/a.ts:3:7: Expected ";"', id: "/home/me/App/Frontend/src/a.ts" }, project),
    ['/home/me/App/Frontend/src/a.ts: error RUNICP008: Expected ";"']);
  // A virtual module or no module at all: the project, as for dev's own failures.
  const virtual = frontendErrorLines({ message: "Failed to load", id: "\0virtual:runic/client" }, project);
  assert.deepEqual(virtual, [`${project} : error RUNICP008: Failed to load`]);
  assert.equal(matcher.exec(virtual[0])?.[1], project);
  // Windows paths keep their drive.
  assert.match(frontendErrorLines({ message: "x", id: "C:/Users/me/App/Frontend/src/App.tsx", loc: { line: 2, column: 0 } }, "C:\\App.csproj")[0],
    /^C:.*App\.tsx\(2,1\): error RUNICP008: x$/u);
});

test("without dotnet runic dev, nothing is printed and the channel is left alone", () => {
  const hot = { send() {} };
  const original = hot.send;
  printDevErrors({ environments: { client: { hot } } }, () => assert.fail("printed"), "");
  assert.equal(hot.send, original);
});

const files = {
  "index.html": '<!doctype html><div id="app"></div><script type="module" src="/src/main.ts"></script>\n',
  "src/main.ts": 'import { greeting } from "./greet";\ndocument.body.textContent = greeting;\nimport.meta.hot?.accept();\n',
  "src/greet.ts": 'export const greeting: string = "Hello";\n',
};

test("under dotnet runic dev, the page's error is printed once, and cleared once the module compiles again", { timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-dev-errors-"));
  for (const [path, text] of Object.entries(files)) {
    await mkdir(join(root, path, ".."), { recursive: true });
    await writeFile(join(root, path), text);
  }
  await mkdir(join(root, "node_modules", "@runic-artifex"), { recursive: true });
  await symlink(process.cwd(), join(root, "node_modules", "@runic-artifex", "vite-plugin-runic"), "dir");
  const lines = [];
  const write = process.stdout.write;
  const previous = process.env.RUNIC_APPLICATION_DEV_PROJECT;
  process.env.RUNIC_APPLICATION_DEV_PROJECT = join(root, "App.csproj");
  process.stdout.write = function (chunk, ...rest) {
    for (const line of String(chunk).split("\n")) if (line.includes("RUNICP008") || line === frontendErrorsClearedLine) lines.push(line);
    return write.call(this, chunk, ...rest);
  };
  const server = await createServer({ root, configFile: false, logLevel: "silent", plugins: [runic({ devtools: false })],
    server: { host: "127.0.0.1", port: 0, strictPort: false } });
  try {
    await server.listen();
    const base = `http://127.0.0.1:${server.httpServer.address().port}`;
    assert.equal((await fetch(`${base}/src/main.ts`)).status, 200);
    assert.equal((await fetch(`${base}/src/greet.ts`)).status, 200);
    // Oxc stops at line 2, column 30 (1-based): a semicolon is missing before the second string literal.
    await writeFile(join(root, "src/greet.ts"), 'export const greeting: string = "Hello";\nexport const broken = 1 + "x" "y";\n');
    await until(async () => (await fetch(`${base}/src/greet.ts`)).status === 500);
    await fetch(`${base}/src/greet.ts`);
    await until(() => lines.length > 0);
    assert.equal(lines.length, 1, `printed once: ${JSON.stringify(lines)}`);
    const match = matcher.exec(`[vite] ${lines[0]}`);
    assert.equal(match?.[1], join(server.config.root, "src/greet.ts"));
    assert.equal(match?.[2], "2,30");
    assert.equal(match?.[5], "RUNICP008");

    await writeFile(join(root, "src/greet.ts"), 'export const greeting: string = "Hello again";\n');
    // The file watcher's change event, which Bun's file watching does not always deliver in time here.
    server.watcher.emit("change", join(server.config.root, "src/greet.ts"));
    await until(() => lines.includes(frontendErrorsClearedLine));
    assert.deepEqual(lines.slice(1), [frontendErrorsClearedLine]);
  } finally {
    process.stdout.write = write;
    if (previous === undefined) delete process.env.RUNIC_APPLICATION_DEV_PROJECT;
    else process.env.RUNIC_APPLICATION_DEV_PROJECT = previous;
    await server.close();
    await rm(root, { recursive: true, force: true });
  }
});

async function until(condition, ms = 20_000) {
  for (const deadline = Date.now() + ms; !(await condition());) {
    if (Date.now() > deadline) throw new Error("not within the time");
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
}
