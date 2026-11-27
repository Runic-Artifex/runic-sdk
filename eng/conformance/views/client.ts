// The client role of the Views scenarios (specs/application/conformance/README.md, "Scenarios"), driven through an
// adapter (PROTOCOL.md, "Client scenarios"). The driver plays the producer: it writes each message the host delivers
// and checks each request the client sends, with the ids, generators and comparison rules of the producer role. The
// adapter owns the client, its test Views, the scripted host bridge and the virtual clock, and runs the calls.
import type { Adapter } from "../core/adapter";
import { AdapterError } from "../core/adapter";
import type { Outcome } from "../core/results";
import { Messages, ScenarioFailure, abbreviate, expand, valueEqual, type Id } from "./messages";
import type { Timing } from "./scenarios";

// The runner capability that the producer runner provides: in the client role the driver is that runner, and it skips
// delivery steps (README "Steps").
const producerCapabilities = ["held-delivery"];

// Steps only a producer runner runs (README "Steps": the client runner skips them).
const producerSteps = ["mutate", "delivery", "flushFields", "closeWindow", "setLocale", "setAppearance", "upload", "endUpload"];

export async function runClientScenario(adapter: Adapter, scenario: any, supports: Set<string>, timing: Timing): Promise<Outcome> {
  const missing = (scenario.requires ?? []).filter((r: string) => !supports.has(r) && !producerCapabilities.includes(r));
  if (missing.length) return { outcome: "unsupported", detail: `requires ${missing.join(", ")}`, requires: missing };
  try {
    await adapter.call("client.begin", { view: scenario.view });
  } catch (e) {
    if (e instanceof AdapterError && e.code === "unsupported") return { outcome: "unsupported", detail: e.message };
    throw e;
  }
  const run = new ClientRun(adapter, scenario, timing);
  try {
    const steps: any[] = expand(scenario.steps) as any[];
    for (let index = 0; index < steps.length; index++) {
      const step = steps[index];
      try {
        await run.step(step);
      } catch (e) {
        if (e instanceof ScenarioFailure) return { outcome: "fail", detail: `step ${index + 1} ${abbreviate(step, 300)}:\n  ${e.message}`, step: index + 1 };
        if (e instanceof AdapterError && e.code === "unsupported")
          return { outcome: "unsupported", detail: `step ${index + 1}: the adapter does not support it: ${e.message}` };
        throw e;
      }
    }
    const leftover = await run.unmatched();
    if (leftover) return { outcome: "fail", detail: leftover, step: steps.length };
    return { outcome: "pass" };
  } finally {
    if (adapter.alive) await adapter.call("client.end", {}).catch(() => {});
  }
}

type Request = { conn: string; tag: string; route: string | null };

class ClientRun {
  private messages: Messages;
  private connections = new Map<string, boolean>(); // name -> open
  private first: string | null = null;
  private requests = new Map<string, Request>(); // send step id -> the request the client sent
  private sessions = new Map<string, string>(); // connection -> the session of its hello
  private calls = new Set<string>();

  constructor(private readonly adapter: Adapter, private readonly scenario: any, private readonly timing: Timing) {
    const ids: Record<string, Id> = scenario.ids ?? {};
    // A provided id that the driver, as the producer, writes before the client sent it: the corpus gives it no value,
    // so the driver writes one of its own (PROTOCOL.md, "Client scenarios").
    this.messages = new Messages(ids, async (name) => (ids[name]?.kind === "wire" ? ids[name]!.value : /epoch/i.test(name) ? 1 : `provided:${name}`), true);
  }

  async step(step: any) {
    const a = this.adapter;
    if (step.role === "producer") return;
    if ("connect" in step) {
      const name = step.connect;
      this.messages.id(name, "connection");
      if (this.connections.has(name)) throw new ScenarioFailure(`The connection ${name} was connected before.`);
      await a.call("client.connect", { conn: name });
      this.connections.set(name, true);
      this.first ??= name;
    } else if ("disconnect" in step) {
      const name = this.open(step.disconnect);
      this.connections.set(name, false);
      await a.call("client.disconnect", { conn: name });
    } else if ("send" in step) {
      await this.send(step);
    } else if ("receive" in step) {
      const conn = this.connectionOf(step);
      let replyTo: Request | null = null;
      if (step.replyTo !== undefined && step.replyTo !== null) {
        this.messages.id(step.replyTo, "request");
        replyTo = this.requests.get(step.replyTo) ?? null;
        if (!replyTo) throw new ScenarioFailure(`The client sent no request ${step.replyTo} that this step could answer.`);
        if (replyTo.conn !== conn) throw new ScenarioFailure(`The request ${step.replyTo} was not sent on the connection ${conn}.`);
      }
      const text = await this.messages.write(step.receive, replyTo?.route ?? null);
      if (step.drop === true) return;
      await a.call("client.deliver", { conn, replyTo: replyTo?.tag ?? null, text });
    } else if ("advance" in step) {
      await a.call("client.advance", { ms: step.advance });
    } else if ("call" in step) {
      const call = { ...step.call };
      this.messages.id(call.id, "call");
      if (this.calls.has(call.id)) throw new ScenarioFailure(`The call ${call.id} was made before.`);
      this.calls.add(call.id);
      // An operation call names the operation's id; the adapter gets its value, as the driver writes it.
      if (typeof call.operation === "string") call.operation = await this.idValue(call.operation);
      await a.call("client.call", { id: call.id, call });
    } else if ("expect" in step) {
      await this.expect(step.expect);
    } else if (!producerSteps.some((name) => name in step)) {
      throw new ScenarioFailure(`The step has no action a client runner knows: ${Object.keys(step).join(", ")}`);
    }
  }

