// Drives vscode-js-debug's standalone DAP server like VS Code would for an "attach" to a CDP port, then runs the
// spike scenario: breakpoint in a Vite source (source map), hit it from a click, stack/scopes/locals, console
// evaluate, step over, step in, continue, console.log output.
// usage: node dap-harness.mjs --jsdebug <dir> --port <cdp port> --webroot <dir> --source <file> --line <n>
//        [--click <css selector>] [--expr <expression at the breakpoint>] [--trace <log file>] [--url-filter <glob>]
import { spawn } from "node:child_process";
import net from "node:net";
import { parseArgs } from "node:util";

const { values: a } = parseArgs({ options: {
  jsdebug: { type: "string" }, port: { type: "string" }, webroot: { type: "string" }, source: { type: "string" },
  line: { type: "string" }, click: { type: "string", default: "button" }, expr: { type: "string" },
  trace: { type: "string" }, "url-filter": { type: "string" }, type: { type: "string", default: "pwa-chrome" },
} });
const t0 = Date.now();
const log = (...x) => console.log(`[${((Date.now() - t0) / 1000).toFixed(2)}s]`, ...x);
const fail = (m) => { log("FAIL:", m); cleanup(1); };

const dapPort = 41000 + Math.floor(Math.random() * 1000);
const server = spawn(process.execPath, [`${a.jsdebug}/src/dapDebugServer.js`, String(dapPort), "127.0.0.1"], { stdio: ["ignore", "pipe", "pipe"] });
server.stderr.on("data", d => process.stderr.write(`[js-debug] ${d}`));
function cleanup(code) { try { server.kill(); } catch {} setTimeout(() => process.exit(code), 200); }
setTimeout(() => fail("overall timeout"), 90000);

class Dap {
  constructor(name) { this.name = name; this.seq = 1; this.pending = new Map(); this.waiters = []; this.buf = Buffer.alloc(0); this.handlers = {}; }
  connect() {
    return new Promise((res, rej) => {
      this.sock = net.connect(dapPort, "127.0.0.1", res); this.sock.on("error", rej);
      this.sock.on("data", d => { this.buf = Buffer.concat([this.buf, d]); this.drain(); });
    });
  }
  drain() {
    for (;;) {
      const h = this.buf.indexOf("\r\n\r\n"); if (h < 0) return;
      const len = Number(/Content-Length: (\d+)/i.exec(this.buf.subarray(0, h).toString())[1]);
      if (this.buf.length < h + 4 + len) return;
      const msg = JSON.parse(this.buf.subarray(h + 4, h + 4 + len).toString()); this.buf = this.buf.subarray(h + 4 + len);
      this.dispatch(msg);
    }
  }
  dispatch(m) {
    if (m.type === "response") { const p = this.pending.get(m.request_seq); this.pending.delete(m.request_seq); p?.(m); return; }
    if (m.type === "event") {
      if (m.event === "output") log(`${this.name} output[${m.body.category}]:`, JSON.stringify(m.body.output.trimEnd()));
      for (const w of [...this.waiters]) if (w.event === m.event && w.pred(m.body)) { this.waiters.splice(this.waiters.indexOf(w), 1); w.res(m.body); }
      return;
    }
    if (m.type === "request") this.handlers[m.command]?.(m);
  }
  send(command, args = {}) {
    const m = { seq: this.seq++, type: "request", command, arguments: args }; const s = JSON.stringify(m);
    this.sock.write(`Content-Length: ${Buffer.byteLength(s)}\r\n\r\n${s}`);
    return new Promise(res => this.pending.set(m.seq, res));
  }
  reply(req, body = {}) { const s = JSON.stringify({ seq: this.seq++, type: "response", request_seq: req.seq, command: req.command, success: true, body }); this.sock.write(`Content-Length: ${Buffer.byteLength(s)}\r\n\r\n${s}`); }
  wait(event, pred = () => true, ms = 20000) {
    return new Promise((res, rej) => { const w = { event, pred, res }; this.waiters.push(w); setTimeout(() => rej(new Error(`${this.name}: no ${event} in ${ms} ms`)), ms); });
  }
}
const init = { clientID: "runic-spike", adapterID: a.type, linesStartAt1: true, columnsStartAt1: true, pathFormat: "path",
  supportsVariableType: true, supportsRunInTerminalRequest: false, supportsStartDebuggingRequest: true };

