// The guest role of the Runic host protocol corpus (specs/desktop/host-conformance/README.md): the driver plays the host.
// It writes the offer, reads the guest's launch lines, connects to the guest's channel over a real loopback WebSocket,
// sends and matches every message, makes the frame and component requests of the region scenarios, and checks the
// test application's results. The adapter only starts the guest with the test application, runs its actions and
// reports their results (eng/conformance/PROTOCOL.md, "Host protocol").
import { createHmac, randomBytes } from "node:crypto";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, sep } from "node:path";
import { AdapterError, type Adapter, type Outcome } from "../core";
import type { PageTransport } from "./page-transport";
import { upgrade, type WebSocketClient } from "./socket";

export class StepFailure extends Error {}

const check = (condition: unknown, message: string) => {
  if (!condition) throw new StepFailure(message);
};

// The launch-line prefixes (host-protocol.md, "Launch"); the reader also accepts the RAX_ spelling of the rename.
export const OFFER_PREFIX = "RUNIC_HOST_OFFER=";
const LINE_PREFIX = /^(?:RUNIC|RAX)_HOST_(CHANNEL|REFUSED)=/;
const HOST_PROOF = "runic-host/1 host";
const GUEST_PROOF = "runic-host/1 guest";

export const base64url = (bytes: Uint8Array) => Buffer.from(bytes).toString("base64url");
export const proof = (nonce: Uint8Array, label: string, key: string) => base64url(createHmac("sha256", nonce).update(`${label} ${key}`).digest());

const FRAME_URL = /^http:\/\/(127\.0\.0\.1|\[::1\]):\d+(\/[A-Za-z0-9._~-]+)*\/_runic_frame\/[A-Za-z0-9_-]{43}$/;
const COMPONENT_MODULE = /^http:\/\/(127\.0\.0\.1|\[::1\]):\d+(\/[A-Za-z0-9._~-]+)*\/_runic_component\/component\.js$/;
const LAUNCH_URL = /^http:\/\/(127\.0\.0\.1|\[::1\]):\d+(\/[A-Za-z0-9._~-]+)*\/_runic_launch\/[A-Za-z0-9_-]{43}$/;
const redact = (text: string) => text.replace(/[A-Za-z0-9_-]{43}/g, "…");
const show = (value: unknown) => redact(JSON.stringify(value) ?? "undefined");

// The results that report a state the runner waits for (README, "Test application").
const OBSERVED = ["activity", "appearance", "dropped", "invoked", "locale", "placement"];

export function deepEqual(a: any, b: any): boolean {
  if (a === b) return true;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== "object") return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) return a.length === b.length && a.every((v, i) => deepEqual(v, b[i]));
  const ka = Object.keys(a).sort(), kb = Object.keys(b).sort();
  return ka.length === kb.length && ka.every((k, i) => k === kb[i] && deepEqual(a[k], b[k]));
}

export function isType(value: any, type: string): boolean {
  switch (type) {
    case "null":
      return value === null || value === undefined;
    case "string":
      return typeof value === "string";
    case "object":
      return value !== null && typeof value === "object" && !Array.isArray(value);
    case "array":
      return Array.isArray(value);
    case "boolean":
      return typeof value === "boolean";
    case "number":
      return typeof value === "number";
    case "integer":
      return typeof value === "number" && Number.isInteger(value);
  }
  return false;
}

// The temporary files of $file and $directory (README, "Values in messages").
export class ScenarioFiles {
  private root?: string;

  static isPath(value: any) {
    return value && typeof value === "object" && ("$file" in value || "$directory" in value) && Object.keys(value).length === ("then" in value ? 2 : 1);
  }

  path(value: any): string {
    this.root ??= mkdtempSync(join(tmpdir(), "rax-host-conformance-"));
    const directory = "$directory" in value;
    const path = join(this.root, directory ? value.$directory : value.$file);
    if (directory) mkdirSync(path, { recursive: true });
    else if (!existsSync(path)) writeFileSync(path, "Runic host conformance\n");
    return value.then ? [path, ...value.then].join(sep) : path;
  }

  dispose() {
    if (this.root) rmSync(this.root, { recursive: true, force: true });
  }
}