  // README "Steps", send: the client's next request on the connection equals the message, with the session and
  // languages rules of README "Comparing messages".
  private async send(step: any) {
    const id = step.id;
    this.messages.id(id, "request");
    if (this.requests.has(id)) throw new ScenarioFailure(`The request ${id} was sent before.`);
    const conn = this.connectionOf(step);
    const r = await this.adapter.call("client.nextRequest", { conn, waitMs: this.timing.receiveMs }, this.timing.receiveMs + 30_000);
    if (!r?.request) throw new ScenarioFailure(`The client sent no request on ${conn} within ${this.timing.receiveMs} ms; expected ${abbreviate(step.send, 300)}`);
    const { tag, text } = r.request as { tag: string; text: string };
    let actual: any;
    try {
      actual = JSON.parse(text);
    } catch (e) {
      throw new ScenarioFailure(`The client sent a request that is not JSON (${(e as Error).message}): ${abbreviate(text)}`);
    }
    const expected = step.send;
    const lists = (member: string) => expected && typeof expected === "object" && member in expected;
    if (actual && typeof actual === "object" && !Array.isArray(actual)) {
      if (actual.kind === "hello") {
        if (typeof actual.session === "string") this.sessions.set(conn, actual.session);
        if ("languages" in actual) {
          const languages = actual.languages;
          if (!Array.isArray(languages) || languages.length > 16 || !languages.every((l: unknown) => typeof l === "string"))
            throw new ScenarioFailure(`The hello's languages are not an array of at most 16 strings: ${abbreviate(languages)}`);
          if (!lists("languages")) delete actual.languages;
        }
      } else if ("session" in actual) {
        if (actual.session !== this.sessions.get(conn))
          throw new ScenarioFailure(`The request names the session ${abbreviate(actual.session)}, not the session of its connection's hello (${this.sessions.get(conn) ?? "none"}).`);
        if (!lists("session")) delete actual.session;
      }
    }
    const route = expected && typeof expected === "object" && typeof expected.route === "string" ? expected.route : null;
    try {
      this.messages.match(expected, actual, route);
    } catch (e) {
      if (e instanceof ScenarioFailure) throw new ScenarioFailure(`${e.message}\n  sent: ${abbreviate(text, 1200)}`);
      throw e;
    }
    this.requests.set(id, { conn, tag, route });
  }

  // README "Calls", expectations of the client. A call that should have completed, and a view request's inputs, are
  // waited for up to the receive timeout; everything else is read once the client ran the work it has.
  private async expect(e: any) {
    const a = this.adapter;
    if (e.state) this.compare(e.state.value, (await a.call("client.state", { route: e.state.route }))?.value, `the state of ${e.state.route}`);
    if (e.call) await this.expectCall(e.call);
    if (e.operation) {
      const operation = await this.idValue(e.operation.id);
      this.compare(e.operation.status, (await a.call("client.operation", { operation }))?.status, `the status of the operation ${e.operation.id}`);
    }
    if (e.diagnostics) {
      const codes = (await a.call("client.diagnostics", {}))?.codes;
      if (!valueEqual(codes, e.diagnostics)) throw new ScenarioFailure(`The client reported the diagnostics ${JSON.stringify(codes)}, not ${JSON.stringify(e.diagnostics)}.`);
    }
    if (e.notifications) {
      const count = (await a.call("client.notifications", { route: e.notifications.route }))?.count;
      if (count !== e.notifications.count)
        throw new ScenarioFailure(`The subscribers of ${e.notifications.route} were notified ${count} times, not ${e.notifications.count}.`);
    }
    if ("connection" in e) {
      const state = (await a.call("client.connection", {}))?.state ?? null;
      if (state !== e.connection) throw new ScenarioFailure(`The client reports the connection ${state}, not ${e.connection}.`);
    }
    if ("locale" in e) {
      const locale = (await a.call("client.locale", {}))?.locale ?? null;
      if (locale !== e.locale) throw new ScenarioFailure(`The client reports the locale ${JSON.stringify(locale)}, not ${JSON.stringify(e.locale)}.`);
    }
    if ("appearance" in e) {
      const value = (await a.call("client.appearance", {}))?.appearance ?? null;
      if (!valueEqual(value, e.appearance)) throw new ScenarioFailure(`The client keeps the appearance ${abbreviate(value)}, not ${abbreviate(e.appearance)}.`);
    }
    if (e.requests) {
      const { route, request, inputs } = e.requests;
      const deadline = Date.now() + this.timing.receiveMs;
      for (;;) {
        const actual = (await a.call("client.requests", { route, request }))?.inputs;
        try {
          this.compare(inputs, actual, `the inputs of the ${request} handler of ${route}`);
          break;
        } catch (failure) {
          if (!(failure instanceof ScenarioFailure) || Date.now() >= deadline) throw failure;
          await Bun.sleep(5);
        }
      }
    }
    // model, presented, closed, flushed and languages are the producer's expectations.
  }

