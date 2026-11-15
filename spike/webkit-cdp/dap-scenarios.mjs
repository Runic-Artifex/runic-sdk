// SPIKE (runic-sdk#1029): more js-debug scenarios against a CDP port, for the Runic template's React page
// (Frontend/src/App.tsx). Same DAP flow as dap-harness.mjs; each scenario prints PASS/FAIL with evidence.
// usage: node dap-scenarios.mjs --jsdebug <dir> --port <cdp port> --frontend <Frontend dir> [--only a,b] [--trace file]
import { spawn } from "node:child_process";
import fs from "node:fs";
import net from "node:net";
import { parseArgs } from "node:util";

const { values: a } = parseArgs({ options: {
  jsdebug: { type: "string" }, port: { type: "string" }, frontend: { type: "string" }, only: { type: "string" }, trace: { type: "string" },
} });
const app = `${a.frontend}/src/App.tsx`;
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
const init = { clientID: "runic-spike", adapterID: "pwa-chrome", linesStartAt1: true, columnsStartAt1: true, pathFormat: "path", supportsVariableType: true, supportsStartDebuggingRequest: true };

await new Promise(r => setTimeout(r, 700));
const parent = new Dap("parent"); await parent.connect();
const childP = new Promise(res => { parent.handlers.startDebugging = async req => {
  parent.reply(req); const c = new Dap("child"); await c.connect(); await c.send("initialize", init);
  const attached = c.send("attach", req.arguments.configuration); await c.wait("initialized"); res({ c, attached });
}; });
await parent.send("initialize", init);
const pa = parent.send("attach", { type: "pwa-chrome", request: "attach", name: "spike", port: Number(a.port), address: "127.0.0.1", webRoot: a.frontend, timeout: 15000, ...(a.trace ? { trace: { logFile: a.trace } } : {}) });
await parent.wait("initialized").catch(() => {}); await parent.send("configurationDone"); await pa;
const { c, attached } = await childP;
await c.send("configurationDone"); await attached;
log("attached");

const counter = 'nav button:nth-of-type(2)';
const click = sel => c.send("evaluate", { expression: `setTimeout(() => document.querySelector(${JSON.stringify(sel)}).click(), 50), "ok"`, context: "repl" });
const top = async threadId => (await c.send("stackTrace", { threadId, levels: 1 })).body.stackFrames[0];
const where = f => `${f.name} ${(f.source?.path ?? f.source?.name ?? "?").replace(a.frontend + "/", "")}:${f.line}:${f.column}`;
const setBps = (bps, path = app) => c.send("setBreakpoints", { source: { path }, breakpoints: bps });
const results = [];