// Whether this machine can listen on [::1].
let ipv6: boolean | undefined;
async function canUseIpv6(): Promise<boolean> {
  if (ipv6 !== undefined) return ipv6;
  const { createServer } = await import("node:net");
  ipv6 = await new Promise<boolean>((resolve) => {
    const server = createServer();
    server.once("error", () => resolve(false));
    server.listen(0, "::1", () => server.close(() => resolve(true)));
  });
  return ipv6;
}

export type GuestContext = { receiveMs: number; transport?: PageTransport };

export class GuestRun {
  private connections = new Map<string, WebSocketClient>();
  private bindings = new Map<string, any>();
  private nonce = randomBytes(32);
  private key?: string;
  private channel?: URL;
  private nextRequest = 0;
  private files = new ScenarioFiles();
  private embedder?: string;
  private allowSameSite = false;
  private browser = false;
  private frameUrls: string[] = [];
  private frameToken?: string;
  private pages: WebSocketClient[] = [];
  private componentModule?: URL;
  private componentUrl?: URL;
  private componentToken?: string;

  constructor(private readonly adapter: Adapter, private readonly scenario: any, private readonly context: GuestContext) {}

  private get limits(): Record<string, number> {
    return this.scenario.arrange?.limits ?? {};
  }

  private get waitMs() {
    return this.context.receiveMs * 2;
  }

  async run(): Promise<Outcome> {
    const steps: any[] = this.scenario.steps;
    let index = 0;
    try {
      await this.adapter.call("host.guest.start", { application: this.scenario.arrange?.application ?? {}, limits: this.limits }, 60_000);
      for (; index < steps.length; index++) await this.step(steps[index]);
      for (const [name, connection] of this.connections) {
        const left = connection.peek();
        if (left) throw new StepFailure(`${name} has an unmatched message: ${left.kind === "text" ? redact(left.text) : `${left.data.length} binary bytes`}`);
      }
      return { outcome: "pass" };
    } catch (e) {
      if (e instanceof AdapterError && e.code === "unsupported") return { outcome: "unsupported", detail: `step ${index + 1}: ${e.message}`, step: index + 1 };
      if (e instanceof AdapterError) throw e;
      const message = e instanceof Error ? e.message : String(e);
      let output = "";
      try {
        const lines: string[] = (await this.adapter.call("host.guest.output", {})).lines;
        if (lines.length) output = `\n  guest output (last lines):\n${lines.slice(-12).map((line) => "    " + redact(line)).join("\n")}`;
      } catch {}
      return { outcome: "fail", step: index + 1, detail: `step ${index + 1} ${show(steps[index])}\n  ${message}${output}` };
    } finally {
      for (const connection of this.connections.values()) connection.close();
      for (const page of this.pages) page.destroy();
      try {
        await this.adapter.call("host.guest.stop", {}, 60_000);
      } catch {}
      this.files.dispose();
    }
  }

  private async step(step: any) {
    const connection = step.connection;
    if ("start" in step) return this.start(step.start);
    if ("line" in step) return this.line(step.line);
    if ("connect" in step) return this.connect(step.connect, step);
    if ("send" in step) return this.send(connection, this.resolve(step.send));
    if ("sendRaw" in step) return this.sendRaw(connection, step.sendRaw);
    if ("receive" in step) return this.receive(connection, step.receive);
    if ("receiveAll" in step) return this.receiveAll(connection, step.receiveAll);
    if ("closed" in step) return this.closed(step.closed);
    if ("disconnect" in step) return this.connections.get(step.disconnect)?.close();
    if ("closeInput" in step) return this.adapter.call("host.guest.closeInput", {});
    if ("advance" in step) {
      const ms = this.limits[step.advance];
      check(typeof ms === "number", `arrange.limits has no ${step.advance}`);
      return Bun.sleep(ms + 500);
    }
    if ("guest" in step) return this.adapter.call("host.guest.act", { as: step.as ?? null, action: this.resolve(step.guest) });
    if ("expect" in step) return this.expect(step.expect);
    if ("frame" in step) return this.frame(step.frame);
    if ("component" in step) return this.component(step.component);
    throw new StepFailure("unknown step");
  }

  // ----- Launch

