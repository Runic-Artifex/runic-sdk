// One Desktop server case through an adapter (specs/desktop/server/README.md, "Running a case"; PROTOCOL.md, "Desktop
// server"). The adapter runs the server with the test application; the driver is the client, over real sockets, and
// checks every byte the server writes.
import { createHash } from "node:crypto";
import { createConnection, type Socket } from "node:net";
import { AdapterError, type Adapter, type Outcome } from "../core";

export class StepFailure extends Error {}

const fail = (message: string): never => {
  throw new StepFailure(message);
};

// The key and accept value of RFC 6455 section 1.3.
export const WEBSOCKET_KEY = "dGhlIHNhbXBsZSBub25jZQ==";
const TAG = "X-Rax-Conformance";
// How long a step that expects nothing waits before it concludes that nothing came.
const QUIET_MS = 250;

const OPCODES: Record<string, number> = { continuation: 0, text: 1, binary: 2, close: 8, ping: 9, pong: 10 };
const OPCODE_NAMES = Object.fromEntries(Object.entries(OPCODES).map(([name, code]) => [code, name]));

type Place = { address: string; port: number };

// Fills the placeholders of a vector string: {authority} (the Host value of the listener), {port} and {port+1}.
export function expandText(text: string, place: Place): string {
  const host = place.address.includes(":") ? `[${place.address}]` : place.address;
  return text.replaceAll("{authority}", `${host}:${place.port}`).replaceAll("{port+1}", String(place.port + 1)).replaceAll("{port}", String(place.port));
}

// The bytes of a payload: {text}, {hex}, {base64} or {repeat: {byte, count}}.
export function payloadBytes(payload: any, place?: Place): Uint8Array {
  if (payload === undefined || payload === null) return new Uint8Array();
  if (typeof payload.text === "string") return new TextEncoder().encode(place ? expandText(payload.text, place) : payload.text);
  if (typeof payload.hex === "string") return Uint8Array.from(Buffer.from(payload.hex.replace(/\s+/g, ""), "hex"));
  if (typeof payload.base64 === "string") return Uint8Array.from(Buffer.from(payload.base64, "base64"));
  if (payload.repeat) return new Uint8Array(payload.repeat.count).fill(typeof payload.repeat.byte === "string" ? payload.repeat.byte.charCodeAt(0) : payload.repeat.byte);
  return fail(`unknown payload ${JSON.stringify(payload)}`);
}

const concat = (parts: Uint8Array[]) => {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const part of parts) {
    out.set(part, at);
    at += part.length;
  }
  return out;
};

// A client frame (README "Frames"): masked unless `mask` is false, with the shortest length unless `lengthForm` or a
// declared `length` says otherwise; `truncate` sends only the first bytes of the encoded frame.
export function encodeFrame(frame: any): Uint8Array {
  const opcode = typeof frame.opcode === "number" ? frame.opcode : OPCODES[frame.opcode];
  if (opcode === undefined) fail(`unknown opcode ${frame.opcode}`);
  let payload = payloadBytes(frame.payload);
  if (frame.close) {
    const reason = frame.close.reason === undefined ? new Uint8Array() : typeof frame.close.reason === "string" ? new TextEncoder().encode(frame.close.reason) : payloadBytes(frame.close.reason);
    payload = concat([new Uint8Array([frame.close.code >> 8, frame.close.code & 0xff]), reason]);
  }
  const fin = frame.fin ?? true;
  const masked = frame.mask ?? true;
  const declared = BigInt(frame.length ?? payload.length);
  const form = frame.lengthForm ?? (declared < 126n ? "7" : declared <= 0xffffn ? "16" : "64");
  const head = [(fin ? 0x80 : 0) | ((frame.rsv ?? 0) << 4) | opcode];
  const maskBit = masked ? 0x80 : 0;
  if (form === "7") head.push(maskBit | Number(declared));
  else if (form === "16") head.push(maskBit | 126, Number(declared >> 8n) & 0xff, Number(declared) & 0xff);
  else {
    head.push(maskBit | 127);
    for (let shift = 56n; shift >= 0n; shift -= 8n) head.push(Number((declared >> shift) & 0xffn));
  }
  const mask = [0x37, 0xfa, 0x21, 0x3d];
  const body = masked ? payload.map((byte, i) => byte ^ mask[i % 4]) : payload;
  const encoded = concat([new Uint8Array(head), masked ? new Uint8Array(mask) : new Uint8Array(), body]);
  return frame.truncate === undefined ? encoded : encoded.subarray(0, frame.truncate);
}

