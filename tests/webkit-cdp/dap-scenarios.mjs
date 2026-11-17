// VS Code's JavaScript debugger (vscode-js-debug's standalone DAP server) against a CDP endpoint, driven as VS Code drives
// a `"type": "chrome", "request": "attach"` configuration: the parent session, then a child session per page
// (startDebugging), which gets the breakpoints again when a new page replaces its target. The scenarios use the runic-app
// template's React page under `dotnet runic dev` (#1029): breakpoints through Vite's source maps hit from a click, stepping,
// conditions, logpoints, exceptions, console output, locals and hover, a breakpoint in the first render after a reload,
// a breakpoint and a logpoint in a module that loads after thousands of others (#1051), HMR, the Window restarting
// under dotnet watch, and another client taking WebKit's inspector from the adapter (D068.1). Each prints PASS or FAIL
// with what it saw; the exit code is 0 when all pass.
// usage: node dap-scenarios.mjs --jsdebug <js-debug dir> --port <cdp port> --frontend <Frontend dir>
//          [--restart-file <a .cs file of the application>] [--inspector <WebKit inspector host:port>] [--modules <count>]
//          [--per-script-sourcemaps auto|yes|no] [--only a,b] [--trace <js-debug log file>]
// With --launch, js-debug instead launches Chrome at that address, as the template's `"request": "launch"` configuration
// "Window in Chrome" does for `dotnet runic dev --browser` (#1031): the same scenarios on Chromium.
//        node dap-scenarios.mjs --jsdebug <js-debug dir> --launch <url> --frontend <Frontend dir> [--browser <executable>] …
import { spawn } from "node:child_process";
import fs from "node:fs";
import net from "node:net";
import { parseArgs } from "node:util";

const { values: a } = parseArgs({ options: {
  jsdebug: { type: "string" }, port: { type: "string" }, frontend: { type: "string" }, only: { type: "string" }, trace: { type: "string" },
  // A C# file of the application: appending a comment makes dotnet watch (no hot reload under dev) restart the Window.
  "restart-file": { type: "string" },
  // The Window's WebKit inspector server (WEBKIT_INSPECTOR_SERVER), which another client connects to.
  inspector: { type: "string" },
  // The late-module scenario's page load: this many modules, each with its own inline source map, before the late one.
  modules: { type: "string", default: "2000" },
  // js-debug's perScriptSourcemaps. With "auto", once paused in Vite's client, js-debug stops where a file it has
  // breakpoints for starts instead of at the instrumentation pause before each script with a source map; "no" keeps that.
  "per-script-sourcemaps": { type: "string", default: "auto" },
  // The attach configuration's type: pwa-chrome ("chrome" in launch.json) or pwa-msedge with useWebView, the template's.
  type: { type: "string", default: "pwa-chrome" },
  // A launch configuration's url, and the Chrome or Chromium it starts: js-debug's installed Chrome ("stable") by default.
  launch: { type: "string" }, browser: { type: "string" },
} });
const app = `${a.frontend}/src/App.tsx`;
const counterPage = `${a.frontend}/src/pages/CounterPage.tsx`;
// The template's React page, found by its code rather than line numbers: the Counter button's handler, the first render's
// useView and a CounterPage local.
const lineOf = (file, marker) => {
  const index = fs.readFileSync(file, "utf8").split("\n").findIndex(line => line.includes(marker));
  if (index < 0) throw new Error(`${file} has no line with ${marker}`);
  return index + 1;
};
const clickLine = lineOf(app, 'navigate.run("showCounter")');
const renderLine = lineOf(app, "useView(workspace)");
const counterLine = lineOf(counterPage, "const error = increment.error");
const t0 = Date.now();
const log = (...x) => console.log(`[${((Date.now() - t0) / 1000).toFixed(2)}s]`, ...x);
// A port that the system has free, for js-debug's DAP server: a random one could be taken.
const dapPort = await new Promise((resolve, reject) => {
  const probe = net.createServer().once("error", reject).listen(0, "127.0.0.1", () => { const { port } = probe.address(); probe.close(() => resolve(port)); });
});
const server = spawn(process.execPath, [`${a.jsdebug}/src/dapDebugServer.js`, String(dapPort), "127.0.0.1"], { stdio: ["ignore", "ignore", "pipe"] });
// js-debug's DAP server reports a launch or attach it cannot do (no browser, no target) only here, without a response.
let reportRefusal;
const serverRefusal = new Promise(resolve => { reportRefusal = resolve; });
server.stderr.on("data", d => {
  process.stderr.write(`[js-debug] ${d}`);
  const refusal = /Unable to (?:launch browser|attach to browser)[^\n]*/.exec(String(d));
  if (refusal) reportRefusal({ message: refusal[0] });
});
const done = code => { try { server.kill(); } catch {} setTimeout(() => process.exit(code), 200); };
setTimeout(() => { log("overall timeout"); done(1); }, 900000);
// A failure before or between the scenarios (attach, page readiness) is a FAIL line too, not a bare exit code.
process.on("unhandledRejection", error => { log(`FAIL setup: ${error?.stack ?? error}`); done(1); });