  private async start(start: any) {
    const input = start === "none" ? null : (start.offerLine ?? OFFER_PREFIX + JSON.stringify(this.resolve(start.offer)));
    await this.adapter.call("host.guest.input", { line: input });
  }

  private async line(expected: any) {
    if (expected === "none") {
      await this.adapter.call("host.guest.exit", { waitMs: this.waitMs }, this.waitMs + 30_000);
      const lines: string[] = (await this.adapter.call("host.guest.output", {})).lines;
      check(!lines.some((line) => LINE_PREFIX.test(line)), "the guest wrote a launch line");
      return;
    }
    const deadline = Date.now() + this.waitMs;
    for (;;) {
      const left = deadline - Date.now();
      check(left > 0, `the guest wrote no launch line within ${this.waitMs} ms`);
      const { line } = await this.adapter.call("host.guest.line", { waitMs: left }, left + 30_000);
      check(line !== null, "the guest's output ended without a launch line");
      const match = LINE_PREFIX.exec(line);
      if (!match) continue;
      let content: any;
      try {
        content = JSON.parse(line.slice(match[0].length));
      } catch {
        throw new StepFailure(`the launch line is not JSON: ${redact(line)}`);
      }
      if ("channel" in expected) {
        check(match[1] === "CHANNEL", `expected a channel line, got ${redact(line)}`);
        this.match(expected.channel, content, "line");
        check(typeof content.url === "string" && typeof content.key === "string", `the channel line is not valid: ${redact(line)}`);
        this.channel = new URL(content.url);
        this.key = content.key;
      } else {
        check(match[1] === "REFUSED", `expected a refusal line, got ${redact(line)}`);
        this.match(expected.refused, content, "line");
      }
      return;
    }
  }

  // ----- Connections

  private async connect(name: string, step: any) {
    const url = this.channel;
    check(url, "no channel line was read");
    const port = url!.port;
    const host = step.host?.replaceAll("{port}", port) ?? url!.host;
    const target = step.path?.replaceAll("{port}", port) ?? url!.pathname;
    const result = await upgrade({
      address: url!.hostname,
      port: Number(port),
      target,
      host,
      headers: Object.entries<string>(step.headers ?? {}),
      timeoutMs: this.waitMs,
    });
    if (step.refused) return check(result.status === null, `the connection was answered with ${result.status}`);
    const expected = step.status ?? 101;
    check(result.status === expected, `the upgrade was answered with ${result.status ?? "nothing"}, not ${expected}`);
    check(!result.problem, result.problem);
    if (result.socket) this.connections.set(name, result.socket);
  }

  private connection(name: string) {
    const found = this.connections.get(name);
    check(found, `no connection ${name}`);
    return found!;
  }

  private send(name: string, message: any) {
    if (message.kind === "region-attach") {
      this.embedder = message.embedder;
      this.allowSameSite = message.allowSameSite === true;
      this.browser = message.browser === true;
    }
    this.connection(name).sendText(JSON.stringify(message));
  }

  private sendRaw(name: string, raw: any) {
    const socket = this.connection(name);
    if ("binary" in raw) socket.sendBinary(new Uint8Array(raw.binary));
    else if ("text" in raw) socket.sendText(raw.text);
    else socket.sendText("{}" + " ".repeat(raw.textBytes - 2));
  }

  private async next(name: string, what: string): Promise<any> {
    const message = await this.connection(name).next(this.waitMs);
    check(message !== undefined, `${name} sent nothing within ${this.waitMs} ms; expected ${what}`);
    check(message !== null, `${name} closed instead of sending ${what}`);
    check(message!.kind === "text", `${name} sent a binary message`);
    try {
      return JSON.parse((message as any).text);
    } catch {
      throw new StepFailure(`${name} sent a message that is not JSON: ${redact((message as any).text)}`);
    }
  }

  private async receive(name: string, expected: any) {
    this.match(expected, await this.next(name, show(expected)), "message");
  }

  private async receiveAll(name: string, expected: any[]) {
    const messages = [];
    for (let i = 0; i < expected.length; i++) messages.push(await this.next(name, `message ${i + 1} of ${expected.length}`));
    const left = [...expected];
    for (const message of messages) {
      const index = left.findIndex((candidate) => this.tryMatch(candidate, message));
      check(index >= 0, `${show(message)} matches none of ${show(left)}`);
      left.splice(index, 1);
    }
  }