// One client connection: a byte buffer the steps read from, and the server's end of the stream.
class Connection {
  private buffer = new Uint8Array();
  private waiters: (() => void)[] = [];
  ended = false;
  methods: string[] = [];
  // Frames read while a fragmented message was reassembled, for the next frame step.
  frames: any[] = [];

  private constructor(private readonly socket: Socket) {
    socket.on("data", (chunk: Buffer) => {
      this.buffer = concat([this.buffer, new Uint8Array(chunk)]);
      this.wake();
    });
    const end = () => {
      this.ended = true;
      this.wake();
    };
    socket.on("end", end);
    socket.on("close", end);
    socket.on("error", end);
  }

  static open(place: Place, timeoutMs: number): Promise<Connection> {
    return new Promise((resolve, reject) => {
      const socket = createConnection({ host: place.address, port: place.port, allowHalfOpen: true });
      socket.setNoDelay(true);
      const timer = setTimeout(() => reject(new StepFailure(`no connection to ${place.address}:${place.port} within ${timeoutMs} ms`)), timeoutMs);
      socket.once("connect", () => {
        clearTimeout(timer);
        resolve(new Connection(socket));
      });
      socket.once("error", (error) => {
        clearTimeout(timer);
        reject(new StepFailure(`connecting failed: ${error.message}`));
      });
    });
  }

  private wake() {
    for (const waiter of this.waiters.splice(0)) waiter();
  }

  // Waits until `ready` holds or the stream ended; false after `timeoutMs`.
  private async until(ready: () => boolean, timeoutMs: number): Promise<boolean> {
    const deadline = Date.now() + timeoutMs;
    while (!ready() && !this.ended) {
      const left = deadline - Date.now();
      if (left <= 0) return false;
      await new Promise<void>((resolve) => {
        const timer = setTimeout(resolve, left);
        this.waiters.push(() => {
          clearTimeout(timer);
          resolve();
        });
      });
    }
    return ready() || this.ended;
  }

  get pending() {
    return this.buffer.length;
  }

  async bytes(count: number, timeoutMs: number, what: string): Promise<Uint8Array> {
    if (!(await this.until(() => this.buffer.length >= count, timeoutMs))) fail(`no ${what} within ${timeoutMs} ms (${this.buffer.length} of ${count} bytes)`);
    if (this.buffer.length < count) fail(`the server ended the connection before ${what} (${this.buffer.length} of ${count} bytes)`);
    const out = this.buffer.slice(0, count);
    this.buffer = this.buffer.slice(count);
    return out;
  }

  async line(timeoutMs: number, what: string, limit = 1 << 20): Promise<string> {
    const find = () => this.buffer.findIndex((byte, i) => byte === 0x0a && i > 0 && this.buffer[i - 1] === 0x0d);
    if (!(await this.until(() => find() >= 0 || this.buffer.length > limit, timeoutMs))) fail(`no ${what} within ${timeoutMs} ms`);
    const index = find();
    if (index < 0) fail(this.ended ? `the server ended the connection before ${what}` : `${what} is longer than ${limit} bytes`);
    const text = new TextDecoder().decode(this.buffer.slice(0, index - 1));
    this.buffer = this.buffer.slice(index + 1);
    return text;
  }

  async rest(timeoutMs: number): Promise<{ data: Uint8Array; ended: boolean }> {
    const ended = await this.until(() => false, timeoutMs);
    const data = this.buffer;
    this.buffer = new Uint8Array();
    return { data, ended };
  }