try {
  await new Promise(r => setTimeout(r, 700));
  const parent = new Dap("parent"); await parent.connect();
  const childReady = new Promise(res => {
    parent.handlers.startDebugging = async (req) => {
      parent.reply(req);
      const child = new Dap("child"); await child.connect();
      await child.send("initialize", init);
      const attached = child.send("attach", req.arguments.configuration);
      await child.wait("initialized");
      res({ child, attached });
    };
  });
  await parent.send("initialize", init);
  const config = { type: a.type, request: "attach", name: "spike", port: Number(a.port), address: "127.0.0.1",
    webRoot: a.webroot, timeout: 15000, ...(a.trace ? { trace: { logFile: a.trace } } : {}),
    ...(a["url-filter"] ? { urlFilter: a["url-filter"] } : {}) };
  const attachResp = parent.send("attach", config);
  await parent.wait("initialized").catch(() => {});
  await parent.send("configurationDone");
  log("parent attach ->", JSON.stringify((await attachResp).success));
  const { child, attached } = await childReady;
  log("child session started");
  const bp = await child.send("setBreakpoints", { source: { path: a.source }, breakpoints: [{ line: Number(a.line) }] });
  log("setBreakpoints ->", JSON.stringify(bp.body?.breakpoints));
  await child.send("configurationDone");
  log("child attach ->", JSON.stringify((await attached).success), (await attached).message ?? "");
  const verified = bp.body?.breakpoints?.[0]?.verified ? bp.body.breakpoints[0] :
    await child.wait("breakpoint", b => b.breakpoint?.verified, 8000).then(b => b.breakpoint).catch(() => null);
  log("breakpoint verified:", JSON.stringify(verified));

  const stoppedP = child.wait("stopped");
  const click = await child.send("evaluate", { expression: `setTimeout(() => document.querySelector(${JSON.stringify(a.click)}).click(), 50), "clicked"`, context: "repl" });
  log("click ->", click.success, click.body?.result ?? click.message);
  const stopped = await stoppedP; log("stopped:", JSON.stringify(stopped));
  const st = await child.send("stackTrace", { threadId: stopped.threadId, levels: 5 });
  for (const f of st.body.stackFrames) log("  frame", f.name, f.source?.path ?? f.source?.name, `${f.line}:${f.column}`);
  const top = st.body.stackFrames[0];
  const sc = await child.send("scopes", { frameId: top.id });
  for (const s of sc.body.scopes) {
    log("  scope", s.name, s.expensive ? "(expensive)" : "");
    if (/local|block|closure/i.test(s.name)) {
      const v = await child.send("variables", { variablesReference: s.variablesReference });
      log("    vars", (v.body?.variables ?? []).slice(0, 12).map(x => `${x.name}=${x.value}`).join(", "));
    }
  }
  if (a.expr) { const e = await child.send("evaluate", { expression: a.expr, frameId: top.id, context: "repl" }); log("evaluate at frame:", a.expr, "->", e.success ? e.body.result : e.message); }
  const logged = child.wait("output", b => b.output.includes("spike-console"), 8000).catch(() => null);
  await child.send("evaluate", { expression: `console.log("spike-console", 1 + 1)`, frameId: top.id, context: "repl" });
  log("console.log seen:", !!(await logged));
  for (const step of ["next", "stepIn"]) {
    const sp = child.wait("stopped");
    await child.send(step, { threadId: stopped.threadId });
    const s2 = await sp; const st2 = await child.send("stackTrace", { threadId: s2.threadId, levels: 1 });
    const f = st2.body.stackFrames[0]; log(`after ${step}:`, s2.reason, f.name, f.source?.path ?? f.source?.name, `${f.line}:${f.column}`);
  }
  await child.send("setBreakpoints", { source: { path: a.source }, breakpoints: [] });
  const cont = child.wait("continued", () => true, 5000).catch(() => null);
  await child.send("continue", { threadId: stopped.threadId }); log("continue ->", !!(await cont) || "ok");
  log("PASS"); await child.send("disconnect", {}); cleanup(0);
} catch (e) { fail(e.stack ?? e.message); }