  private async closed(closed: any) {
    const socket = this.connection(closed.connection);
    const message = await socket.next(this.waitMs);
    check(message !== undefined, `${closed.connection} did not close within ${this.waitMs} ms`);
    check(message === null, `${closed.connection} sent ${message && message.kind === "text" ? redact(message.text) : "a binary message"} instead of closing`);
    if (closed.status !== undefined) check(socket.closeCode === closed.status, `${closed.connection} closed with ${socket.closeCode}, not ${closed.status}`);
  }

  // ----- Values

  private secret(name: string): string {
    const key = () => {
      check(this.key, "no channel line gave a key yet");
      return this.key!;
    };
    switch (name) {
      case "nonce":
        return base64url(this.nonce);
      case "key":
        return key();
      case "other-key":
        return base64url(randomBytes(32));
      case "host-proof":
        return proof(this.nonce, HOST_PROOF, key());
      case "host-proof-other-nonce":
        return proof(randomBytes(32), HOST_PROOF, key());
      case "guest-proof":
        return proof(this.nonce, GUEST_PROOF, key());
      case "guest-proof-other-nonce":
        return proof(randomBytes(32), GUEST_PROOF, key());
      case "frame-token":
        check(this.frameToken, "no frame launch gave a token yet");
        return this.frameToken!;
      case "component-token":
        check(this.componentToken, "no component attach gave a token yet");
        return this.componentToken!;
    }
    throw new StepFailure(`the runner cannot write the secret ${name}`);
  }

  // A value the runner writes: $any its default, $id its binding (a new request id the first time), $secret its value.
  resolve(node: any): any {
    if (Array.isArray(node)) return node.map((item) => this.resolve(item));
    if (!node || typeof node !== "object") return node;
    if ("$any" in node) return node.default;
    if ("$id" in node) {
      const id = node.$id;
      if (this.bindings.has(id)) return structuredClone(this.bindings.get(id));
      const kind = this.scenario.ids?.[id]?.kind;
      if (kind === "request") {
        const value = ++this.nextRequest;
        this.bindings.set(id, value);
        return value;
      }
      if (kind === "wire") return structuredClone(this.scenario.ids[id].value);
      throw new StepFailure(`${id} is not bound yet`);
    }
    if ("$secret" in node) return this.secret(node.$secret);
    if (ScenarioFiles.isPath(node)) return this.files.path(node);
    return Object.fromEntries(Object.entries(node).map(([k, v]) => [k, this.resolve(v)]));
  }

  private tryMatch(expected: any, actual: any): boolean {
    const saved = new Map(this.bindings);
    const frames = [...this.frameUrls];
    const component = [this.componentModule, this.componentUrl, this.componentToken] as const;
    try {
      this.match(expected, actual, "message");
      return true;
    } catch (e) {
      if (!(e instanceof StepFailure)) throw e;
      this.bindings = saved;
      this.frameUrls = frames;
      [this.componentModule, this.componentUrl, this.componentToken] = component;
      return false;
    }
  }

  // The loopback family that a frame of the latest region-attach's embedder must use: the other one, where this machine
  // can listen on [::1].
  private frameHost(): string {
    check(this.embedder, "no region-attach was sent");
    const host = new URL(this.embedder!).host.replace(/:\d+$/, "");
    if (host === "[::1]") return "127.0.0.1";
    return ipv6 === false && host === "localhost" ? "127.0.0.1" : "[::1]";
  }

  // The host a component of the latest region-attach comes from (README, "Region scenarios").
  private componentHost(): string {
    check(this.embedder, "no region-attach was sent");
    const host = new URL(this.embedder!).hostname;
    const embedder = host.includes(":") ? `[${host.replace(/^\[|\]$/g, "")}]` : host;
    if (this.browser) return embedder === "[::1]" ? "127.0.0.1" : "[::1]";
    return embedder === "127.0.0.1" && !this.allowSameSite ? "[::1]" : "127.0.0.1";
  }

