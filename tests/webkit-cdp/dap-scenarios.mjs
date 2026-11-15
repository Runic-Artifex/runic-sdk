// VS Code's JavaScript debugger (vscode-js-debug's standalone DAP server) against a CDP endpoint, driven as VS Code drives
// a `"type": "chrome", "request": "attach"` configuration: the parent session, then a child session per page
// (startDebugging), which gets the breakpoints again when a new page replaces its target. The scenarios use the runic-app
// template's React page under `dotnet runic dev` (#1029): breakpoints through Vite's source maps hit from a click, stepping,
// conditions, logpoints, exceptions, console output, locals and hover, a breakpoint in the first render after a reload,
// HMR, and the Window restarting under dotnet watch. Each prints PASS or FAIL with what it saw; the exit code is 0 when
// all pass.
// usage: node dap-scenarios.mjs --jsdebug <js-debug dir> --port <cdp port> --frontend <Frontend dir>
//          [--restart-file <a .cs file of the application>] [--only a,b] [--trace <js-debug log file>]
import { spawn } from "node:child_process";
import fs from "node:fs";
import net from "node:net";
import { parseArgs } from "node:util";

const { values: a } = parseArgs({ options: {
  jsdebug: { type: "string" }, port: { type: "string" }, frontend: { type: "string" }, only: { type: "string" }, trace: { type: "string" },
  // A C# file of the application: appending a comment makes dotnet watch (no hot reload under dev) restart the Window.
  "restart-file": { type: "string" },
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
const dapPort = 42000 + Math.floor(Math.random() * 1000);
const server = spawn(process.execPath, [`${a.jsdebug}/src/dapDebugServer.js`, String(dapPort), "127.0.0.1"], { stdio: ["ignore", "ignore", "pipe"] });
server.stderr.on("data", d => process.stderr.write(`[js-debug] ${d}`));
const done = code => { try { server.kill(); } catch {} setTimeout(() => process.exit(code), 200); };
setTimeout(() => { log("overall timeout"); done(1); }, 240000);

class Dap {
  constructor(name) { this.name = name; this.seq = 1; this.pending = new Map(); this.waiters = []; this.buf = Buffer.alloc(0); this.handlers = {}; this.outputs = []; }
  connect() { return new Promise((res, rej) => { this.sock = net.connect(dapPort, "127.0.0.1", res); this.sock.on("error", rej); this.sock.on("data", d => { this.buf = Buffer.concat([this.buf, d]); this.drain(); }); }); }
  drain() {
    for (;;) {
      const h = this.buf.indexOf("\r\n\r\n"); if (h < 0) return;
      const len = Number(/Content-Length: (\d+)/i.exec(this.buf.subarray(0, h).toString())[1]);
      if (this.buf.length < h + 4 + len) return;
      const m = JSON.parse(this.buf.subarray(h + 4, h + 4 + len).toString()); this.buf = this.buf.subarray(h + 4 + len);
      if (m.type === "response") { const p = this.pending.get(m.request_seq); this.pending.delete(m.request_seq); p?.(m); }
      else if (m.type === "event") {
        if (m.event === "output") this.outputs.push(m.body);
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
const init = { clientID: "runic-webkit-cdp", adapterID: "pwa-chrome", linesStartAt1: true, columnsStartAt1: true, pathFormat: "path", supportsVariableType: true, supportsStartDebuggingRequest: true };

await new Promise(r => setTimeout(r, 700));
const parent = new Dap("parent"); await parent.connect();
// Every page js-debug attaches to is a child session (startDebugging), as in VS Code. The first is the scenarios'
// session; a later one (the Window restarted) gets the breakpoints VS Code would send it and becomes the current one.
const breakpoints = new Map();
let current = null;
let children = 0;
const childP = new Promise(res => { parent.handlers.startDebugging = async req => {
  parent.reply(req); const child = new Dap(`child${++children}`); await child.connect(); await child.send("initialize", init);
  const attached = child.send("attach", req.arguments.configuration); await child.wait("initialized");
  if (children === 1) { res({ c: child, attached }); return; }
  for (const [path, bps] of breakpoints) await child.send("setBreakpoints", { source: { path }, breakpoints: bps });
  await child.send("configurationDone"); await attached;
  log(`child session ${children} attached (a new page)`);
  current = child;
}; });
await parent.send("initialize", init);
const pa = parent.send("attach", { type: "pwa-chrome", request: "attach", name: "webkit-cdp", port: Number(a.port), address: "127.0.0.1", webRoot: a.frontend, timeout: 15000, ...(a.trace ? { trace: { logFile: a.trace } } : {}) });
await parent.wait("initialized").catch(() => {}); await parent.send("configurationDone"); await pa;
const first = await childP;
current = first.c;
await current.send("configurationDone"); await first.attached;
// The current child session: scenarios use `c`, which follows a new page after a Window restart.
const c = new Proxy({}, { get: (_, name) => { const v = current[name]; return typeof v === "function" ? v.bind(current) : v; } });
log("attached");

const counter = 'nav button:nth-of-type(2)';
const click = sel => c.send("evaluate", { expression: `setTimeout(() => document.querySelector(${JSON.stringify(sel)}).click(), 50), "ok"`, context: "repl" });
const top = async threadId => (await c.send("stackTrace", { threadId, levels: 1 })).body.stackFrames[0];
const where = f => `${f.name} ${(f.source?.path ?? f.source?.name ?? "?").replace(a.frontend + "/", "")}:${f.line}:${f.column}`;
const setBps = (bps, path = app) => { breakpoints.set(path, bps); return c.send("setBreakpoints", { source: { path }, breakpoints: bps }); };
const results = [];

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
    await setBps([{ line: counterLine }], counterPage);
    const s = c.wait("stopped", () => true, 6000);
    await click('nav button:nth-of-type(1)'); await new Promise(r => setTimeout(r, 300)); await click(counter);
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
    await new Promise(r => setTimeout(r, 2500));
    // After the reload, a click breakpoint must still work.
    await setBps([{ line: clickLine }]);
    const s2 = c.wait("stopped", () => true, 6000); await click(counter); const st2 = await s2.catch(() => null);
    const f2 = st2 ? await top(st2.threadId) : null;
    await setBps([]); if (st2) await c.send("continue", { threadId: st2.threadId });
    return [!!st2, `first render after reload: ${f ? "stopped at " + where(f) : "NOT stopped"}; click after reload: ${f2 ? "stopped at " + where(f2) : "NOT stopped"}`];
  },
  async "window-restart"() {
    if (!a["restart-file"]) return [true, "skipped (no --restart-file)"];
    await setBps([{ line: clickLine }]);
    fs.appendFileSync(a["restart-file"], `\n// webkit-cdp scenario: restart ${Date.now()}\n`);
    // The same js-debug session, without a new attach: the adapter announces the new Window's page and js-debug attaches.
    const deadline = Date.now() + 120000;
    let st = null;
    await new Promise(r => setTimeout(r, 5000));
    while (!st && Date.now() < deadline) {
      const s = c.wait("stopped", () => true, 3000);
      await click(counter).catch(() => null);
      st = await s.catch(() => null);
    }
    const f = st ? await top(st.threadId) : null;
    await setBps([]); if (st) await c.send("continue", { threadId: st.threadId });
    return [!!st, `after the Window restarted: ${f ? "stopped at " + where(f) : "NOT stopped within 120 s"}`];
  },
  async hmr() {
    const original = fs.readFileSync(app, "utf8");
    if (!original.includes("<h1>Runic Views</h1>")) throw new Error("App.tsx has no <h1>Runic Views</h1> to edit");
    fs.writeFileSync(app, original.replace("<h1>Runic Views</h1>", "<h1>Runic Views (edited)</h1>"));
    await new Promise(r => setTimeout(r, 2500));
    try {
      await setBps([{ line: clickLine }]);
      const s = c.wait("stopped", () => true, 6000); await click(counter); const st = await s.catch(() => null);
      const f = st ? await top(st.threadId) : null;
      let text = null;
      if (st) { text = await c.send("evaluate", { expression: `document.querySelector("header h1").textContent`, frameId: f.id, context: "repl" }); }
      await setBps([]); if (st) await c.send("continue", { threadId: st.threadId });
      return [!!st && text?.body?.result?.includes("edited"), `after HMR edit: ${f ? "stopped at " + where(f) : "NOT stopped"}; header ${text?.body?.result}`];
    } finally { fs.writeFileSync(app, original); await new Promise(r => setTimeout(r, 1500)); }
  },
};

for (const [name, run] of Object.entries(scenarios)) {
  if (a.only && !a.only.split(",").includes(name)) continue;
  try { const [ok, detail] = await run(); results.push(ok); log(`${ok ? "PASS" : "FAIL"} ${name}: ${detail}`); }
  catch (e) { results.push(false); log(`FAIL ${name}: ${e.message}`); }
  await new Promise(r => setTimeout(r, 500));
}
await Promise.race([c.send("disconnect", {}), new Promise(r => setTimeout(r, 3000))]).catch(() => {});
log(`${results.filter(Boolean).length}/${results.length} passed`);
done(results.every(Boolean) ? 0 : 1);
