// The host role of the Runic host protocol corpus (specs/desktop/host-conformance/README.md): the driver plays the guest.
// The adapter starts its host with a relay guest, a process that connects to the driver's relay port and relays its
// standard input and output there, so the driver reads the host's offer and writes the launch lines itself. The driver
// listens on its own channel, accepts the host's connections over a real loopback WebSocket, and sends and matches every
// message. The adapter plays the host's user and application (`user`) and reports what the host shows (`observe`) and
// how its start, departures and appearance changes ended (`wait`); the driver compares them (eng/conformance/PROTOCOL.md,
// "Host protocol").
import { randomBytes } from "node:crypto";
import { createServer, type Server, type Socket } from "node:net";
import { AdapterError, type Adapter, type Outcome } from "../core";
import { base64url, deepEqual, isType, proof, ScenarioFiles, StepFailure } from "./guest";
import { Listener, type WebSocketClient } from "./socket";

const check = (condition: unknown, message: string) => {
  if (!condition) throw new StepFailure(message);
};

// The launch-line prefixes (host-protocol.md, "Launch"). The host's offer is read in either spelling of the rename; the
// lines the driver writes use the spelling of the protocol's version 1.
const OFFER = /^(?:RUNIC|RAX)_HOST_OFFER=/;
const CHANNEL_PREFIX = "RUNIC_HOST_CHANNEL=";
const REFUSED_PREFIX = "RUNIC_HOST_REFUSED=";
const CHANNEL_PATH = "/_runic_host";
const HOST_PROOF = "runic-host/1 host";
const GUEST_PROOF = "runic-host/1 guest";

const redact = (text: string) => text.replace(/[A-Za-z0-9_-]{43}/g, "…");
const show = (value: unknown) => redact(JSON.stringify(value) ?? "undefined");

// The relay guest's connection: lines the guest's standard output should carry go to it, and lines of the guest's
// standard input come from it. `exit <code>` makes the relay end its process with that code.
class Relay {
  private server: Server;
  private socket?: Socket;
  private connected: Promise<Socket>;
  private lines: string[] = [];
  private partial = "";
  private ended = false;
  private waiters: (() => void)[] = [];
  port = 0;

  private constructor() {
    let accepted!: (socket: Socket) => void;
    this.connected = new Promise((resolve) => (accepted = resolve));
    this.server = createServer((socket) => {
      if (this.socket) return socket.destroy();
      this.socket = socket;
      socket.setEncoding("utf8");
      socket.on("data", (chunk: string) => {
        const parts = (this.partial + chunk).split("\n");
        this.partial = parts.pop()!;
        this.lines.push(...parts.map((line) => line.replace(/\r$/, "")));
        this.wake();
      });
      const end = () => {
        this.ended = true;
        this.wake();
      };
      socket.on("close", end);
      socket.on("error", end);
      accepted(socket);
    });
  }

  static async open(): Promise<Relay> {
    const relay = new Relay();
    await new Promise<void>((resolve) => relay.server.listen(0, "127.0.0.1", resolve));
    relay.port = (relay.server.address() as { port: number }).port;
    return relay;
  }

  private wake() {
    for (const waiter of this.waiters.splice(0)) waiter();
  }

  // The guest's next line of standard input: undefined after `timeoutMs`, null once its input ended.
  async next(timeoutMs: number): Promise<string | null | undefined> {
    const deadline = Date.now() + timeoutMs;
    while (!this.lines.length) {
      if (this.ended) return null;
      const left = deadline - Date.now();
      if (left <= 0) return undefined;
      await new Promise<void>((resolve) => {
        const timer = setTimeout(resolve, left);
        this.waiters.push(() => {
          clearTimeout(timer);
          resolve();
        });
      });
    }
    return this.lines.shift()!;
  }

  async write(line: string, timeoutMs: number) {
    const socket = await Promise.race([this.connected, Bun.sleep(timeoutMs).then(() => undefined)]);
    check(socket, `the host started no relay guest within ${timeoutMs} ms`);
    if (!socket!.destroyed) socket!.write(line + "\n");
  }

  close() {
    this.server.close();
    this.socket?.destroy();
  }
}

export type HostContext = { receiveMs: number; guestActivationMs: number };

