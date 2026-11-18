import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { bridgeSource, generated, render, scripts } from "../../eng/generate-page-scripts.mjs";

// The page scripts Runic.Desktop compiles in as UTF-8 data (runic-sdk#1109): eng/generate-page-scripts.mjs writes
// PageScripts.g.cs from Internal/*.js, with /webui.js's WebUiBridge.js minified.
const readable = readFileSync(bridgeSource, "utf8");
const served = (await scripts()).find(script => script.name === "WebUiBridge").text;
const placeholders = ["__TOKEN__", "__PORT__", "__BASE_PATH__", "__SESSION_CREDENTIAL__", "__CUSTOM_WINDOW_DRAG__",
  "__FRAME__", "__MEASURE_HEIGHT__"];

test("PageScripts.g.cs is the generator's output of the scripts", async () => {
  // Run bun eng/generate-page-scripts.mjs after changing a script in packages/dotnet/Runic.Desktop/Internal.
  expect(readFileSync(generated, "utf8")).toBe(await render());
});

test("the minified bridge keeps each placeholder that WebUiWindow.BuildBridgeScript replaces", () => {
  for (const placeholder of placeholders) {
    // Once each, in the configuration declarations at the top of the script.
    expect(served.split(placeholder).length - 1).toBe(1);
  }
});

// The same session on both scripts: the packets the page sends and the page API it defines are identical.
async function session(script) {
  const filled = script.replaceAll("__TOKEN__", "7").replaceAll("__PORT__", "8080").replaceAll("__BASE_PATH__", "")
    .replaceAll("__SESSION_CREDENTIAL__", '"credential"').replaceAll("__CUSTOM_WINDOW_DRAG__", "false")
    .replaceAll("__FRAME__", "false").replaceAll("__MEASURE_HEIGHT__", "false");
  const sent = [];
  let socket;
  const events = {};
  class Socket {
    static OPEN = 1;
    readyState = 1;
    handlers = {};
    constructor(url, protocols) { socket = this; sent.push(["open", String(url), protocols ?? null]); }
    addEventListener(name, callback) { this.handlers[name] = callback; }
    send(data) { sent.push(["send", Array.from(new Uint8Array(data.buffer ?? data, data.byteOffset ?? 0, data.byteLength))]); }
    close() { sent.push(["close"]); }
  }
  const location = { href: "http://127.0.0.1:8080/" };
  const context = {
    TextEncoder, TextDecoder, URL, WebSocket: Socket, location, window: { location },
    document: { addEventListener() {} }, navigation: { addEventListener(name, callback) { events[name] = callback; } },
    addEventListener() {}, setTimeout() {}, clearTimeout() {}, setInterval() {}, console,
  };
  runInNewContext(filled, context);
  socket.handlers.open?.({});
  const list = new TextEncoder().encode("__webui_core_api__,save,");
  const packet = new Uint8Array(9 + list.length);
  packet[0] = 0xDD;
  packet[7] = 0xF5;
  packet[8] = 1;
  packet.set(list, 9);
  socket.handlers.message({ data: packet.buffer });
  await context.webui.connected;
  context.webui.call("save", "näme", 42);
  await new Promise(resolve => setTimeout(resolve, 0));
  return { sent, api: Object.getOwnPropertyNames(context.webui).sort(), navigationEvents: Object.keys(events).sort() };
}

test("the minified bridge behaves as its source in a session", async () => {
  const fromSource = await session(readable);
  expect(fromSource.sent.length).toBeGreaterThan(1);
  expect(await session(served)).toEqual(fromSource);
});