  private matchComponent(secret: string, text: string): boolean {
    switch (secret) {
      case "component-module": {
        if (!COMPONENT_MODULE.test(text) || hostOf(text) !== this.componentHost()) return false;
        this.componentModule = new URL(text);
        return true;
      }
      case "component-url": {
        const pattern = this.context.transport?.componentUrl;
        if (!pattern) throw new AdapterError("unsupported", "a component's WebSocket address needs a page-transport profile the driver knows");
        if (!pattern.test(text) || !this.componentModule || new URL(text).host !== this.componentModule.host) return false;
        this.componentUrl = new URL(text);
        return true;
      }
      default:
        if (!/^[A-Za-z0-9_-]{43}$/.test(text) || text === this.componentToken) return false;
        this.componentToken = text;
        return true;
    }
  }

  // A message the guest sent against the expected one, by JSON value.
  match(expected: any, actual: any, path: string) {
    if (Array.isArray(expected)) {
      check(Array.isArray(actual) && actual.length === expected.length, `${path}: ${show(actual)} is not ${show(expected)}`);
      expected.forEach((item, i) => this.match(item, actual[i], `${path}/${i}`));
      return;
    }
    if (!expected || typeof expected !== "object") {
      check(deepEqual(expected, actual), `${path}: ${show(actual ?? null)} is not ${show(expected)}`);
      return;
    }
    if ("$any" in expected) {
      const types: string[] = Array.isArray(expected.$any) ? expected.$any : [expected.$any];
      check(types.some((type) => isType(actual, type)), `${path}: ${show(actual ?? null)} is not ${types.join(" or ")}`);
      return;
    }
    if ("$id" in expected) {
      const id = expected.$id;
      if (this.bindings.has(id)) check(deepEqual(this.bindings.get(id), actual), `${path}: ${show(actual)} is not ${id} (${show(this.bindings.get(id))})`);
      else this.bindings.set(id, structuredClone(actual));
      return;
    }
    if ("$secret" in expected) {
      const secret = expected.$secret;
      const text = typeof actual === "string" ? actual : null;
      if (secret === "component-module" || secret === "component-url" || secret === "component-token") {
        check(text !== null && this.matchComponent(secret, text), `${path}: ${show(actual ?? null)} is not a new ${secret}`);
        return;
      }
      if (secret === "frame-url") {
        check(text !== null && FRAME_URL.test(text) && hostOf(text) === this.frameHost() && !this.frameUrls.includes(text), `${path}: ${show(actual ?? null)} is not a new frame-url`);
        this.frameUrls.push(text!);
        return;
      }
      check(secret === "launch-url" ? text !== null && LAUNCH_URL.test(text) : text === this.secret(secret), `${path}: ${show(actual)} is not the ${secret}`);
      return;
    }
    if (ScenarioFiles.isPath(expected)) {
      const local = this.files.path(expected);
      check(actual === local, `${path}: ${show(actual)} is not ${local}`);
      return;
    }
    check(actual && typeof actual === "object" && !Array.isArray(actual), `${path}: ${show(actual ?? null)} is not an object`);
    const keys = Object.keys(expected).sort(), got = Object.keys(actual).sort();
    check(deepEqual(keys, got), `${path}: members ${got.join(",")} are not ${keys.join(",")}`);
    for (const key of keys) this.match(expected[key], actual[key], `${path}/${key}`);
  }

  // ----- Test application

  private async result(as: string, waitMs: number): Promise<any> {
    return this.adapter.call("host.guest.result", { as, waitMs }, waitMs + 30_000);
  }