  // Whether the server ends the stream within `timeoutMs` without sending anything more.
  async ends(timeoutMs: number): Promise<{ ended: boolean; extra: number }> {
    await this.until(() => this.buffer.length > 0, timeoutMs);
    if (this.buffer.length) return { ended: false, extra: this.buffer.length };
    return { ended: this.ended, extra: 0 };
  }

  write(data: Uint8Array) {
    try {
      this.socket.write(data);
    } catch {}
  }

  halfClose() {
    try {
      this.socket.end();
    } catch {}
  }

  destroy() {
    this.socket.destroy();
  }
}

type Response = { status: number; reason: string; version: string; headers: [string, string][]; body: Uint8Array };

async function readResponse(conn: Connection, method: string, timeoutMs: number): Promise<Response> {
  for (;;) {
    const statusLine = await conn.line(timeoutMs, "a status line");
    const match = /^(HTTP\/\d\.\d) (\d{3})(?: (.*))?$/.exec(statusLine);
    if (!match) fail(`not a status line: ${JSON.stringify(statusLine)}`);
    const status = Number(match![2]);
    const headers: [string, string][] = [];
    for (;;) {
      const line = await conn.line(timeoutMs, "the response head");
      if (line === "") break;
      const colon = line.indexOf(":");
      if (colon <= 0) fail(`not a header line: ${JSON.stringify(line)}`);
      headers.push([line.slice(0, colon).toLowerCase(), line.slice(colon + 1).trim()]);
    }
    // An interim response other than 101 precedes the final one.
    if (status >= 100 && status < 200 && status !== 101) continue;
    const get = (name: string) => headers.filter(([n]) => n === name).map(([, v]) => v);
    let body = new Uint8Array();
    if (method !== "HEAD" && status !== 204 && status !== 304 && status !== 101) {
      if (get("transfer-encoding").some((v) => /chunked/i.test(v))) {
        const parts: Uint8Array[] = [];
        for (;;) {
          const size = parseInt((await conn.line(timeoutMs, "a chunk size")).split(";")[0], 16);
          if (Number.isNaN(size)) fail("an invalid chunk size");
          if (size === 0) {
            while ((await conn.line(timeoutMs, "the trailer")) !== "");
            break;
          }
          parts.push(await conn.bytes(size, timeoutMs, "a chunk"));
          await conn.bytes(2, timeoutMs, "a chunk's end");
        }
        body = concat(parts);
      } else if (get("content-length").length) {
        body = await conn.bytes(Number(get("content-length")[0]), timeoutMs, "the body");
      } else {
        body = (await conn.rest(timeoutMs)).data;
      }
    }
    return { status, reason: match![3] ?? "", version: match![1], headers, body };
  }
}

const show = (data: Uint8Array) => {
  const text = new TextDecoder("utf-8", { fatal: false }).decode(data.subarray(0, 200));
  return JSON.stringify(text) + (data.length > 200 ? `… (${data.length} bytes)` : "");
};

function statusMatches(expected: any, actual: number): boolean {
  if (Array.isArray(expected)) return expected.some((e) => statusMatches(e, actual));
  if (typeof expected === "string") return /^\dxx$/.test(expected) && Math.floor(actual / 100) === Number(expected[0]);
  return expected === actual;
}

// Compares bytes with a body or payload expectation: {text}, {hex}, {base64}, {length}, {sha1} or {empty: true}.
function bytesProblem(expected: any, actual: Uint8Array, place: Place): string | null {
  if (expected.empty) return actual.length ? `${show(actual)}, expected nothing` : null;
  if (expected.length !== undefined && actual.length !== expected.length) return `${actual.length} bytes, expected ${expected.length}`;
  if (expected.sha1 !== undefined) {
    const digest = createHash("sha1").update(actual).digest("hex");
    if (digest !== expected.sha1) return `the SHA-1 ${digest}, expected ${expected.sha1}`;
  }
  if (expected.text !== undefined || expected.hex !== undefined || expected.base64 !== undefined || expected.repeat !== undefined) {
    const want = payloadBytes(expected, place);
    if (Buffer.compare(Buffer.from(want), Buffer.from(actual)) !== 0) return `${show(actual)}, expected ${show(want)}`;
  }
  return null;
}

