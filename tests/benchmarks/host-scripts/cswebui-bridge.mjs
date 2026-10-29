// Measures the CS-WebUI page bridge (packages/dotnet/Runic.Application.Views.CsWebUi/www/runic-cswebui.js)
// against fakes of WebUI's browser client, as tests/engineering/host-client-scripts.test.mjs drives it:
//
// - connection: the time from WebUI's connection (token accepted) to the hello that a connection listener sends,
//   and from the close to the rejection of a call in flight. "events" loads the script before webui.js creates
//   window.webui, as a page does, so WebUI reports to the script's event callback; "poll" loads it after, which
//   falls back to polling isConnected() every 250 ms, the only mode before 0.14.
// - mailbox: the time per message from a receive reply to a listener that parses each message, as the page's
//   runtime does, and how often the script itself parses and serializes. "sliced" is a reply as .NET writes it
//   (BridgeMessages.Receive); "parsed" is the same reply with one space, which the script parses and serializes
//   per message, as every reply was before 0.14.
//
// Run: bun tests/benchmarks/host-scripts/cswebui-bridge.mjs [--script <path>] [--rounds <n>]
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { parseArgs } from "node:util";
import { runInNewContext } from "node:vm";

const root = resolve(import.meta.dir, "../../..");
const { values } = parseArgs({ options: { script: { type: "string" }, rounds: { type: "string", default: "20" } } });
const source = readFileSync(resolve(root, values.script ?? "packages/dotnet/Runic.Application.Views.CsWebUi/www/runic-cswebui.js"), "utf8");
const rounds = Number(values.rounds);

const counts = { parse: 0, stringify: 0 };
const countingJson = {
  parse: (...args) => { counts.parse++; return JSON.parse(...args); },
  stringify: (...args) => { counts.stringify++; return JSON.stringify(...args); },
};

class WebuiBridge {
  #eventsCallback = null;
  #lastEvent = -1;
  event = { CONNECTED: 0, DISCONNECTED: 1 };
  online = false;
  calls = [];
  isConnected() { return this.online; }
  allowNavigation() {}
  call(name, ...args) {
    return new Promise((resolve, reject) => { this.calls.push({ message: args[0], at: performance.now(), resolve, reject }); });
  }
  setEventCallback(callback) { this.#eventsCallback = callback; }
  report(event) {
    if (this.#eventsCallback && event != this.#lastEvent) { this.#lastEvent = event; this.#eventsCallback(event); }
  }
  connect() { this.online = true; this.report(0); }
  disconnect() { this.online = false; this.report(1); }
}

// The pages' polls, stopped at the end.
const intervals = new Set();
const trackedInterval = (...args) => { const id = setInterval(...args); intervals.add(id); return id; };

function page({ events }) {
  const loaded = [];
  const context = {
    history: { pushState() {}, replaceState() {} }, setInterval: trackedInterval, clearInterval, setTimeout, clearTimeout, console,
    TextDecoder, URL, WebuiBridge, JSON: countingJson,
    document: { addEventListener: (type, listener) => { if (type === "DOMContentLoaded") loaded.push(listener); } },
  };
  context.window = context;
  context.globalThis = context;
  if (!events) context.webui = new WebuiBridge();
  runInNewContext(source, context);
  if (events) {
    context.webui = new WebuiBridge();
    for (const listener of loaded) listener();
  }
  return context;
}

const wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const settle = () => new Promise(resolve => setTimeout(resolve, 0));
const summary = samples => {
  const sorted = [...samples].sort((a, b) => a - b);
  const at = fraction => sorted[Math.min(sorted.length - 1, Math.floor(fraction * sorted.length))].toFixed(2);
  return `median ${at(0.5)} ms, p90 ${at(0.9)} ms, max ${sorted.at(-1).toFixed(2)} ms`;
};

async function connection(events) {
  const context = page({ events });
  const { webui, __runicBridge: bridge } = context;
  bridge.onConnectionChange(connected => { if (connected) void bridge.request('{"kind":"hello"}').catch(() => {}); });
  const toHello = [];
  const toRejection = [];
  for (let round = 0; round < rounds; round++) {
    // WebUI connects at any point of a poll interval.
    await wait(Math.random() * 250);
    const connected = performance.now();
    const before = webui.calls.length;
    webui.connect();
    while (!webui.calls.slice(before).some(call => call.message === '{"kind":"hello"}')) await wait(1);
    toHello.push(webui.calls.slice(before).find(call => call.message === '{"kind":"hello"}').at - connected);
    await wait(Math.random() * 250);
    let rejected;
    bridge.request('{"kind":"set"}').catch(() => { rejected = performance.now(); });
    const closed = performance.now();
    webui.disconnect();
    while (rejected === undefined) await wait(1);
    toRejection.push(rejected - closed);
  }
  return { toHello, toRejection };
}

const operation = index => ({ kind: "operation", route: "notes", operation: `o${index}`, status: { kind: "running", progress: index / 64 } });
const interaction = index => ({
  kind: "interaction", route: "notes", interaction: `i${index}`,
  request: { title: "Grüße   \"quoted\" {not} [json]", items: Array.from({ length: 40 }, (_, item) => ({ id: item, text: `Line ${item}` })) },
});

async function mailbox(layout, message, perReply, replies) {
  const context = page({ events: false });
  const { webui, __runicBridge: bridge } = context;
  webui.online = true;
  let delivered = 0;
  bridge.onMessage(text => { JSON.parse(text); delivered++; });
  const texts = Array.from({ length: perReply }, (_, index) => JSON.stringify(message(index)));
  const reply = layout === "sliced"
    ? `{"ok":true,"messages":[${texts.join(",")}]}`
    : `{"ok":true, "messages":[${texts.join(",")}]}`;
  const pending = () => webui.calls.findLast(call => call.message === '{"kind":"receive"}' && !call.done);
  for (let warmup = 0; warmup < 50; warmup++) { const call = pending(); call.done = true; call.resolve(reply); await settle(); }
  counts.parse = 0;
  counts.stringify = 0;
  delivered = 0;
  let elapsed = 0;
  for (let index = 0; index < replies; index++) {
    const call = pending();
    call.done = true;
    const started = performance.now();
    call.resolve(reply);
    await null; await null; await null;
    elapsed += performance.now() - started;
    await settle();
  }
  if (delivered !== perReply * replies) throw new Error(`Delivered ${delivered} of ${perReply * replies} messages.`);
  return {
    perMessage: elapsed * 1000 / delivered,
    parses: counts.parse / delivered + 1, // and the listener's own parse
    serializations: counts.stringify / delivered,
  };
}

console.log(`runic-cswebui.js connection (${rounds} rounds)`);
for (const events of [true, false]) {
  const { toHello, toRejection } = await connection(events);
  console.log(`  ${events ? "events" : "poll  "} connect -> hello: ${summary(toHello)}`);
  console.log(`  ${events ? "events" : "poll  "} close -> call rejected: ${summary(toRejection)}`);
}
console.log("runic-cswebui.js mailbox (parses include the listener's; time includes it)");
for (const [name, message, perReply] of [["operation", operation, 32], ["interaction", interaction, 8]]) {
  for (const layout of ["sliced", "parsed"]) {
    const result = await mailbox(layout, message, perReply, 400);
    console.log(`  ${name.padEnd(11)} ${layout}: ${result.perMessage.toFixed(2)} µs/message, ` +
      `${result.parses} parses and ${result.serializations} serializations per message`);
  }
}
for (const id of intervals) clearInterval(id);