  private async expect(expect: any) {
    if ("action" in expect) {
      const { action, ...rest } = expect;
      const expected = this.resolve(rest);
      if (expected.cancelled) {
        const r = await this.result(action, this.waitMs);
        check(r.state === "cancelled", `the action was not cancelled: ${show(r)}`);
        return;
      }
      if (typeof expected.failed === "string") {
        const r = await this.result(action, this.waitMs);
        check(r.state === "failed", `the action did not fail with ${expected.failed}: ${show(r)}`);
        check(r.codes?.includes(expected.failed), `the action failed with ${(r.codes ?? []).join(", ")}, not ${expected.failed}`);
        return;
      }
      const observed = OBSERVED.find((key) => key in expected);
      const deadline = Date.now() + this.waitMs;
      for (;;) {
        const r = await this.result(action, Math.max(0, deadline - Date.now()));
        check(r.state !== "pending", `the action did not end within ${this.waitMs} ms`);
        check(r.state === "done", `the action ended ${r.state}${r.message ? `: ${r.message}` : ""}${r.codes ? ` (${r.codes.join(", ")})` : ""}`);
        const value = r.value ?? {};
        if (observed) {
          if (deepEqual(value[observed] ?? null, expected[observed] ?? null)) return;
          if (Date.now() >= deadline) throw new StepFailure(`${observed} is ${show(value[observed] ?? null)}, not ${show(expected[observed] ?? null)}`);
          await Bun.sleep(25);
          continue;
        }
        if (expected.outcome && value.outcome) {
          const [kind, detail] = Object.entries(expected.outcome)[0];
          const [actualKind, actualDetail] = Object.entries(value.outcome)[0] ?? [];
          check(kind === actualKind && (kind === "success" || detail === actualDetail), `the outcome is ${show(value.outcome)}, not ${show(expected.outcome)}`);
          const { outcome: _, ...others } = expected;
          const { outcome: __, ...actualOthers } = value;
          for (const key of Object.keys(others)) check(deepEqual(others[key], actualOthers[key]), `${key} is ${show(actualOthers[key])}, not ${show(others[key])}`);
          return;
        }
        check(deepEqual(value, expected), `the result is ${show(value)}, not ${show(expected)}`);
        return;
      }
    }
    if ("startFailure" in expect) {
      const r = await this.adapter.call("host.guest.exit", { waitMs: this.waitMs }, this.waitMs + 30_000);
      check(r.state === "failed", `the hosted start did not fail: ${show(r)}`);
      check(r.failure === expect.startFailure, `the hosted start failed with ${r.failure}: ${r.message ?? ""}`);
      return;
    }
    if ("exited" in expect) {
      const r = await this.adapter.call("host.guest.exit", { waitMs: this.waitMs }, this.waitMs + 30_000);
      check(r.state !== "running", `the guest did not exit within ${this.waitMs} ms`);
      if (typeof expect.exited === "number") check(r.state === "returned" && r.code === expect.exited, `the guest ended ${r.state} with ${r.code ?? r.failure}, not ${expect.exited}`);
      else check(r.state === "returned" || expect.exited === true, `the guest ended ${r.state}: ${r.failure}`);
      check(!r.processExits?.length, "the guest ended its process instead of returning");
      return;
    }
    if ("running" in expect) {
      const r = await this.adapter.call("host.guest.exit", { waitMs: 0 });
      check(r.state === "running" && !r.processExits?.length, `the guest is no longer running: ${show(r)}`);
      return;
    }
    throw new StepFailure("unknown expectation");
  }

  // ----- region/1 and region-component/1 requests (README, "Region scenarios")

  private transport(): PageTransport {
    const transport = this.context.transport;
    if (!transport) throw new AdapterError("unsupported", "a frame or component request needs a page-transport profile the driver knows");
    return transport;
  }