const sleep = ms => new Promise(r => setTimeout(r, ms));
class Dap {
  constructor(name) { this.name = name; this.seq = 1; this.pending = new Map(); this.waiters = []; this.buf = Buffer.alloc(0); this.handlers = {}; this.outputs = []; this.breakpointEvents = []; }
  connectOnce() { return new Promise((res, rej) => { this.sock = net.connect(dapPort, "127.0.0.1", res); this.sock.once("error", rej); this.sock.on("data", d => { this.buf = Buffer.concat([this.buf, d]); this.drain(); }); }); }
  // js-debug's server takes a moment to listen, longer on a loaded machine: connect once it does.
  async connect() {
    for (const deadline = Date.now() + 60000; ;) {
      try { await this.connectOnce(); return; } catch (error) { if (Date.now() > deadline) throw error; await sleep(200); }
    }
  }
  drain() {
    for (;;) {
      const h = this.buf.indexOf("\r\n\r\n"); if (h < 0) return;
      const len = Number(/Content-Length: (\d+)/i.exec(this.buf.subarray(0, h).toString())[1]);
      if (this.buf.length < h + 4 + len) return;
      const m = JSON.parse(this.buf.subarray(h + 4, h + 4 + len).toString()); this.buf = this.buf.subarray(h + 4 + len);
      if (m.type === "response") { const p = this.pending.get(m.request_seq); this.pending.delete(m.request_seq); p?.(m); }
      else if (m.type === "event") {
        if (m.event === "output") this.outputs.push(m.body);
        if (m.event === "breakpoint") this.breakpointEvents.push(m.body.breakpoint);
        for (const w of [...this.waiters]) if (w.event === m.event && w.pred(m.body)) { this.waiters.splice(this.waiters.indexOf(w), 1); w.res(m.body); }
      } else if (m.type === "request") this.handlers[m.command]?.(m);
    }
  }
  write(o) { const s = JSON.stringify(o); this.sock.write(`Content-Length: ${Buffer.byteLength(s)}\r\n\r\n${s}`); }
  send(command, args = {}) { const m = { seq: this.seq++, type: "request", command, arguments: args }; this.write(m); return new Promise(res => this.pending.set(m.seq, res)); }
  reply(req, body = {}) { this.write({ seq: this.seq++, type: "response", request_seq: req.seq, command: req.command, success: true, body }); }
  wait(event, pred = () => true, ms = 15000) {
    return new Promise((res, rej) => { const w = { event, pred, res }; this.waiters.push(w); setTimeout(() => { const i = this.waiters.indexOf(w); if (i >= 0) { this.waiters.splice(i, 1); rej(new Error(`no ${event} in ${ms} ms`)); } }, ms); });
  }
  noEvent(event, ms) { return this.wait(event, () => true, ms).then(() => false, () => true); }
}
const init = { clientID: "runic-webkit-cdp", adapterID: a.type, linesStartAt1: true, columnsStartAt1: true, pathFormat: "path", supportsVariableType: true, supportsStartDebuggingRequest: true };