function responseProblems(expect: any, response: Response, place: Place): string[] {
  const problems: string[] = [];
  if (expect.status !== undefined && !statusMatches(expect.status, response.status)) problems.push(`status ${response.status}, expected ${JSON.stringify(expect.status)}`);
  for (const [name, want] of Object.entries<any>(expect.headers ?? {})) {
    const values = response.headers.filter(([n]) => n === name.toLowerCase()).map(([, v]) => v);
    if (want === null) {
      if (values.length) problems.push(`${name}: ${JSON.stringify(values)}, expected none`);
    } else if (Array.isArray(want)) {
      const expanded = want.map((w: string) => expandText(w, place));
      if (JSON.stringify(values) !== JSON.stringify(expanded)) problems.push(`${name}: ${JSON.stringify(values)}, expected ${JSON.stringify(expanded)}`);
    } else if (typeof want === "object" && want.token !== undefined) {
      const tokens = values.flatMap((v) => v.split(",")).map((t) => t.trim().toLowerCase());
      if (!tokens.includes(want.token.toLowerCase())) problems.push(`${name}: ${JSON.stringify(values)}, expected the token ${want.token}`);
    } else if (typeof want === "object" && want.ignoreCase !== undefined) {
      if (values.length !== 1 || values[0].toLowerCase() !== want.ignoreCase.toLowerCase()) problems.push(`${name}: ${JSON.stringify(values)}, expected ${JSON.stringify(want.ignoreCase)} in any case`);
    } else if (typeof want === "object" && want.present) {
      if (!values.length) problems.push(`${name}: none, expected one`);
    } else {
      const expanded = expandText(String(want), place);
      if (values.length !== 1 || values[0] !== expanded) problems.push(`${name}: ${JSON.stringify(values)}, expected ${JSON.stringify(expanded)}`);
    }
  }
  if (expect.body) {
    const problem = bytesProblem(expect.body, response.body, place);
    if (problem) problems.push(`body: ${problem}`);
  }
  return problems;
}

type Frame = { fin: boolean; rsv: number; opcode: number; payload: Uint8Array };

// The next server frame. Every frame is checked against RFC 6455's rules for what a server sends: unmasked (5.1), no
// reserved bits without an extension (5.2), the shortest length (5.2), and short, final control frames (5.5).
async function readFrame(conn: Connection, timeoutMs: number): Promise<Frame> {
  const [first, second] = await conn.bytes(2, timeoutMs, "a frame");
  if (second & 0x80) fail("the server masked a frame (RFC 6455 section 5.1: a server MUST NOT mask)");
  let length = second & 0x7f;
  if (length === 126) {
    const b = await conn.bytes(2, timeoutMs, "a frame length");
    length = (b[0] << 8) | b[1];
    if (length < 126) fail(`the server sent the length ${length} in 16 bits (RFC 6455 section 5.2: the minimal number of bytes MUST be used)`);
  } else if (length === 127) {
    const b = await conn.bytes(8, timeoutMs, "a frame length");
    const big = b.reduce((n, byte) => n * 256n + BigInt(byte), 0n);
    if (big <= 0xffffn) fail(`the server sent the length ${big} in 64 bits (RFC 6455 section 5.2: the minimal number of bytes MUST be used)`);
    if (big > 1n << 30n) fail(`the server declared a ${big}-byte frame`);
    length = Number(big);
  }
  const frame = { fin: (first & 0x80) !== 0, rsv: (first >> 4) & 7, opcode: first & 0x0f, payload: await conn.bytes(length, timeoutMs, "a frame payload") };
  if (frame.rsv) fail(`the server set reserved bits ${frame.rsv} without an extension (RFC 6455 section 5.2)`);
  if (frame.opcode >= 8 && (!frame.fin || length > 125)) fail("the server sent a fragmented or long control frame (RFC 6455 section 5.5)");
  if (!(frame.opcode in OPCODE_NAMES)) fail(`the server sent the reserved opcode ${frame.opcode}`);
  return frame;
}

