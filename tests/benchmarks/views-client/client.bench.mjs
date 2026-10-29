// Benchmarks of the @runic-artifex/views client: frames of keyed collections, large inserts and subscriber
// notifications. Runs the built package (`bun run --cwd packages/web/views build` first) in any JavaScript engine
// the repo has: `bun tests/benchmarks/views-client/client.bench.mjs` (JavaScriptCore) and
// `node tests/benchmarks/views-client/client.bench.mjs` (V8, as in WebView2 and Chromium). Pass `--json` for
// machine-readable output.
//
// The host bridge is an in-memory one that delivers each push synchronously, as one WebSocket message is one task
// in a browser, and an animation frame is a fake one the benchmark runs by hand.
import { connectView, defineCollection, defineCollections } from "../../../packages/web/views/dist/generated.js";

const json = process.argv.includes("--json");
const engine = typeof Bun === "object" ? `bun ${Bun.version} (JavaScriptCore)` : `node ${process.versions.node} (V8 ${process.versions.v8})`;

// --------------------------------------------------------------- host bridge and animation frames

const frames = [];
globalThis.requestAnimationFrame = callback => { frames.push(callback); return frames.length; };
globalThis.cancelAnimationFrame = handle => { frames[handle - 1] = undefined; };
/** Runs the animation frame callbacks requested so far, as a browser does once per display refresh. */
function animationFrame() {
  const due = frames.splice(0);
  for (const callback of due) callback?.(performance.now());
}

let listener;
let states = new Map();
globalThis.__runicBridge = {
  isConnected: () => true,
  onConnectionChange(report) { report(true); return () => {}; },
  onMessage(next) { listener = next; return () => { listener = undefined; }; },
  async request(text) {
    const message = JSON.parse(text);
    if (message.kind === "hello") return JSON.stringify({ ok: true, protocol: 4, implementation: { name: "bench", version: "0" }, features: [] });
    if (message.kind === "open") return JSON.stringify({ ok: true, presentation: "ok", revision: 0, state: states.get(message.route) });
    if (message.kind === "close") return JSON.stringify({ ok: true });
    return JSON.stringify({ ok: false, error: { kind: "rejected", message: `unexpected ${message.kind}` } });
  },
};
const push = message => listener(JSON.stringify(message));

// --------------------------------------------------------------- the View

const decodeRow = wire => {
  if (wire === null || typeof wire !== "object" || typeof wire.id !== "number" || typeof wire.label !== "string") throw new Error("Invalid row.");
  return { id: wire.id, label: wire.label };
};
const collections = defineCollections({ rows: defineCollection(decodeRow, row => String(row.id)) });
const members = {
  rows: wire => { if (!Array.isArray(wire)) throw new Error("Not an array."); return wire.map(decodeRow); },
  title: wire => { if (typeof wire !== "string") throw new Error("Not a string."); return wire; },
};
const rowsOf = (count, start = 0) => Array.from({ length: count }, (_, offset) => ({ id: start + offset, label: `row ${start + offset}` }));
let routes = 0;

async function connect(rows) {
  const route = `bench${++routes}`;
  states.set(route, { rows, title: "bench" });
  const client = await connectView({ contract: "Bench:fingerprint", route, members, collections });
  return { client, route, revision: 0 };
}

const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
const results = [];
function report(name, value, unit, note = "") {
  results.push({ name, value, unit, ...(note ? { note } : {}) });
  if (!json) console.log(`${name.padEnd(46)} ${typeof value !== "number" || Number.isInteger(value) ? value : value.toFixed(value < 10 ? 3 : 1)} ${unit}${note ? `  (${note})` : ""}`);
}

// --------------------------------------------------------------- 1. one-row replace in a large collection

for (const size of [10_000, 100_000]) {
  const view = await connect(rowsOf(size));
  const iterations = size >= 100_000 ? 60 : 300;
  const times = [];
  for (let iteration = 0; iteration < iterations + 10; iteration++) {
    const at = (iteration * 7919) % size;
    const started = performance.now();
    push({
      kind: "frame", route: view.route, baseRevision: view.revision, revision: ++view.revision,
      changes: [{ field: "rows", kind: "replace", index: at, oldIndex: at, keys: [String(at)], items: [{ id: at, label: `edited ${iteration}` }] }],
    });
    if (iteration >= 10) times.push(performance.now() - started);
  }
  animationFrame();
  if (view.client.snapshot.rows.length !== size) throw new Error("The replace benchmark lost rows.");
  report(`replace one row, ${size.toLocaleString("en")} rows`, median(times), "ms/frame");
  view.client.dispose();
}

// --------------------------------------------------------------- 2. one-row add in a large collection

{
  const size = 100_000;
  const view = await connect(rowsOf(size));
  const times = [];
  for (let iteration = 0; iteration < 70; iteration++) {
    const id = size + iteration;
    const started = performance.now();
    push({
      kind: "frame", route: view.route, baseRevision: view.revision, revision: ++view.revision,
      changes: [{ field: "rows", kind: "add", index: 0, oldIndex: -1, keys: [String(id)], items: [{ id, label: `added ${id}` }] }],
    });
    if (iteration >= 10) times.push(performance.now() - started);
  }
  animationFrame();
  report(`add one row, ${size.toLocaleString("en")} rows`, median(times), "ms/frame");
  view.client.dispose();
}

// --------------------------------------------------------------- 3. inserting 150,000 rows in one change

{
  const count = 150_000;
  const view = await connect(rowsOf(10));
  const reported = [];
  const previous = globalThis.reportError;
  globalThis.reportError = error => { reported.push(error); };
  const started = performance.now();
  push({
    kind: "frame", route: view.route, baseRevision: view.revision, revision: ++view.revision,
    changes: [{ field: "rows", kind: "add", index: 5, oldIndex: -1, keys: rowsOf(count, 1000).map(row => String(row.id)), items: rowsOf(count, 1000) }],
  });
  const elapsed = performance.now() - started;
  globalThis.reportError = previous;
  animationFrame();
  const applied = view.client.snapshot.rows.length === count + 10;
  report(`insert ${count.toLocaleString("en")} rows in one change`, applied ? elapsed : "failed", applied ? "ms" : "",
    applied ? "" : `${String(reported[0]?.cause ?? reported[0]).split("\n")[0]}; the client recovers with a snapshot`);
  view.client.dispose();
}

// --------------------------------------------------------------- 4. subscriber notifications for a burst of frames

{
  const burst = 200;
  const view = await connect(rowsOf(1_000));
  let notifications = 0;
  // A subscriber that renders: a framework binding schedules work for each notification.
  let rendered = 0;
  view.client.subscribe(state => { notifications++; for (const row of state.rows) rendered += row.id & 1; });
  notifications = 0;
  const started = performance.now();
  for (let index = 0; index < burst; index++) {
    push({ kind: "frame", route: view.route, baseRevision: view.revision, revision: ++view.revision, changes: [{ field: "title", kind: "set", value: `title ${index}` }] });
  }
  animationFrame();
  const elapsed = performance.now() - started;
  if (view.client.snapshot.title !== `title ${burst - 1}`) throw new Error("The notification benchmark lost a frame.");
  report(`${burst} frames in one animation frame`, notifications, "notifications");
  report(`${burst} frames in one animation frame, total`, elapsed, "ms", rendered > 0 ? "subscriber reads 1,000 rows" : "");
  view.client.dispose();
}

if (json) console.log(JSON.stringify({ engine, results }, null, 2));
else console.log(`\n${engine}`);
process.exit(0);