// Every page js-debug attaches to is a child session (startDebugging), as in VS Code. The first is the scenarios'
// session; a later one (another Window) gets the breakpoints VS Code would send it. A Window restart keeps the session.
const breakpoints = new Map();
let current = null;
let children = 0;
// Starts debugging as VS Code does: the parent session, then its first child session. With --launch, the first call
// launches Chrome at that address; otherwise, and for a later call (replaced-client), it attaches to the CDP port.
// A launch or attach that js-debug refuses, such as a browser it cannot start or attach to, starts no page session: each
// setup step then fails with js-debug's message instead of waiting for the overall timeout. The refusal is an
// unsuccessful response, or only a line on js-debug's standard error.
let launched = false;
async function attachIde() {
  const parent = new Dap("parent"); await parent.connect();
  let own = 0;
  const childP = new Promise(res => { parent.handlers.startDebugging = async req => {
    parent.reply(req); const child = new Dap(`child${++children}`); await child.connect();
    // js-debug can send "initialized" in the same read as the initialize response: wait for it before sending.
    const initialized = child.wait("initialized", () => true, 120000);
    await child.send("initialize", init);
    const attached = child.send("attach", req.arguments.configuration); await initialized;
    if (++own === 1) { res({ c: child, attached }); return; }
    for (const [path, bps] of breakpoints) await child.send("setBreakpoints", { source: { path }, breakpoints: bps });
    await child.send("configurationDone"); await attached;
    log(`child session ${children} attached (another page)`);
  }; });
  const parentInitialized = parent.wait("initialized", () => true, 120000);
  await parent.send("initialize", init);
  const launch = a.launch && !launched;
  launched = true;
  const pa = launch
    // Headless without a display; --no-sandbox for CI runners whose kernel refuses Chrome's sandbox.
    ? parent.send("launch", { type: a.type, request: "launch", name: "browser", url: a.launch, webRoot: a.frontend, timeout: 30000,
      perScriptSourcemaps: a["per-script-sourcemaps"], runtimeExecutable: a.browser ?? "stable", runtimeArgs: ["--headless=new", "--no-sandbox"],
      ...(a.trace ? { trace: { logFile: a.trace } } : {}) })
    : parent.send("attach", { type: a.type, request: "attach", name: "webkit-cdp", port: Number(a.port), webRoot: a.frontend, timeout: 15000,
      perScriptSourcemaps: a["per-script-sourcemaps"],
      ...(a.type === "pwa-msedge" ? { useWebView: true } : {}), ...(a.trace ? { trace: { logFile: a.trace } } : {}) });
  const refused = Promise.race([pa.then(response => response.success ? new Promise(() => {}) : response), serverRefusal]);
  const orRefused = async step => {
    const outcome = await Promise.race([step.then(value => ({ value })), refused.then(response => ({ response }))]);
    if (!outcome.response) return outcome.value;
    log(`FAIL setup: js-debug's ${launch ? "launch" : "attach"} failed: ${outcome.response.message ?? JSON.stringify(outcome.response.body)}`);
    done(1);
    return new Promise(() => {});
  };
  await orRefused(parentInitialized); await orRefused(parent.send("configurationDone")); await orRefused(pa);
  const first = await orRefused(childP);
  await first.c.send("configurationDone"); await first.attached;
  return { parent, child: first.c };
}
let { parent, child: firstChild } = await attachIde();
current = firstChild;
// The scenarios' child session.
const c = new Proxy({}, { get: (_, name) => { const v = current[name]; return typeof v === "function" ? v.bind(current) : v; } });
log("attached");