// The next message: a control frame, or a data message with its fragments joined (a server may fragment, RFC 6455
// section 5.4). Control frames between fragments are kept for the following reads.
async function readMessage(conn: Connection, timeoutMs: number): Promise<Frame> {
  if (conn.frames.length) return conn.frames.shift();
  const first = await readFrame(conn, timeoutMs);
  if (first.opcode === 0) fail("the server sent a continuation frame without a message");
  if (first.opcode >= 8 || first.fin) return first;
  const parts = [first.payload];
  for (;;) {
    const next = await readFrame(conn, timeoutMs);
    if (next.opcode >= 8) {
      conn.frames.push(next);
      continue;
    }
    if (next.opcode !== 0) fail("the server started a message inside another (RFC 6455 section 5.4)");
    parts.push(next.payload);
    if (next.fin) return { ...first, fin: true, payload: concat(parts) };
  }
}

function describeFrame(frame: Frame): string {
  const name = OPCODE_NAMES[frame.opcode] ?? String(frame.opcode);
  if (frame.opcode === 8) return frame.payload.length >= 2 ? `close ${(frame.payload[0] << 8) | frame.payload[1]} ${show(frame.payload.subarray(2))}` : `close ${show(frame.payload)}`;
  return `${name} ${show(frame.payload)}`;
}

function frameProblems(expect: any, frame: Frame, place: Place): string[] {
  const problems: string[] = [];
  const name = OPCODE_NAMES[frame.opcode];
  if (expect.opcode !== undefined && expect.opcode !== name) return [`${describeFrame(frame)}, expected a ${expect.opcode} frame`];
  if (name === "text") {
    try {
      new TextDecoder("utf-8", { fatal: true }).decode(frame.payload);
    } catch {
      problems.push("the text message is not UTF-8 (RFC 6455 section 5.6)");
    }
  }
  if (name === "close") {
    if (frame.payload.length === 1) problems.push("a one-byte close payload");
    const code = frame.payload.length >= 2 ? (frame.payload[0] << 8) | frame.payload[1] : null;
    if (expect.code !== undefined && (Array.isArray(expect.code) ? !expect.code.includes(code) : code !== expect.code)) problems.push(`close code ${code}, expected ${JSON.stringify(expect.code)}`);
    if (expect.reason !== undefined) {
      const reason = new TextDecoder().decode(frame.payload.subarray(2));
      if (reason !== expect.reason) problems.push(`close reason ${JSON.stringify(reason)}, expected ${JSON.stringify(expect.reason)}`);
    }
  } else if (expect.payload) {
    const problem = bytesProblem(expect.payload, frame.payload, place);
    if (problem) problems.push(`payload: ${problem}`);
  }
  return problems;
}

// What the host recorded for one connection, compared with a `host` step's events.
function eventProblem(expected: any, actual: any, place: Place): string | null {
  const said = JSON.stringify({ ...actual, headers: undefined, data: actual.data === undefined ? undefined : show(Buffer.from(actual.data, "base64")) });
  if (expected.type !== actual.type) return `${said}, expected a ${expected.type} event`;
  for (const key of ["method", "websocket", "kind", "code", "reason"]) {
    if (expected[key] !== undefined && expected[key] !== actual[key]) return `${said}, expected ${key} ${JSON.stringify(expected[key])}`;
  }
  if (expected.target !== undefined && expandText(expected.target, place) !== actual.target) return `${said}, expected the target ${JSON.stringify(expected.target)}`;
  for (const [name, want] of Object.entries<any>(expected.headers ?? {})) {
    const values = (actual.headers ?? []).filter(([n]: string[]) => n === name.toLowerCase()).map(([, v]: string[]) => v);
    if (want === null ? values.length : values.length !== 1 || values[0] !== expandText(want, place)) return `${said}: header ${name} ${JSON.stringify(values)}, expected ${JSON.stringify(want)}`;
  }
  if (expected.data) {
    const problem = bytesProblem(expected.data, Uint8Array.from(Buffer.from(actual.data ?? "", "base64")), place);
    if (problem) return `${said}: data ${problem}`;
  }
  return null;
}