export class HostRun {
  private connections = new Map<string, WebSocketClient>();
  private disconnected: WebSocketClient[] = [];
  private bindings = new Map<string, any>();
  private nextRequest = 1000;
  private files = new ScenarioFiles();
  private relay?: Relay;
  private listener?: Listener;
  private nonce?: Uint8Array;
  private key?: string;
  // The guest the latest successful hello reply named, which a started host reports.
  private applicationId?: string | null;

  constructor(private readonly adapter: Adapter, private readonly scenario: any, private readonly context: HostContext) {}

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
      return { outcome: "fail", step: index + 1, detail: `step ${index + 1} ${show(steps[index])}\n  ${message}` };
    } finally {
      for (const connection of [...this.connections.values(), ...this.disconnected]) connection.destroy();
      try {
        await this.adapter.call("host.host.stop", {}, 60_000);
      } catch {}
      this.relay?.close();
      this.listener?.close();
      this.files.dispose();
    }
  }

  private async step(step: any) {
    const connection = step.connection;
    if ("start" in step) return this.start();
    if ("offer" in step) return this.offer(step.offer);
    if ("line" in step) return this.line(step.line);
    if ("accept" in step) return this.accept(step.accept);
    if ("send" in step) return this.send(connection, this.resolve(step.send));
    if ("sendRaw" in step) return this.sendRaw(connection, step.sendRaw);
    if ("receive" in step) return this.receive(connection, step.receive);
    if ("receiveAll" in step) return this.receiveAll(connection, step.receiveAll);
    if ("closed" in step) return this.closed(step.closed);
    if ("disconnect" in step) return this.disconnect(step.disconnect);
    if ("exit" in step) return this.relay?.write(`exit ${step.exit}`, this.waitMs).catch(() => {});
    if ("advance" in step) return this.advance(step.advance);
    if ("user" in step) return this.user(step.user);
    if ("expect" in step) return this.expect(step.expect);
    throw new StepFailure("unknown step");
  }

  // ----- Launch

  private async start() {
    this.relay = await Relay.open();
    await this.adapter.call("host.host.start", { host: this.scenario.arrange?.host ?? {}, limits: this.limits, relayPort: this.relay.port }, 60_000);
  }

  private async offer(expected: any) {
    check(this.relay, "no start step ran");
    const line = await this.relay!.next(this.waitMs);
    check(line !== undefined, `the host wrote no offer within ${this.waitMs} ms`);
    check(line !== null, "the guest's standard input ended without an offer");
    const match = OFFER.exec(line!);
    check(match, `not an offer line: ${redact(line!)}`);
    let offer: any;
    try {
      offer = JSON.parse(line!.slice(match![0].length));
    } catch {
      throw new StepFailure(`the offer is not JSON: ${redact(line!)}`);
    }
    const nonce = typeof offer?.nonce === "string" ? Buffer.from(offer.nonce, "base64url") : undefined;
    check(nonce?.length === 32 && base64url(nonce) === offer.nonce, "the offer's nonce is not 32 bytes of base64url");
    this.nonce = nonce;
    this.match(expected, offer, "offer");
  }

  // The channel line names the driver's own listener and a new key.
  private async line(line: any) {
    check(this.relay, "no start step ran");
    if ("text" in line) return this.relay!.write(line.text, this.waitMs);
    if ("refused" in line) return this.relay!.write(REFUSED_PREFIX + JSON.stringify(this.resolve(line.refused)), this.waitMs);
    this.listener ??= await Listener.open();
    this.key = base64url(randomBytes(32));
    const channel = { url: `ws://127.0.0.1:${this.listener.port}${CHANNEL_PATH}`, key: this.key, protocol: line.channel.protocol };
    await this.relay!.write(CHANNEL_PREFIX + JSON.stringify(channel), this.waitMs);
  }

  // ----- Connections

  private async accept(name: string) {
    check(this.listener, "no channel line was written");
    const socket = await this.listener!.accept(this.waitMs);
    check(socket, `the host did not connect within ${this.waitMs} ms`);
    this.connections.set(name, socket!);
  }

  // The runner closes the connection; what the host sent on it before is no longer matched.
  private disconnect(name: string) {
    const socket = this.connection(name);
    socket.close();
    this.connections.delete(name);
    this.disconnected.push(socket);
  }

  private connection(name: string) {
    const found = this.connections.get(name);
    check(found, `no connection ${name}`);
    return found!;
  }

  private send(name: string, message: any) {
    if (message.ok === true && message.guest) this.applicationId = message.guest.applicationId ?? null;
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

  // The host's next messages, as many as listed, in any order: a reply and the push its request caused are sent
  // independently.
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

  // A host that fixes how long an activation after the guest's request counts as the guest's (`guestActivation`), as
  // Runic's does, is waited at least that long (README, "Arrange").
  private async advance(name: string) {
    const ms = this.limits[name];
    if (name === "guestActivation") return Bun.sleep(Math.max(ms ?? 0, this.context.guestActivationMs) + 500);
    check(typeof ms === "number", `arrange.limits has no ${name}`);
    return Bun.sleep(ms + 500);
  }

  // ----- Values

  private secret(name: string): string {
    const nonce = () => {
      check(this.nonce, "no offer gave a nonce yet");
      return this.nonce!;
    };
    const key = () => {
      check(this.key, "no channel line gave a key yet");
      return this.key!;
    };
    switch (name) {
      case "nonce":
        return base64url(nonce());
      case "key":
        return key();
      case "other-key":
        return base64url(randomBytes(32));
      case "host-proof":
        return proof(nonce(), HOST_PROOF, key());
      case "host-proof-other-nonce":
        return proof(randomBytes(32), HOST_PROOF, key());
      case "guest-proof":
        return proof(nonce(), GUEST_PROOF, key());
      case "guest-proof-other-nonce":
        return proof(randomBytes(32), GUEST_PROOF, key());
    }
    throw new StepFailure(`the runner cannot write the secret ${name}`);
  }

  // A value the runner writes: $any its default, $id its binding (a new request id the first time; the runner's own
  // window and item ids are literal values in this role), $secret its value, $file and $directory a path.
  resolve(node: any): any {
    if (Array.isArray(node)) return node.map((item) => this.resolve(item));
    if (!node || typeof node !== "object") return node;
    if ("$any" in node) return node.default;
    if ("$id" in node) {
      const id = node.$id;
      if (this.bindings.has(id)) return structuredClone(this.bindings.get(id));
      if (this.scenario.ids?.[id]?.kind === "wire") return structuredClone(this.scenario.ids[id].value);
      const value = ++this.nextRequest;
      this.bindings.set(id, value);
      return value;
    }
    if ("$secret" in node) return this.secret(node.$secret);
    if (ScenarioFiles.isPath(node)) return this.files.path(node);
    return Object.fromEntries(Object.entries(node).map(([k, v]) => [k, this.resolve(v)]));
  }

  private tryMatch(expected: any, actual: any): boolean {
    const saved = new Map(this.bindings);
    try {
      this.match(expected, actual, "message");
      return true;
    } catch (e) {
      if (!(e instanceof StepFailure)) throw e;
      this.bindings = saved;
      return false;
    }
  }

  // A message or state of the host against the expected one, by JSON value. An expected member that is null matches one
  // the host leaves out; the host sends no member the scenario does not name.
  match(expected: any, actual: any, path: string) {
    actual ??= null;
    if (Array.isArray(expected)) {
      check(Array.isArray(actual) && actual.length === expected.length, `${path}: ${show(actual)} is not ${show(expected)}`);
      expected.forEach((item, i) => this.match(item, actual[i], `${path}/${i}`));
      return;
    }
    if (!expected || typeof expected !== "object") {
      check(deepEqual(expected, actual), `${path}: ${show(actual)} is not ${show(expected)}`);
      return;
    }
    if ("$any" in expected) {
      const types: string[] = Array.isArray(expected.$any) ? expected.$any : [expected.$any];
      check(types.some((type) => isType(actual, type)), `${path}: ${show(actual)} is not ${types.join(" or ")}`);
      return;
    }
    if ("$id" in expected) {
      const id = expected.$id;
      if (this.bindings.has(id)) check(deepEqual(this.bindings.get(id), actual), `${path}: ${show(actual)} is not ${id} (${show(this.bindings.get(id))})`);
      else this.bindings.set(id, structuredClone(actual));
      return;
    }
    if ("$secret" in expected) {
      check(actual === this.secret(expected.$secret), `${path}: ${show(actual)} is not the ${expected.$secret}`);
      return;
    }
    if (ScenarioFiles.isPath(expected)) {
      const local = this.files.path(expected);
      check(actual === local, `${path}: ${show(actual)} is not ${local}`);
      return;
    }
    check(actual && typeof actual === "object" && !Array.isArray(actual), `${path}: ${show(actual)} is not an object`);
    for (const key of Object.keys(expected)) this.match(expected[key], actual[key], `${path}/${key}`);
    const extra = Object.keys(actual).filter((key) => !(key in expected));
    check(!extra.length, `${path}: ${show(actual)} has members ${extra.join(", ")} that ${show(expected)} does not`);
  }

  // ----- The host's user and application

  private async user(action: any) {
    const result = await this.adapter.call("host.host.user", { action: this.resolve(action), waitMs: this.waitMs }, this.waitMs + 30_000);
    if (typeof result?.failed === "string") throw new StepFailure(result.failed);
  }

  private async observe(args: any): Promise<any> {
    return (await this.adapter.call("host.host.observe", args)).value;
  }

  // Asks again until the state matches or the runner's timeout passes.
  private async until(args: any, matches: (value: any) => boolean, describe: (value: any) => string) {
    const deadline = Date.now() + this.waitMs;
    for (;;) {
      const value = await this.observe(args);
      if (matches(value)) return;
      if (Date.now() >= deadline) throw new StepFailure(describe(value));
      await Bun.sleep(25);
    }
  }

  private matches(expected: any, actual: any): boolean {
    try {
      this.match(expected, actual, "state");
      return true;
    } catch (e) {
      if (!(e instanceof StepFailure)) throw e;
      return false;
    }
  }

  // The members a scenario names, of a state with more.
  private subset(expected: Record<string, any>, actual: any): boolean {
    return actual !== null && typeof actual === "object" && Object.keys(expected).every((key) => this.matches(expected[key], actual[key]));
  }

  private async wait(kind: string): Promise<any> {
    const r = await this.adapter.call("host.host.wait", { kind, waitMs: this.waitMs }, this.waitMs + 30_000);
    check(r.state !== "pending", `the host's ${kind} did not end within ${this.waitMs} ms`);
    return r;
  }

  private async expect(expect: any) {
    const window = expect.window;
    if ("menuBar" in expect)
      return this.until({ kind: "menuBar", window }, (bar) => this.matches(expect.menuBar, bar), (bar) => `the window's menu bar is ${show(bar)}, not ${show(expect.menuBar)}`);
    if ("region" in expect)
      return this.until({ kind: "region", window }, (region) => this.subset(expect.region, region), (region) => `the region is ${show(region)}, not ${show(expect.region)}`);
    if ("page" in expect) {
      const { name } = expect.page;
      return this.until({ kind: "page", window, name }, (page) => this.subset(expect.page, page), (page) => `the page's region is ${show(page)}, not ${show(expect.page)}`);
    }
    if ("closed" in expect) return this.until({ kind: "closed", window }, (closed) => closed === true, () => `the host did not close the window ${window}`);
    if ("clipboard" in expect)
      return this.until({ kind: "clipboard" }, (text) => text === expect.clipboard, (text) => `the host's clipboard has ${show(text)}, not ${show(expect.clipboard)}`);
    if ("launched" in expect) {
      const launched = this.resolve(expect.launched);
      return this.until({ kind: "launched" }, (actual) => deepEqual(actual, launched), (actual) => `the host opened ${show(actual)}, not ${show(launched)}`);
    }
    if ("dialogOwner" in expect) {
      const owner = await this.observe({ kind: "dialogOwner" });
      check(owner === expect.dialogOwner, `the host's last dialogs were created for ${show(owner)}, not ${expect.dialogOwner}`);
      return;
    }
    if ("applied" in expect) {
      const r = await this.wait("applied");
      check(r.value === expect.applied, `SetAppearanceAsync returned ${r.value}`);
      return;
    }
    if ("departure" in expect) {
      const r = await this.wait("departure");
      check(r.value === expect.departure, `the departure was ${r.value ? "allowed" : "refused"}`);
      return;
    }
    if ("started" in expect) {
      const r = await this.wait("started");
      check(r.state === "started", `the host's start failed with ${r.code}: ${r.message ?? ""}`);
      check(r.applicationId === (this.applicationId ?? null), `the host named the guest ${show(r.applicationId)}, not ${show(this.applicationId ?? null)}`);
      return;
    }
    if ("startFailure" in expect) {
      const r = await this.wait("started");
      check(r.state === "failed", "the host started the guest");
      check(r.code === expect.startFailure, `the start failed with ${r.code}: ${r.message ?? ""}`);
      return;
    }
    throw new StepFailure("unknown expectation");
  }
}