const counter = 'nav button:nth-of-type(2)';
const click = sel => c.send("evaluate", { expression: `setTimeout(() => document.querySelector(${JSON.stringify(sel)}).click(), 50), "ok"`, context: "repl" });
const top = async threadId => (await c.send("stackTrace", { threadId, levels: 1 })).body.stackFrames[0];
const where = f => `${f.name} ${(f.source?.path ?? f.source?.name ?? "?").replace(a.frontend + "/", "")}:${f.line}:${f.column}`;
// Sets a file's breakpoints, as VS Code does, and returns once js-debug reports each of them verified: bound to the
// page's script through its source map. A debugger user sees that before clicking; a slow machine takes longer.
// A file that the page has not loaded yet gets its breakpoints with verify false: js-debug binds them when it loads.
async function setBps(bps, path = app, { verify = true } = {}) {
  breakpoints.set(path, bps);
  const seen = current.breakpointEvents.length;
  const response = await c.send("setBreakpoints", { source: { path }, breakpoints: bps });
  const ids = verify ? (response.body?.breakpoints ?? []).filter(bp => !bp.verified).map(bp => bp.id) : [];
  for (const deadline = Date.now() + 30000; ids.length > 0;) {
    for (const event of current.breakpointEvents.slice(seen)) if (event.verified && ids.includes(event.id)) ids.splice(ids.indexOf(event.id), 1);
    if (ids.length === 0) break;
    if (Date.now() > deadline) throw new Error(`breakpoints ${ids.join(", ")} in ${path} were not verified within 30 s`);
    await sleep(100);
  }
  return response;
}
// Waits until an expression is true in the page, such as the template's Counter button being rendered and enabled.
async function until(expression, what, ms = 120000) {
  for (const deadline = Date.now() + ms; ;) {
    const result = await c.send("evaluate", { expression: `Boolean(${expression})`, context: "repl" });
    if (result.success && result.body.result === "true") return;
    if (Date.now() > deadline) throw new Error(`${what}: not within ${ms / 1000} s`);
    await sleep(250);
  }
}
// Saves a file as editors do, by renaming a complete copy over it: a development server that reads the file while a
// plain write has truncated it gets an empty module (Vite: "Could not Fast Refresh (export removed)").
function saveAtomically(path, text) {
  const temporary = `${path}.webkit-cdp-${process.pid}.tmp`;
  fs.writeFileSync(temporary, text);
  fs.renameSync(temporary, path);
}
const counterReady = `document.querySelector(${JSON.stringify(counter)})?.disabled === false`;
// Waits until the page has stopped loading: the Counter button works, and the same document keeps it for a while. Vite
// reloads the page again for each batch of deleted modules that were in its graph, and the next scenario's breakpoints
// must not arrive in the middle of such a load: before js-debug has the source map of the page's App.tsx, it sets a
// breakpoint at its source line in the served JavaScript, which is not where the source line runs (#1108).
async function settled(what, ms = 120000) {
  for (const deadline = Date.now() + ms; ;) {
    await until(counterReady, what, Math.max(deadline - Date.now(), 1000));
    const mark = `__webkitCdpSettled${Date.now()}`;
    await c.send("evaluate", { expression: `globalThis.${mark} = true`, context: "repl" });
    await sleep(2000);
    const result = await c.send("evaluate", { expression: `Boolean(globalThis.${mark} && ${counterReady})`, context: "repl" });
    if (result.success && result.body.result === "true") return;
    if (Date.now() > deadline) throw new Error(`${what}: the page kept loading for ${ms / 1000} s`);
  }
}
// A plain client of WebKit's inspector server (WEBKIT_INSPECTOR_SERVER), as GNOME Web's inspector:// connects: it sets
// up, which gives it WebKit's single client slot, and resolves once the server described its protocol.
function inspectorClient(address) {
  const colon = address.lastIndexOf(":");
  const socket = net.connect(Number(address.slice(colon + 1)), address.slice(0, colon).replace(/^\[|\]$/g, ""));
  const name = Buffer.from("SetupInspectorClient\0");
  const header = Buffer.alloc(5);
  header.writeUInt32BE(name.length + 1);
  header[4] = 1; // little-endian parameters
  return new Promise((resolve, reject) => {
    let received = Buffer.alloc(0);
    socket.once("error", reject);
    socket.once("connect", () => socket.write(Buffer.concat([header, name, Buffer.from([0])])));
    socket.on("data", data => {
      received = Buffer.concat([received, data]);
      if (received.length >= 5 + 24 && received.subarray(5, 5 + 23).toString() === "DidSetupInspectorClient") resolve(socket);
    });
  });
}
const results = [];
// Each scenario's wall-clock window in the adapter trace's format (HH:mm:ss.fff, local time), for its failures.
const clock = () => { const d = new Date(); return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}:${String(d.getSeconds()).padStart(2, "0")}.${String(d.getMilliseconds()).padStart(3, "0")}`; };
// The page loads on, and the Window's Bridge connects, after dev's [dev] Ready: start once the Counter button works.
const setupFailed = error => { log(`FAIL setup: ${error?.message ?? error}`); done(1); return new Promise(() => {}); };
await until(counterReady, "the page's Counter button").catch(setupFailed);
log("page ready");

const scenarios = {
  async "step-in-out"() {
    await setBps([{ line: clickLine }]);
    const s = c.wait("stopped"); await click(counter); const st = await s;
    const f0 = await top(st.threadId);
    let p = c.wait("stopped"); await c.send("stepIn", { threadId: st.threadId }); const f1 = await top((await p).threadId);
    p = c.wait("stopped"); await c.send("stepOut", { threadId: st.threadId }); const f2 = await top((await p).threadId);
    await setBps([]); await c.send("continue", { threadId: st.threadId });
    return [f1.name !== f0.name || f1.source?.path !== f0.source?.path, `at ${where(f0)}; stepIn -> ${where(f1)}; stepOut -> ${where(f2)}`];
  },
  async conditional() {
    await setBps([{ line: clickLine, condition: "1 === 2" }]);
    await click(counter); const quiet = await c.noEvent("stopped", 2000);
    await setBps([{ line: clickLine, condition: "client !== undefined" }]);
    const s = c.wait("stopped", () => true, 5000); await click(counter); const st = await s.catch(() => null);
    await setBps([]); if (st) await c.send("continue", { threadId: st.threadId });
    return [quiet && !!st, `false condition: ${quiet ? "no stop" : "STOPPED"}; true condition: ${st ? "stopped" : "no stop"}`];
  },
  async logpoint() {
    await setBps([{ line: clickLine, logMessage: "webkit-cdp-logpoint client={client !== undefined}" }]);
    const o = c.wait("output", b => b.output.includes("webkit-cdp-logpoint"), 5000); const quiet = c.noEvent("stopped", 2000);
    await click(counter); const out = await o.catch(() => null); const noStop = await quiet;
    await setBps([]);
    return [!!out && noStop, `output ${JSON.stringify(out?.output?.trim())}, ${noStop ? "no stop" : "STOPPED"}`];
  },
  async exception() {
    await c.send("setExceptionBreakpoints", { filters: ["uncaught"] });
    const s = c.wait("stopped", () => true, 5000);
    await c.send("evaluate", { expression: `setTimeout(() => { throw new Error("webkit-cdp-boom"); }, 50), "ok"`, context: "repl" });
    const st = await s.catch(() => null);
    let info = null;
    if (st) { info = await c.send("exceptionInfo", { threadId: st.threadId }); await c.send("continue", { threadId: st.threadId }); }
    await c.send("setExceptionBreakpoints", { filters: [] });
    const out = await c.wait("output", b => b.output.includes("webkit-cdp-boom"), 3000).catch(() => null);
    return [st?.reason === "exception", `stopped=${st?.reason}; exceptionInfo=${JSON.stringify(info?.body?.description ?? info?.message)}; output=${JSON.stringify(out?.output?.split("\n")[0])}`];
  },
  async "uncaught-output"() {
    const out = c.wait("output", b => b.output.includes("webkit-cdp-uncaught"), 4000);
    await c.send("evaluate", { expression: `setTimeout(() => { throw new Error("webkit-cdp-uncaught"); }, 50), "ok"`, context: "repl" });
    const o = await out.catch(() => null);
    return [!!o, `output[${o?.category}] ${JSON.stringify(o?.output?.split("\n").slice(0, 2).join(" / "))}`];
  },
  async "console-levels"() {
    const want = ["webkit-cdp-warn", "webkit-cdp-error", "webkit-cdp-info", "webkit-cdp-obj"];
    const got = want.map(w => c.wait("output", b => b.output.includes(w) || JSON.stringify(b).includes(w), 4000).then(b => `${w}:${b.category}`, () => `${w}:MISSING`));
    await c.send("evaluate", { expression: `console.warn("webkit-cdp-warn"); console.error("webkit-cdp-error"); console.info("webkit-cdp-info"); console.log({ tag: "webkit-cdp-obj", n: 2 }); "ok"`, context: "repl" });
    const r = await Promise.all(got);
    return [r.every(x => !x.endsWith("MISSING")), r.join(", ")];
  },
  async "set-variable"() {
    // The Welcome View first, so a click on Counter renders CounterPage, where the breakpoint is.
    await click('nav button:nth-of-type(1)'); await until(`!document.querySelector(".count")`, "the Welcome View");
    await setBps([{ line: counterLine }], counterPage);
    await until(counterReady, "the Counter button");
    const s = c.wait("stopped", () => true, 15000);
    await click(counter);
    const st = await s.catch(() => null);
    if (!st) { await setBps([], counterPage); return [false, "no stop in CounterPage"]; }
    const f = await top(st.threadId);
    const scopes = (await c.send("scopes", { frameId: f.id })).body.scopes;
    const local = scopes.find(x => /local/i.test(x.name));
    const vars = (await c.send("variables", { variablesReference: local.variablesReference })).body.variables;
    const names = vars.map(v => `${v.name}=${v.value.slice(0, 40)}`);
    const sv = await c.send("setVariable", { variablesReference: local.variablesReference, name: "error", value: "42" });
    const check = await c.send("evaluate", { expression: "error", frameId: f.id, context: "repl" });
    const hover = await c.send("evaluate", { expression: "counter.state?.count", frameId: f.id, context: "hover" });
    await setBps([], counterPage);
    await c.send("continue", { threadId: st.threadId });
    return [names.length > 0, `stopped at ${where(f)}; locals ${names.slice(0, 6).join(" | ")}; setVariable error=42 -> ${sv.success ? sv.body.value : "FAILED: " + sv.message}, read back ${check.body?.result}; hover counter.state?.count -> ${hover.success ? hover.body.result : hover.message}`];
  },
  async reload() {
    // App() renders after a reload: line 21 runs during the page's first render.
    await setBps([{ line: renderLine }]);
    const s = c.wait("stopped", () => true, 15000);
    await c.send("evaluate", { expression: `setTimeout(() => location.reload(), 50), "ok"`, context: "repl" });
    const st = await s.catch(() => null);
    const f = st ? await top(st.threadId) : null;
    await setBps([]); if (st) await c.send("continue", { threadId: st.threadId });
    await until(counterReady, "the reloaded page's Counter button");
    // After the reload, a click breakpoint must still work.
    await setBps([{ line: clickLine }]);
    const s2 = c.wait("stopped", () => true, 6000); await click(counter); const st2 = await s2.catch(() => null);
    const f2 = st2 ? await top(st2.threadId) : null;
    await setBps([]); if (st2) await c.send("continue", { threadId: st2.threadId });
    return [!!st2, `first render after reload: ${f ? "stopped at " + where(f) : "NOT stopped"}; click after reload: ${f2 ? "stopped at " + where(f2) : "NOT stopped"}`];
  },
  async "late-module"() {
    // A page load as in a large Vite app (#1051): the template's main module imports a module that imports thousands of
    // others, each with its own inline source map, and then imports a second group dynamically. In that group the late
    // module runs first, but WebKit parses 200 more modules after it. The late module's breakpoint, on its first
    // statement, and logpoint must work on its first execution, at their source lines: js-debug sets them through the
    // module's source map before the module runs, at the module's own instrumentation pause, or, once it paused in
    // Vite's client, where a module it has breakpoints for starts. An instrumentation pause that stood for the newest
    // module of the group missed the late one. The page has not loaded the module before the first load, and has its
    // old source map before the reload.
    // js-debug stops at the start of a file it has breakpoints for by a pattern of its name: no other module has "late".
    const directory = `${a.frontend}/src/webkit-cdp-batch`;
    const main = `${a.frontend}/src/main.tsx`;
    const count = Number(a.modules);
    const late = `${directory}/late.ts`;
    // Without the import that an interrupted run may have left.
    const imported = `\nimport "./webkit-cdp-batch/entry";\n`;
    const original = fs.readFileSync(main, "utf8").replaceAll(imported, "");
    fs.rmSync(directory, { recursive: true, force: true });
    fs.mkdirSync(directory);
    try {
      for (let i = 0; i < count; i++)
        fs.writeFileSync(`${directory}/m${i}.ts`, `// One of the late-module scenario's modules.\ntype Value = number;\nexport const v${i}: Value = ${i};\n`);
      fs.writeFileSync(`${directory}/many.ts`, Array.from({ length: count }, (_, i) => `import { v${i} } from "./m${i}";`).join("\n") +
        `\nexport const total: number = ${Array.from({ length: count }, (_, i) => `v${i}`).join(" + ") || "0"};\n`);
      const tail = 200;
      for (let i = 0; i < tail; i++)
        fs.writeFileSync(`${directory}/t${i}.ts`, `// One of the modules parsed after the late one.\ntype Value = number;\nexport const t${i}: Value = ${i};\n`);
      fs.writeFileSync(`${directory}/tail.ts`, Array.from({ length: tail }, (_, i) => `import { t${i} } from "./t${i}";`).join("\n") +
        `\nexport const tailTotal: number = ${Array.from({ length: tail }, (_, i) => `t${i}`).join(" + ")};\n`);
      fs.writeFileSync(`${directory}/group.ts`, `import "./late";\nimport { tailTotal } from "./tail";\n(globalThis as Record<string, unknown>).__webkitCdpTail = tailTotal;\n`);
      fs.writeFileSync(`${directory}/entry.ts`, `import { total } from "./many";\n(globalThis as Record<string, unknown>).__webkitCdpMany = total;\nvoid import("./group");\n`);
      // TypeScript lines before the marked ones, so that their lines in the served JavaScript differ.
      fs.writeFileSync(late, [
        "// The late-module scenario's module, imported after the others.",
        "interface Late {",
        "  value: number;",
        "  label: string;",
        "}",
        "type Doubled = number;",
        // The module's first statement, where WebKit stops for the instrumentation pause, has the breakpoint.
        `const late: Late = { value: 21, label: "late" }; // webkit-cdp-late-breakpoint`,
        "const doubled: Doubled = late.value * 2; // webkit-cdp-late-logpoint",
        "(globalThis as Record<string, unknown>).__webkitCdpLate = { doubled, at: performance.now() };",
        "export {};",
        "",
      ].join("\n"));
      const logLine = lineOf(late, "webkit-cdp-late-logpoint");
      const breakLine = lineOf(late, "webkit-cdp-late-breakpoint");
      await setBps([{ line: logLine, logMessage: "webkit-cdp-late value={late.value}" }, { line: breakLine }], late, { verify: false });
      const mapped = f => f?.source?.path === late;
      // Stopped at the breakpoint after a page load; the logpoint's output and where js-debug places it.
      const load = async (what, start) => {
        const output = c.wait("output", b => b.output.includes("webkit-cdp-late value=21"), 120000).catch(() => null);
        const stopped = c.wait("stopped", () => true, 120000).catch(() => null);
        await start();
        const st = await stopped;
        const f = st ? await top(st.threadId) : null;
        if (st) await c.send("continue", { threadId: st.threadId });
        const out = await output;
        await until(`globalThis.__webkitCdpLate?.doubled === 42 && ${counterReady}`, `the page after ${what}`, 120000);
        const at = Number((await c.send("evaluate", { expression: "Math.round(globalThis.__webkitCdpLate.at)", context: "repl" })).body?.result);
        const ok = mapped(f) && f.line === breakLine && out?.source?.path === late && out.line === logLine;
        return [ok, `${what}: ${f ? "stopped at " + where(f) : "NOT stopped"}; logpoint ${out ? `at ${(out.source?.path ?? "?").replace(a.frontend + "/", "")}:${out.line}` : "MISSING"}; late module ran ${at} ms after navigation`];
      };
      // The first load of the modules: Vite reloads the page for the changed main module.
      const [first, firstDetail] = await load("first load", async () => saveAtomically(main, original + imported));
      const [again, againDetail] = await load("reload", () => c.send("evaluate", { expression: `setTimeout(() => location.reload(), 50), "ok"`, context: "repl" }));
      await setBps([], late, { verify: false });
      return [first && again, `${count} + ${tail + 3} modules; expected ${breakLine} and logpoint ${logLine}; ${firstDetail}; ${againDetail}`];
    } finally {
      saveAtomically(main, original);
      await until(`!globalThis.__webkitCdpMany && ${counterReady}`, "the page without the late module", 300000).catch(() => null);
      // Vite reloads the page again for the deleted modules: the next scenario starts once it has.
      fs.rmSync(directory, { recursive: true, force: true });
      await settled("the page after the late module's files were deleted", 300000).catch(() => null);
    }
  },
  async "window-restart"() {
    if (!a["restart-file"]) return [true, "skipped (no --restart-file)"];
    // The adapter gives the restarted Window's page the breakpoints as js-debug set them on this one, so js-debug must
    // set them on a loaded page, through App.tsx's source map.
    await settled("the page before the restart");
    await setBps([{ line: clickLine }]);
    // A mark in this Window's page: the restarted Window's page does not have it.
    await c.send("evaluate", { expression: "globalThis.__webkitCdpBeforeRestart = true", context: "repl" });
    fs.appendFileSync(a["restart-file"], `\n// webkit-cdp scenario: restart ${Date.now()}\n`);
    // The same js-debug session, without a new attach: the adapter continues it on the new Window's page.
    await until(`!globalThis.__webkitCdpBeforeRestart && ${counterReady}`, "the restarted Window's page", 300000);
    const s = c.wait("stopped", () => true, 15000);
    await click(counter);
    const st = await s.catch(() => null);
    const f = st ? await top(st.threadId) : null;
    await setBps([]); if (st) await c.send("continue", { threadId: st.threadId });
    return [!!st, `after the Window restarted: ${f ? "stopped at " + where(f) : "NOT stopped"}`];
  },
  async hmr() {
    const original = fs.readFileSync(app, "utf8");
    if (!original.includes("<h1>Runic Views</h1>")) throw new Error("App.tsx has no <h1>Runic Views</h1> to edit");
    try {
      saveAtomically(app, original.replace("<h1>Runic Views</h1>", "<h1>Runic Views (edited)</h1>"));
      // Vite's HMR update has reached the page: React rendered the edited module.
      await until(`document.querySelector("header h1")?.textContent.includes("(edited)")`, "the HMR update");
      await setBps([{ line: clickLine }]);
      const s = c.wait("stopped", () => true, 15000); await click(counter); const st = await s.catch(() => null);
      const f = st ? await top(st.threadId) : null;
      let text = null;
      if (st) { text = await c.send("evaluate", { expression: `document.querySelector("header h1").textContent`, frameId: f.id, context: "repl" }); }
      await setBps([]); if (st) await c.send("continue", { threadId: st.threadId });
      return [!!st && text?.body?.result?.includes("edited"), `after HMR edit: ${f ? "stopped at " + where(f) : "NOT stopped"}; header ${text?.body?.result}`];
    } finally {
      saveAtomically(app, original);
      await until(`!document.querySelector("header h1")?.textContent.includes("(edited)")`, "the HMR update back").catch(() => null);
    }
  },
  async "replaced-client"() {
    // D068.1: another local client takes WebKit's single client slot during the session and leaves. WebKit tells the
    // adapter nothing, and the adapter's next Setup on its old connection would crash the Window. The debugger must be
    // told that its page detached, the Window must keep running, and attaching again must work.
    if (!a.inspector) return [true, "skipped (no --inspector)"];
    await c.send("evaluate", { expression: "globalThis.__webkitCdpBeforeReplace = true", context: "repl" });
    const old = { parent, child: current };
    const terminated = old.child.wait("terminated", () => true, 30000).then(() => true, () => false);
    const other = await inspectorClient(a.inspector);
    const told = await terminated;
    await sleep(1000);
    other.destroy();
    await sleep(1500);
    // The developer starts debugging again, as with F5 after the session ended.
    await Promise.race([old.parent.send("disconnect", {}), sleep(3000)]);
    ({ parent, child: current } = await attachIde());
    await until(counterReady, "the page after attaching again");
    const same = (await c.send("evaluate", { expression: "globalThis.__webkitCdpBeforeReplace === true", context: "repl" })).body?.result === "true";
    await setBps([{ line: clickLine }]);
    const s = c.wait("stopped", () => true, 15000); await click(counter); const st = await s.catch(() => null);
    const f = st ? await top(st.threadId) : null;
    await setBps([]); if (st) await c.send("continue", { threadId: st.threadId });
    return [told && same && !!st, `the debugger was told it detached: ${told}; the same Window after the other client left: ${same}; ` +
      `breakpoint after attaching again: ${f ? "stopped at " + where(f) : "NOT stopped"}`];
  },
};

for (const [name, run] of Object.entries(scenarios)) {
  if (a.only && !a.only.split(",").includes(name)) continue;
  const start = clock();
  let ok = false;
  try { const [passed, detail] = await run(); ok = passed; log(`${ok ? "PASS" : "FAIL"} ${name}: ${detail}`); }
  catch (e) { log(`FAIL ${name}: ${e.message}`); }
  results.push(ok);
  if (!ok) {
    console.log(`SCENARIO_WINDOW|${name}|${start}|${clock()}`);
    // A failed scenario must not leave the page paused or with its breakpoints for the next one.
    for (const path of [...breakpoints.keys()]) await setBps([], path).catch(() => null);
    await c.send("setExceptionBreakpoints", { filters: [] }).catch(() => null);
    await c.send("continue", { threadId: 0 }).catch(() => null);
  }
  await new Promise(r => setTimeout(r, 500));
}
await Promise.race([c.send("disconnect", {}), new Promise(r => setTimeout(r, 3000))]).catch(() => {});
log(`${results.filter(Boolean).length}/${results.length} passed`);
done(results.every(Boolean) ? 0 : 1);