function requestBytes(step: any, place: Place, conn: string): { bytes: Uint8Array; method: string } {
  const r = step.request;
  const method = r.method ?? "GET";
  let headers: [string, string][] = (r.headers ?? [["Host", "{authority}"]]).map(([n, v]: string[]) => [n, expandText(v, place)]);
  if (step.upgrade) {
    const defaults: [string, string][] = [
      ["Host", "{authority}"],
      ["Connection", "Upgrade"],
      ["Upgrade", "websocket"],
      ["Sec-WebSocket-Version", "13"],
      ["Sec-WebSocket-Key", WEBSOCKET_KEY],
    ];
    const given = new Set((r.headers ?? []).map(([n]: string[]) => n.toLowerCase()));
    const omit = new Set((r.omit ?? []).map((n: string) => n.toLowerCase()));
    headers = [...defaults.filter(([n]) => !given.has(n.toLowerCase()) && !omit.has(n.toLowerCase())).map(([n, v]) => [n, expandText(v, place)] as [string, string]), ...(r.headers ?? []).map(([n, v]: string[]) => [n, expandText(v, place)] as [string, string])];
  }
  if (r.tag !== false) headers.push([TAG, conn]);
  const body = r.body ? payloadBytes(r.body, place) : new Uint8Array();
  const head = `${method} ${expandText(r.target ?? "/", place)} ${r.version ?? "HTTP/1.1"}\r\n${headers.map(([n, v]) => `${n}: ${v}\r\n`).join("")}\r\n`;
  return { bytes: concat([new TextEncoder().encode(head), body]), method };
}

