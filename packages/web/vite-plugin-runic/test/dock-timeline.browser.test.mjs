// The dock's Bridge call timeline in Chromium (#954): under `vite dev` with the Vite DevTools dock, a click's command is
// a row `command EditorViewModel.save → SaveAsync() <n> ms` of the Runic dock's "Bridge calls" card, whose button opens
// the `[RelayCommand]` method in the editor (only what /__runic/open would open). A failed command shows its .NET frames, and a frame
// in the ViewModel's file opens at its line. A reply that carries .NET's handler timing (handler-timing/1) shows the
// handler's own duration.
// The page is the shape `dotnet runic dev` gives a window: a document the .NET host serves from its own origin, which
// loads Vite's modules from Vite's origin (tools/dotnet-runic/FrontendDevelopmentDocument.cs), so the development
// client loads the dock from Vite. The page's Bridge answers over a MessageChannel, as a host transport does; the
// project is a ViewModel file and the Bridge generator's source map in obj, as `dotnet build` leaves them.
import assert from "node:assert/strict";
import { chmod, mkdir, mkdtemp, readFile, rm, symlink, writeFile } from "node:fs/promises";
import { createServer as createHttpServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { chromium } from "playwright-core";
import { createServer } from "vite";
import { DevTools } from "@vitejs/devtools";
import { runic } from "../dist/index.js";
import { scaled } from "../../../../tests/support/timeouts.mjs";
import { readRecord, recordingEditor } from "../../../../tests/support/recording-editor.mjs";

const views = fileURLToPath(new URL("../../views/", import.meta.url));

const viewModel = `namespace Demo;

public sealed partial class EditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string title = "";

    [RelayCommand]
    private async Task SaveAsync()
    {
        await Task.Delay(20);
        throw new InvalidOperationException("Disk full.");
    }
}
`;

function sourceMap(file) {
  return {
    format: "runic.views.bridge-sources/1",
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

// The first save answers after 20 ms, the second fails with .NET's development detail.
const main = (file) => `
import { connectView } from "@runic-artifex/views/generated";

const channel = new MessageChannel();
const pending = [];
let revision = 1;
let saves = 0;
channel.port2.onmessage = ({ data }) => pending.shift()(data);
globalThis.__runicBridge = {
  isConnected: () => true,
  onConnectionChange(report) { report(true); return () => {}; },
  onMessage() { return () => {}; },
  request(text) {
    const message = JSON.parse(text);
    const reply = value => Promise.resolve(JSON.stringify(value));
    switch (message.kind) {
      // A .NET host in Development measures each handler (handler-timing/1, #958).
      case "hello": return reply({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features: ["handler-timing/1"] });
      case "open": return reply({ ok: true, presentation: "ok", revision, state: { title: "" } });
      case "command": {
        saves++;
        // The command changes no state, so the reply keeps the revision.
        globalThis.dockTimingRequested = message.timing === true;
        const answer = saves === 1 ? { ok: true, revision, ...(message.timing === true ? { handler: { name: "EditorViewModel.SaveAsync", duration: 12 } } : {}) } : { ok: false, error: { kind: "failed", message: "Save failed.",
          detail: { type: "System.InvalidOperationException", message: "Disk full.", stack: ${JSON.stringify(
            `System.InvalidOperationException: Disk full.\n   at Demo.EditorViewModel.SaveAsync() in ${file}:line 12\n` +
            "   at CommunityToolkit.Mvvm.Input.AsyncRelayCommand.AwaitAndThrowIfFailed(Task executionTask)")} } } };
        setTimeout(() => channel.port1.postMessage(JSON.stringify(answer)), 20);
        return new Promise(resolve => pending.push(resolve));
      }
      default: return reply({ ok: true });
    }
  },
};
const client = await connectView({ contract: "Demo.EditorViewModel:00", route: "editor",
  members: { title: wire => String(wire) } });
document.getElementById("save").addEventListener("click", () => { client.command("save").catch(() => {}); });
globalThis.dockTest = true;
`;

test("vite dev: a click's command is a dock row with its C# handler, and its button opens the [RelayCommand] method", {
  timeout: scaled(60_000),
  skip: !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH || process.platform === "win32",
}, async () => {
  const directory = await mkdtemp(join(tmpdir(), "runic-dock-timeline-"));
  const frontend = join(directory, "Frontend");
  const file = join(directory, "EditorViewModel.cs");
  // An editor that records its arguments: launch-editor calls an unknown editor with file, line, column.
  const { record, environment: editorEnvironment } = await recordingEditor(directory);
  const environment = { ...editorEnvironment, VITE_DEVTOOLS_DISABLE_CLIENT_AUTH: "true" };
  const previous = Object.fromEntries(Object.keys(environment).map((name) => [name, process.env[name]]));
  let server, host, browser, context;
  try {
    await writeFile(file, viewModel);
    const mapPath = join(directory, "obj", "Debug", "net10.0", "runic-bridge", "sources.json");
    await mkdir(join(mapPath, ".."), { recursive: true });
    await writeFile(mapPath, JSON.stringify(sourceMap(file)));
    await mkdir(join(frontend, "node_modules", "@runic-artifex"), { recursive: true });
    await writeFile(join(frontend, "index.html"),
      '<!doctype html><html><body><button id="save">Save</button><script type="module" src="/main.js"></script></body></html>');
    await writeFile(join(frontend, "main.js"), main(file));
    await symlink(process.cwd(), join(frontend, "node_modules", "@runic-artifex", "vite-plugin-runic"), "dir");
    await symlink(views, join(frontend, "node_modules", "@runic-artifex", "views"), "dir");
    // Only this loopback test server bypasses the upstream interactive OTP prompt.
    Object.assign(process.env, environment);

    const plugin = runic({ devtools: true });
    const setup = plugin.devtools.setup;
    plugin.devtools.setup = async (value) => {
      context = value;
      await setup(value);
    };
    // The Vite root is the project's Frontend/, so the plugin finds the map under the project's obj.
    server = await createServer({ root: frontend, configFile: false, logLevel: "silent",
      plugins: [DevTools({ embeddedVisibility: "normal" }), plugin],
      server: { host: "127.0.0.1", port: 0, fs: { strict: false } } });
    await server.listen();
    browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, headless: true });
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", (error) => errors.push(String(error)));
    // The host's document: Vite's page with absolute URLs for Vite's modules, as dotnet runic dev writes it.
    const vite = `http://127.0.0.1:${server.httpServer.address().port}`;
    const document = (await (await fetch(`${vite}/index.html`)).text())
      .replace(/(<script\b[^>]*\bsrc=")\/(?!\/)/gu, `$1${vite}/`).replace("<head>", '<head><base href="./">');
    host = createHttpServer((request, response) => {
      const page = request.url === "/" || request.url === "/index.html";
      response.writeHead(page ? 200 : 404, { "Content-Type": "text/html; charset=utf-8" });
      response.end(page ? document : "");
    });
    await new Promise((resolve) => host.listen(0, "127.0.0.1", resolve));
    await page.goto(`http://127.0.0.1:${host.address().port}/`);
    await page.waitForFunction(() => globalThis.dockTest === true, null, { timeout: scaled(20_000) });
    await page.locator('button[aria-label="Runic"]').waitFor({ state: "attached", timeout: scaled(10_000) });
    // The development client loaded the dock from Vite's origin.
    assert.equal(await page.locator(`script[src="${vite}/__devtools/embedded.js"]`).count(), 1);
    context.docks.activate("runic:overview");
    await page.getByText("No Bridge call yet", { exact: true }).waitFor({ timeout: scaled(10_000) });

    // The click's command, with its handler from the source map and the page's measured duration.
    await page.click("#save");
    const row = page.getByText(/^command EditorViewModel\.save → SaveAsync\(\) \d+ ms$/u);
    await row.waitFor({ timeout: scaled(10_000) });
    const duration = Number(/(\d+) ms$/u.exec(await row.textContent())[1]);
    assert.ok(duration >= 15, `the call took ${duration} ms, less than the reply's delay`);
    await page.getByText(/EditorViewModel\.cs:9/u).first().waitFor({ timeout: scaled(10_000) });
    // The reply carried .NET's handler timing, which @runic-artifex/views puts on the call's end event: the row shows
    // the handler's own duration.
    assert.equal(await page.evaluate(() => globalThis.dockTimingRequested), true, "the call asked for its handler's timing");
    await page.getByText(/^C# 12 ms · revision 1 · EditorViewModel\.cs:9/u).waitFor({ timeout: scaled(10_000) });

    // Its button opens the [RelayCommand] method.
    await page.getByRole("button", { name: "Open SaveAsync()" }).first().click();
    assert.deepEqual(await readRecord(record), [file, "9", "5"]);

    // A failed save shows its .NET frames; the frame in the ViewModel's file opens at its line.
    await page.click("#save");
    await page.getByText(/^command EditorViewModel\.save → SaveAsync\(\) \d+ ms · error \(failed\)$/u).waitFor({ timeout: scaled(10_000) });
    await page.getByText("System.InvalidOperationException: Disk full.", { exact: true }).waitFor({ timeout: scaled(10_000) });
    const frame = page.getByRole("button", { name: "EditorViewModel.SaveAsync() · EditorViewModel.cs:12" }).first();
    await frame.click();
    assert.deepEqual(await readRecord(record), [file, "12", "1"]);
    // A frame outside the map's files is text only.
    assert.equal(await page.getByRole("button", { name: /AwaitAndThrowIfFailed/u }).count(), 0);

    // The state endpoint has the calls, without the failure's stack.
    const state = await fetch(`http://127.0.0.1:${server.httpServer.address().port}/__runic/state`).then((response) => response.json());
    assert.deepEqual(state.calls.map((call) => call.label.replace(/\d+ ms/u, "n ms")), [
      "command EditorViewModel.save → SaveAsync() n ms",
      "command EditorViewModel.save → SaveAsync() n ms · error (failed)",
    ]);
    assert.equal(state.calls[0].handlerDuration, 12);
    assert.doesNotMatch(JSON.stringify(state), /Disk full|line 12/u);
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