  private async expectCall(e: any) {
    this.messages.id(e.id, "call");
    if (!this.calls.has(e.id)) throw new ScenarioFailure(`The scenario expects the call ${e.id}, which it did not make.`);
    const wait = e.pending ? 0 : this.timing.receiveMs;
    const r = await this.adapter.call("client.callResult", { id: e.id, waitMs: wait }, wait + 30_000);
    const describe = () => (r.state === "rejected" ? `rejected with ${abbreviate(r.error)}` : r.state === "resolved" ? `resolved with ${abbreviate(r.value)}` : "pending");
    if (e.pending && r.state !== "pending") throw new ScenarioFailure(`The call ${e.id} is ${describe()}; expected it pending.`);
    if ("resolved" in e) {
      if (r.state !== "resolved") throw new ScenarioFailure(`The call ${e.id} is ${describe()}; expected it resolved with ${abbreviate(e.resolved)}.`);
      this.compare(e.resolved, r.value, `the result of the call ${e.id}`);
    }
    if (e.rejected) {
      if (r.state !== "rejected") throw new ScenarioFailure(`The call ${e.id} is ${describe()}; expected it rejected with ${abbreviate(e.rejected)}.`);
      const expected = e.rejected;
      if (expected.kind === "domain-failed") {
        if (r.error?.kind !== "domain-failed") throw new ScenarioFailure(`The call ${e.id} is ${describe()}; expected a declared failure.`);
        this.compare(expected.failure, r.error.failure, `the declared failure of the call ${e.id}`);
      } else {
        if (r.error?.kind !== expected.kind) throw new ScenarioFailure(`The call ${e.id} is ${describe()}; expected the kind ${expected.kind}.`);
        if (expected.reason !== undefined && r.error?.reason !== expected.reason)
          throw new ScenarioFailure(`The call ${e.id} is ${describe()}; expected the reason ${expected.reason}.`);
      }
    }
  }

  // A value the client exposes, compared as a message (README "Comparing messages").
  private compare(expected: unknown, actual: unknown, what: string) {
    try {
      this.messages.route = null;
      this.messages.matchAt(expected, actual ?? null, "$", null);
    } catch (e) {
      if (e instanceof ScenarioFailure) throw new ScenarioFailure(`${what}: ${e.message}\n  actual: ${abbreviate(actual, 1200)}`);
      throw e;
    }
  }

  // The value of a wire or provided id, as the driver writes it.
  private async idValue(name: string): Promise<unknown> {
    this.messages.id(name, "wire", "provided");
    return JSON.parse(await this.messages.write({ $id: name }));
  }

  // At the end: no connection may have a request no send step matched.
  async unmatched(): Promise<string | null> {
    for (const name of this.connections.keys()) {
      const r = await this.adapter.call("client.nextRequest", { conn: name, waitMs: 0 });
      if (r?.request) return `${this.scenario.id}: the client sent a request on ${name} that no send step matched: ${abbreviate(r.request.text)}`;
    }
    return null;
  }

  private connectionOf(step: any): string {
    if (typeof step.connection === "string") return this.open(step.connection);
    if (!this.first) throw new ScenarioFailure("The step uses a connection before the scenario connected one.");
    return this.first;
  }

  private open(name: string): string {
    this.messages.id(name, "connection");
    if (!this.connections.has(name)) throw new ScenarioFailure(`The connection ${name} is not connected.`);
    if (!this.connections.get(name)) throw new ScenarioFailure(`The connection ${name} is closed.`);
    return name;
  }
}