const scenarios = {
  async "step-in-out"() {
    await setBps([{ line: 31 }]);
    const s = c.wait("stopped"); await click(counter); const st = await s;
    const f0 = await top(st.threadId);
    let p = c.wait("stopped"); await c.send("stepIn", { threadId: st.threadId }); const f1 = await top((await p).threadId);
    p = c.wait("stopped"); await c.send("stepOut", { threadId: st.threadId }); const f2 = await top((await p).threadId);
    await setBps([]); await c.send("continue", { threadId: st.threadId });
    return [f1.name !== f0.name || f1.source?.path !== f0.source?.path, `at ${where(f0)}; stepIn -> ${where(f1)}; stepOut -> ${where(f2)}`];
  },
  async conditional() {
    await setBps([{ line: 31, condition: "1 === 2" }]);
    await click(counter); const quiet = await c.noEvent("stopped", 2000);
    await setBps([{ line: 31, condition: "client !== undefined" }]);
    const s = c.wait("stopped", () => true, 5000); await click(counter); const st = await s.catch(() => null);
    await setBps([]); if (st) await c.send("continue", { threadId: st.threadId });
    return [quiet && !!st, `false condition: ${quiet ? "no stop" : "STOPPED"}; true condition: ${st ? "stopped" : "no stop"}`];
  },
  async logpoint() {
    await setBps([{ line: 31, logMessage: "spike-logpoint client={client !== undefined}" }]);
    const o = c.wait("output", b => b.output.includes("spike-logpoint"), 5000); const quiet = c.noEvent("stopped", 2000);
    await click(counter); const out = await o.catch(() => null); const noStop = await quiet;
    await setBps([]);
    return [!!out && noStop, `output ${JSON.stringify(out?.output?.trim())}, ${noStop ? "no stop" : "STOPPED"}`];
  },
  async exception() {
    await c.send("setExceptionBreakpoints", { filters: ["uncaught"] });
    const s = c.wait("stopped", () => true, 5000);
    await c.send("evaluate", { expression: `setTimeout(() => { throw new Error("spike-boom"); }, 50), "ok"`, context: "repl" });
    const st = await s.catch(() => null);
    let info = null;
    if (st) { info = await c.send("exceptionInfo", { threadId: st.threadId }); await c.send("continue", { threadId: st.threadId }); }
    await c.send("setExceptionBreakpoints", { filters: [] });
    const out = await c.wait("output", b => b.output.includes("spike-boom"), 3000).catch(() => null);
    return [st?.reason === "exception", `stopped=${st?.reason}; exceptionInfo=${JSON.stringify(info?.body?.description ?? info?.message)}; output=${JSON.stringify(out?.output?.split("\n")[0])}`];
  },
  async "uncaught-output"() {
    const out = c.wait("output", b => b.output.includes("spike-uncaught"), 4000);
    await c.send("evaluate", { expression: `setTimeout(() => { throw new Error("spike-uncaught"); }, 50), "ok"`, context: "repl" });
    const o = await out.catch(() => null);
    return [!!o, `output[${o?.category}] ${JSON.stringify(o?.output?.split("\n").slice(0, 2).join(" / "))}`];
  },
  async "console-levels"() {
    const want = ["spike-warn", "spike-error", "spike-info", "spike-obj"];
    const got = want.map(w => c.wait("output", b => b.output.includes(w) || JSON.stringify(b).includes(w), 4000).then(b => `${w}:${b.category}`, () => `${w}:MISSING`));
    await c.send("evaluate", { expression: `console.warn("spike-warn"); console.error("spike-error"); console.info("spike-info"); console.log({ tag: "spike-obj", n: 2 }); "ok"`, context: "repl" });
    const r = await Promise.all(got);
    return [r.every(x => !x.endsWith("MISSING")), r.join(", ")];
  },
  async "set-variable"() {
    await c.send("setBreakpoints", { source: { path: `${a.frontend}/src/pages/CounterPage.tsx` }, breakpoints: [{ line: 16 }] });
    const s = c.wait("stopped", () => true, 6000);
    await click('nav button:nth-of-type(1)'); await new Promise(r => setTimeout(r, 300)); await click(counter);
    const st = await s.catch(() => null);
    if (!st) { await c.send("setBreakpoints", { source: { path: `${a.frontend}/src/pages/CounterPage.tsx` }, breakpoints: [] }); return [false, "no stop in CounterPage"]; }
    const f = await top(st.threadId);
    const scopes = (await c.send("scopes", { frameId: f.id })).body.scopes;
    const local = scopes.find(x => /local/i.test(x.name));
    const vars = (await c.send("variables", { variablesReference: local.variablesReference })).body.variables;
    const names = vars.map(v => `${v.name}=${v.value.slice(0, 40)}`);
    const sv = await c.send("setVariable", { variablesReference: local.variablesReference, name: "error", value: "42" });
    const check = await c.send("evaluate", { expression: "error", frameId: f.id, context: "repl" });
    const hover = await c.send("evaluate", { expression: "counter.state?.count", frameId: f.id, context: "hover" });
    await c.send("setBreakpoints", { source: { path: `${a.frontend}/src/pages/CounterPage.tsx` }, breakpoints: [] });
    await c.send("continue", { threadId: st.threadId });
    return [names.length > 0, `stopped at ${where(f)}; locals ${names.slice(0, 6).join(" | ")}; setVariable error=42 -> ${sv.success ? sv.body.value : "FAILED: " + sv.message}, read back ${check.body?.result}; hover counter.state?.count -> ${hover.success ? hover.body.result : hover.message}`];
  },
  async reload() {
    // App() renders after a reload: line 21 runs during the page's first render.
    await setBps([{ line: 21 }]);
    const s = c.wait("stopped", () => true, 15000);
    await c.send("evaluate", { expression: `setTimeout(() => location.reload(), 50), "ok"`, context: "repl" });
    const st = await s.catch(() => null);
    const f = st ? await top(st.threadId) : null;
    await setBps([]); if (st) await c.send("continue", { threadId: st.threadId });
    await new Promise(r => setTimeout(r, 2500));
    // After the reload, a click breakpoint must still work.
    await setBps([{ line: 31 }]);
    const s2 = c.wait("stopped", () => true, 6000); await click(counter); const st2 = await s2.catch(() => null);
    const f2 = st2 ? await top(st2.threadId) : null;
    await setBps([]); if (st2) await c.send("continue", { threadId: st2.threadId });
    return [!!st2, `first render after reload: ${f ? "stopped at " + where(f) : "NOT stopped"}; click after reload: ${f2 ? "stopped at " + where(f2) : "NOT stopped"}`];
  },
  async hmr() {
    const original = fs.readFileSync(app, "utf8");
    fs.writeFileSync(app, original.replace("<p>Window/View starter · React</p>", "<p>Window/View starter · React (edited)</p>"));
    await new Promise(r => setTimeout(r, 2500));
    try {
      await setBps([{ line: 31 }]);
      const s = c.wait("stopped", () => true, 6000); await click(counter); const st = await s.catch(() => null);
      const f = st ? await top(st.threadId) : null;
      let text = null;
      if (st) { text = await c.send("evaluate", { expression: `document.querySelector("header p").textContent`, frameId: f.id, context: "repl" }); }
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
await c.send("disconnect", {}).catch(() => {});
log(`${results.filter(Boolean).length}/${results.length} passed`);
done(results.every(Boolean) ? 0 : 1);
