// Runic Inspect under `dotnet runic dev` (#1012): the window's document is the .NET host's page, served from its own
// origin, which loads Vite's modules from Vite's origin (tools/dotnet-runic/FrontendDevelopmentDocument.cs). Relative
// URLs such as /__runic/sources name the host, so Inspect asks the Vite server over Vite's HMR connection instead: the
// overview shows the command's C# location from the source map, its link opens it in the editor (a LAUNCH_EDITOR
// script records the file, line and column), a location the source map and Vite do not serve is refused over the same
// connection and launches nothing, and the host's origin never receives a /__runic request.
import assert from "node:assert/strict";
import { chmod, mkdir, mkdtemp, readFile, rm, stat, symlink, writeFile } from "node:fs/promises";
import { createServer as createHttpServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { chromium } from "playwright-core";
import { createServer } from "vite";
import { runic } from "../dist/index.js";
import { scaled } from "../../../../tests/support/timeouts.mjs";

const views = fileURLToPath(new URL("../../views/", import.meta.url));

const viewModel = `namespace Demo;

public sealed partial class EditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string title = "";

    [RelayCommand]
    private async Task SaveAsync() => await Task.Delay(20);
}
`;

function sourceMap(file) {
  return {
    format: "runic-bridge-sources/1",
    viewModels: [{
      id: "Demo.EditorViewModel", name: "Editor", route: "editor", contract: "Demo.EditorViewModel:00", pageKinds: ["editor"],
      symbol: "Demo.EditorViewModel", location: { file, line: 3, column: 29 },
    }],
    members: [{
      id: "Demo.EditorViewModel/command/save", viewModel: "Demo.EditorViewModel", member: "save", kind: "command",
      symbol: "Demo.EditorViewModel.SaveAsync()", property: "Demo.EditorViewModel.SaveCommand", location: { file, line: 9, column: 5 },
    }],
  };
}

const main = `
import { connectView } from "@runic-artifex/views/generated";

const channel = new MessageChannel();
const pending = [];
channel.port2.onmessage = ({ data }) => pending.shift()(data);
globalThis.__runicBridge = {
  isConnected: () => true,
  onConnectionChange(report) { report(true); return () => {}; },
  onMessage() { return () => {}; },
  request(text) {
    const message = JSON.parse(text);
    const reply = value => Promise.resolve(JSON.stringify(value));
    switch (message.kind) {
      case "hello": return reply({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features: [] });
      case "open": return reply({ ok: true, presentation: "ok", revision: 1, state: { title: "" } });
      case "command":
        setTimeout(() => channel.port1.postMessage(JSON.stringify({ ok: true, revision: 1 })), 10);
        return new Promise(resolve => pending.push(resolve));
      default: return reply({ ok: true });
    }
  },
};
const client = await connectView({ contract: "Demo.EditorViewModel:00", route: "editor", members: { title: wire => String(wire) } });
document.getElementById("save").addEventListener("click", () => { void client.command("save"); });
globalThis.inspectHostTest = true;
`;

async function waitForFile(path, timeout) {
  const deadline = Date.now() + timeout;
  for (;;) {
    try { return await readFile(path, "utf8"); }
    catch (error) {
      if (Date.now() > deadline) throw error;
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
  }
}

test("dotnet runic dev's page: Runic Inspect gets C# locations and opens them over Vite's connection", {
  timeout: scaled(60_000),
  skip: !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH || process.platform === "win32",
}, async () => {
  const directory = await mkdtemp(join(tmpdir(), "runic-inspect-host-"));
  const frontend = join(directory, "Frontend");
  const file = join(directory, "EditorViewModel.cs");
  const record = join(directory, "editor-arguments.txt");
  const editor = join(directory, "record-editor.sh");
  const environment = { LAUNCH_EDITOR: editor, RUNIC_TEST_EDITOR_RECORD: record };
  const previous = Object.fromEntries(Object.keys(environment).map((name) => [name, process.env[name]]));
  let server, host, browser;
  try {
    await writeFile(file, viewModel);
    const mapPath = join(directory, "obj", "Debug", "net10.0", "runic-bridge", "sources.json");
    await mkdir(join(mapPath, ".."), { recursive: true });
    await writeFile(mapPath, JSON.stringify(sourceMap(file)));
    await writeFile(editor, '#!/bin/sh\nprintf "%s\\n" "$@" > "$RUNIC_TEST_EDITOR_RECORD"\n');
    await chmod(editor, 0o755);
    await mkdir(join(frontend, "node_modules", "@runic-artifex"), { recursive: true });
    await writeFile(join(frontend, "index.html"),
      '<!doctype html><html><body><button id="save">Save</button><script type="module" src="/main.js"></script></body></html>');
    await writeFile(join(frontend, "main.js"), main);
    await symlink(process.cwd(), join(frontend, "node_modules", "@runic-artifex", "vite-plugin-runic"), "dir");
    await symlink(views, join(frontend, "node_modules", "@runic-artifex", "views"), "dir");
    Object.assign(process.env, environment);

    server = await createServer({ root: frontend, configFile: false, logLevel: "silent", plugins: [runic()],
      server: { host: "127.0.0.1", port: 0, fs: { strict: false } } });
    await server.listen();
    // The host's document: Vite's page with absolute URLs for Vite's modules, as dotnet runic dev writes it. The host
    // serves nothing else and records what the page asks it for.
    const vite = `http://127.0.0.1:${server.httpServer.address().port}`;
    const document = (await (await fetch(`${vite}/index.html`)).text())
      .replace(/(<script\b[^>]*\bsrc=")\/(?!\/)/gu, `$1${vite}/`).replace("<head>", '<head><base href="./">');
    const hostRequests = [];
    host = createHttpServer((request, response) => {
      const page = request.url === "/" || request.url === "/index.html";
      if (!page) hostRequests.push(request.url);
      response.writeHead(page ? 200 : 404, { "Content-Type": "text/html; charset=utf-8" });
      response.end(page ? document : "");
    });
    await new Promise((resolve) => host.listen(0, "127.0.0.1", resolve));

    browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, headless: true });
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", (error) => errors.push(String(error)));
    await page.goto(`http://127.0.0.1:${host.address().port}/`);
    await page.waitForFunction(() => globalThis.inspectHostTest === true, null, { timeout: scaled(20_000) });

    // The click teaches Inspect the button's command; the overview has its C# location from the source map.
    await page.click("#save");
    await page.waitForFunction(async () => (await globalThis[Symbol.for("runic.vite.inspect")]
      .describe(document.querySelector("#save"))).calls[0]?.outcome === "ok", null, { timeout: scaled(10_000) });
    const description = await page.evaluate(() => globalThis[Symbol.for("runic.vite.inspect")].describe(document.querySelector("#save")));
    assert.deepEqual(description.commands[0]?.location, { file, line: 9, column: 5, editor: "runic", id: "Demo.EditorViewModel/command/save" });
    assert.ok(!description.notes.some((note) => /source map/iu.test(note)), JSON.stringify(description.notes));

    // The overview card's link opens the [RelayCommand] method.
    await page.evaluate(() => globalThis[Symbol.for("runic.vite.inspect")].select(document.querySelector("#save")));
    await page.locator("runic-inspect .card a", { hasText: "EditorViewModel.cs:9" }).click();
    assert.deepEqual((await waitForFile(record, scaled(10_000))).trim().split("\n"), [file, "9", "5"]);
    await rm(record);

    // Refused over the same connection: a file neither the source map lists nor Vite serves (Vite's fs.strict is off
    // here, so a file outside the root must not exist), an id the map lacks, a relative path.
    const refused = await page.evaluate(async (paths) => {
      const inspect = globalThis[Symbol.for("runic.vite.inspect")];
      return Promise.all([
        inspect.open({ file: paths.missing, line: 1, column: 1, editor: "vite" }),
        inspect.open({ file: paths.cs, line: 1, column: 1, editor: "runic", id: "Demo.EditorViewModel/command/missing" }),
        inspect.open({ file: "EditorViewModel.cs", line: 1, column: 1, editor: "vite" }),
      ]);
    }, { missing: join(directory, "missing.cs"), cs: file });
    assert.deepEqual(refused, [false, false, false]);
    await new Promise((resolve) => setTimeout(resolve, 200));
    assert.equal(await stat(record).then(() => true, () => false), false, "a refused location launched the editor");

    // Nothing went to the host's origin, which neither serves nor forwards /__runic.
    assert.deepEqual(hostRequests.filter((url) => url.startsWith("/__runic")), []);
    assert.deepEqual(errors, []);
  } finally {
    await browser?.close();
    host?.close();
    await server?.close();
    for (const [name, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
    await rm(directory, { recursive: true, force: true });
  }
});