// Runs one case; a failing step throws a StepFailure, which becomes the case's detail.
export async function runServerCase(adapter: Adapter, testCase: any, supports: Set<string>, timing: { receiveMs: number }): Promise<Outcome> {
  const missing = (testCase.requires ?? []).filter((r: string) => !supports.has(r));
  if (missing.length) return { outcome: "unsupported", detail: `the adapter lists no ${missing.join(", ")}`, requires: missing };
  const timeout = timing.receiveMs;
  let started: any;
  try {
    started = await adapter.call("server.start", testCase.config ?? {});
  } catch (e) {
    if (e instanceof AdapterError && e.code === "unsupported") return { outcome: "unsupported", detail: e.message };
    throw e;
  }
  const place: Place = { address: started.address, port: started.port };
  const conns = new Map<string, Connection>();
  const events = new Map<string, any[]>();
  const conn = (name: string) => conns.get(name) ?? fail(`the case uses the connection ${name} before it connects`);
  // Takes the host's events so far, waiting up to `waitMs` until `conn` has `count`.
  const collect = async (name: string, count: number, waitMs: number) => {
    const deadline = Date.now() + waitMs;
    for (;;) {
      const have = events.get(name)?.length ?? 0;
      const wait = have >= count ? 0 : Math.max(0, Math.min(deadline - Date.now(), 100));
      const { events: fresh } = await adapter.call("server.events", { waitMs: wait, count: 1 });
      for (const event of fresh) events.set(event.conn, [...(events.get(event.conn) ?? []), event]);
      if ((events.get(name)?.length ?? 0) >= count || Date.now() >= deadline) return;
    }
  };
  let index = 0;
  try {
    for (const step of testCase.steps) {
      index++;
      if (step.connect) conns.set(step.connect, await Connection.open(place, timeout));
      else if (step.send) {
        const c = conn(step.send);
        if (step.request) {
          const { bytes, method } = requestBytes(step, place, step.send);
          c.methods.push(method);
          c.write(bytes);
        } else if (step.upgrade) {
          const { bytes, method } = requestBytes({ request: step.upgrade, upgrade: true }, place, step.send);
          c.methods.push(method);
          c.write(bytes);
        } else if (step.raw) {
          c.methods.push(...(step.raw.methods ?? ["GET"]));
          const tagged = { ...step.raw, text: step.raw.text === undefined ? undefined : step.raw.text.replaceAll("{tag}", `${TAG}: ${step.send}`) };
          c.write(payloadBytes(tagged, place));
        } else if (step.frames) c.write(concat(step.frames.map(encodeFrame)));
        else fail("a send step needs request, upgrade, raw or frames");
      } else if (step.halfClose) conn(step.halfClose).halfClose();
      else if (step.close) {
        conn(step.close).destroy();
        conns.delete(step.close);
      } else if (step.response) {
        const c = conn(step.response);
        const method = c.methods.shift() ?? "GET";
        if (step.orEnd) {
          // A server may refuse by ending the connection instead of answering.
          const { ended, extra } = await c.ends(timeout);
          if (ended && !extra) continue;
          if (!extra) fail(`neither a response nor the connection's end within ${timeout} ms`);
        }
        const response = await readResponse(c, method, timeout);
        const problems = responseProblems(step.expect ?? {}, response, place);
        if (problems.length) fail(`HTTP ${response.status} ${response.reason}: ${problems.join("; ")}`);
        if (step.expect?.closes) {
          const { ended, extra } = await c.ends(timeout);
          if (!ended) fail(extra ? `${extra} more bytes after the response, expected the connection to close` : `the connection stayed open ${timeout} ms after the response`);
        }
      } else if (step.frame) {
        const frame = await readMessage(conn(step.frame), timeout);
        const problems = frameProblems(step.expect ?? {}, frame, place);
        if (problems.length) fail(problems.join("; "));
      } else if (step.closeOrEnd) {
        // The connection ends, with at most a close frame first and no data (RFC 6455 section 7.1.7).
        const c = conn(step.closeOrEnd);
        await c.ends(timeout);
        if (c.pending) {
          const frame = await readMessage(c, timeout);
          if (frame.opcode !== 8) fail(`${describeFrame(frame)}, expected the connection to end`);
          c.halfClose();
        }
        const { ended, extra } = await c.ends(timeout);
        if (!ended) fail(extra ? `${extra} more bytes, expected the connection to end` : `the connection stayed open ${timeout} ms`);
      } else if (step.end) {
        const c = conn(step.end);
        if (step.shutdown === true) c.halfClose();
        const { ended, extra } = await c.ends(timeout);
        if (!ended) fail(extra ? `${extra} more bytes (${show((await c.rest(0)).data)}), expected the connection to end` : `the connection stayed open ${timeout} ms`);
      } else if (step.host) {
        // The host's next events for the connection, in order; an empty list: none within a short wait.
        const want: any[] = step.expect ?? [];
        if (want.length) await collect(step.host, want.length, timeout);
        else await collect(step.host, 1, QUIET_MS);
        const have = events.get(step.host) ?? [];
        const taken = have.splice(0, want.length || have.length);
        if (!want.length && taken.length) fail(`the host saw ${taken.map((e) => `${e.type} ${e.target ?? ""}`.trim()).join(", ")} for ${step.host}, expected nothing`);
        if (taken.length < want.length) fail(`the host saw ${taken.length} of ${want.length} events for ${step.host}: ${taken.map((e) => e.type).join(", ") || "none"}`);
        for (const [i, expected] of want.entries()) {
          const problem = eventProblem(expected, taken[i], place);
          if (problem) fail(`host event ${i + 1}: ${problem}`);
        }
      } else if (step.hostSend) {
        await adapter.call("server.wsSend", { conn: step.hostSend, kind: step.kind ?? "text", data: Buffer.from(payloadBytes(step.data, place)).toString("base64") });
      } else if (step.hostClose) {
        await adapter.call("server.wsClose", { conn: step.hostClose, code: step.code ?? 1000, reason: step.reason ?? "" });
      } else if (step.stop) await adapter.call("server.stop", {});
    }
    return { outcome: "pass" };
  } catch (e) {
    if (e instanceof StepFailure) return { outcome: "fail", detail: `step ${index} (${Object.keys(testCase.steps[index - 1]).join(", ")}): ${e.message}` };
    throw e;
  } finally {
    for (const c of conns.values()) c.destroy();
    await adapter.call("server.stop", {}).catch(() => {});
  }
}
