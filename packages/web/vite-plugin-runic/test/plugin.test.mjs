import assert from "node:assert/strict";
import test from "node:test";
import { DevTools } from "@vitejs/devtools";
import { createServer } from "vite";
import { runicToolkit } from "../dist/index.js";

test("exposes a real virtual client and bounded diagnostics endpoint", () => {
  const plugin = runicToolkit({ devtools: false });
  plugin.configResolved({ command: "serve", mode: "development", root: process.cwd() });
  assert.equal(plugin.resolveId("virtual:runic-toolkit/client"), "\0virtual:runic-toolkit/client");
  assert.match(plugin.load("\0virtual:runic-toolkit/client"), /vite-plugin-runic-toolkit\/client/);

  const handlers = new Map();
  const middleware = [];
  plugin.configureServer({
    ws: {
      on: (event, handler) => handlers.set(event, handler),
      send: () => undefined,
    },
    middlewares: { use: (path, handler) => middleware.push([path, handler]) },
    httpServer: undefined,
  });
  assert.equal(handlers.has("runic-toolkit:state"), true);
  assert.equal(handlers.has("runic-toolkit:trace"), true);
  assert.equal(middleware[0][0], "/__runic-toolkit/state");
});

test("registers the official Vite DevTools dock, shared state, and command", async () => {
  const plugin = runicToolkit({
    devtools: false,
    contract: { identity: "sample", version: "1", fingerprint: "abc" },
  });
  const docks = [];
  const commands = [];
  const specs = [];
  await plugin.devtools.setup({
    rpc: {
      sharedState: {
        get: async (_key, options) => ({
          value: () => options.initialValue,
          mutate: () => undefined,
        }),
      },
    },
    createJsonRenderer: (spec) => {
      specs.push(spec);
      return { updateSpec: (next) => specs.push(next) };
    },
    docks: { register: (dock) => docks.push(dock) },
    commands: { register: (command) => commands.push(command) },
  });
  assert.equal(docks[0].id, "runic-toolkit:overview");
  assert.equal(docks[0].type, "json-render");
  assert.equal(commands[0].id, "runic-toolkit:copy-diagnostic-state");
  assert.equal(specs.length, 1);
});

test("starts with the official Vite DevTools server and validated JSON renderer", async () => {
  const server = await createServer({
    configFile: false,
    logLevel: "silent",
    plugins: [
      DevTools({ visibility: "passive" }),
      runicToolkit({
        contract: { identity: "sample", version: "1", fingerprint: "abc" },
      }),
    ],
    server: { host: "127.0.0.1", port: 0, strictPort: false },
  });
  try {
    await server.listen();
    const address = server.httpServer?.address();
    assert.ok(address && typeof address === "object");
    const response = await fetch(`http://127.0.0.1:${address.port}/__runic-toolkit/state`);
    assert.equal(response.status, 200);
    assert.equal((await response.json()).contract.identity, "sample");
  } finally {
    await server.close();
  }
});

test("sanitizes trace details before exposing them", () => {
  const plugin = runicToolkit({ devtools: false, maxTimelineEntries: 1 });
  const handlers = new Map();
  let latest;
  plugin.configureServer({
    ws: {
      on: (event, handler) => handlers.set(event, handler),
      send: (message) => { latest = message.data; },
    },
    middlewares: { use: () => undefined },
    httpServer: undefined,
  });
  handlers.get("runic-toolkit:trace")({
    kind: "command",
    label: "Navigate",
    detail: { target: "review", token: "must-not-escape", stack: "hidden" },
  });
  assert.deepEqual(latest.timeline[0].detail, { target: "review" });
});