  private async frame(frame: any) {
    if (frame.request === "closed") return this.pageClosed("frame");
    const transport = this.transport();
    const launch = frame.url === "previous" ? this.frameUrls.at(-2) : this.frameUrls.at(-1);
    check(launch, "no frame-url was bound");
    const surface = new URL("../", launch);
    let status: number;
    switch (frame.request) {
      case "launch": {
        const response = await fetch(launch!, { redirect: "manual" });
        status = response.status;
        await response.arrayBuffer();
        check(!response.headers.has("set-cookie"), "the frame launch set a cookie");
        if (status === 303) {
          const location = response.headers.get("location") ?? "";
          const fragment = /#runic-frame=([A-Za-z0-9_-]{43})$/.exec(location);
          check(fragment && location.startsWith(surface.href), `the frame launch redirected to ${redact(location)}`);
          this.frameToken = fragment![1];
          const policy = response.headers.get("content-security-policy") ?? "";
          check(policy.split(",").map((p) => p.trim()).includes(`frame-ancestors ${this.embedder}`), `the frame launch's policy is ${policy}`);
          check(!response.headers.has("x-frame-options"), "the frame launch sent X-Frame-Options");
          check(response.headers.get("referrer-policy") === "no-referrer", "the frame launch sent no Referrer-Policy: no-referrer");
        }
        break;
      }
      case "bootstrap": {
        const response = await fetch(new URL(transport.bootstrap, surface), { redirect: "manual" });
        status = response.status;
        const text = await response.text();
        if (status === 200) check(!transport.carriesCredential(text), "the frame surface's bootstrap carries the session credential");
        break;
      }
      case "upgrade": {
        let bootstrap = "";
        if (transport.frameNeedsBootstrap) {
          const response = await fetch(new URL(transport.bootstrap, surface), { redirect: "manual" });
          bootstrap = await response.text();
          if (!response.ok) {
            status = response.status;
            break;
          }
        }
        const token = frame.token === undefined ? undefined : this.resolve(frame.token);
        const result = await upgrade({
          address: surface.hostname,
          port: Number(surface.port),
          target: surface.pathname + transport.socket,
          host: surface.host,
          headers: [["Origin", surface.origin], ...(token ? ([["Sec-WebSocket-Protocol", `runic-desktop.${token}`]] as [string, string][]) : [])],
          timeoutMs: this.waitMs,
        });
        status = result.status ?? 0;
        if (result.socket) {
          transport.admitFrame(result.socket, bootstrap);
          this.pages.push(result.socket);
        }
        break;
      }
      default:
        throw new StepFailure(`unknown frame request ${frame.request}`);
    }
    check(frame.refused ? status >= 400 && status < 500 : status === frame.status, `the frame ${frame.request} was answered ${status}, not ${frame.refused ? "refused" : frame.status}`);
  }

  private async component(component: any) {
    if (component.request === "closed") return this.pageClosed("component");
    const transport = this.transport();
    const origin = !component.origin || component.origin === "embedder" ? this.embedder! : component.origin;
    let status: number;
    switch (component.request) {
      case "module": {
        check(this.componentModule, "no component-module was bound");
        const response = await fetch(this.componentModule!, { redirect: "manual", headers: { Origin: origin } });
        status = response.status;
        await response.arrayBuffer();
        check(!response.headers.has("set-cookie"), "the component's module set a cookie");
        if (status === 200) check(response.headers.get("access-control-allow-origin") === origin, "the component's module carries no CORS grant for exactly its origin");
        break;
      }
      case "upgrade": {
        check(this.componentUrl, "no component-url was bound");
        const url = this.componentUrl!;
        const token = component.token === undefined ? undefined : this.resolve(component.token);
        const result = await upgrade({
          address: url.hostname,
          port: Number(url.port),
          target: url.pathname,
          host: url.host,
          headers: [["Origin", origin], ...(token ? ([["Sec-WebSocket-Protocol", `runic-desktop.${token}`]] as [string, string][]) : [])],
          timeoutMs: this.waitMs,
        });
        status = result.status ?? 0;
        if (result.socket) {
          transport.admitComponent(result.socket);
          this.pages.push(result.socket);
        }
        break;
      }
      default:
        throw new StepFailure(`unknown component request ${component.request}`);
    }
    check(component.refused ? status >= 400 && status < 500 : status === component.status, `the component ${component.request} was answered ${status}, not ${component.refused ? "refused" : component.status}`);
  }

  // The guest closes the page that the latest 101 kept, as its region-detach does.
  private async pageClosed(kind: string) {
    const page = this.pages.at(-1);
    check(page, `no ${kind} page is connected`);
    const deadline = Date.now() + this.waitMs;
    for (;;) {
      const message = await page!.next(Math.max(0, deadline - Date.now()));
      if (message === null) return;
      check(message !== undefined, `the guest did not close the ${kind} page within ${this.waitMs} ms`);
    }
  }
}

const hostOf = (text: string) => {
  const url = new URL(text);
  return url.hostname.includes(":") || url.hostname.startsWith("[") ? `[${url.hostname.replace(/^\[|\]$/g, "")}]` : url.hostname;
};

export async function prepareGuest() {
  await canUseIpv6();
}
