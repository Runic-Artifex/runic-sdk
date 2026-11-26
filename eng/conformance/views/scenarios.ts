// The producer role of the Views scenarios (specs/application/conformance/README.md, "Scenarios"), driven through
// an adapter. The driver owns the steps, ids, matching, unordered groups and timeouts; the adapter owns the window,
// its test Views, the transport and the virtual clock. The client role's steps are a later slice (R7 G10).
import type { Adapter } from "../core/adapter";
import { AdapterError } from "../core/adapter";
import type { Outcome } from "../core/results";
import { Messages, ScenarioFailure, abbreviate, expand, valueEqual, type Id } from "./messages";

export type Timing = { receiveMs: number };

type Msg = { replyTo: string | null; text: string };

const isUnordered = (s: any) => s.role !== "client" && "receive" in s && s.unordered === true;
const isSkipped = (s: any) => s.role === "client" || "call" in s || (s.expect && !("model" in s.expect));

export async function runScenario(adapter: Adapter, scenario: any, supports: Set<string>, timing: Timing, implementationRoles: Set<string>): Promise<Outcome> {
  const roles: string[] = scenario.roles ?? [];
  if (!implementationRoles.has("producer")) {
    if (!roles.includes("client")) return { outcome: "excluded", detail: "role:producer" };
    return { outcome: "unsupported", detail: "the driver has no client steps yet (R7 G10)" };
  }
  if (!roles.includes("producer")) return { outcome: "excluded", detail: "role:client" };
  const missing = (scenario.requires ?? []).filter((r: string) => !supports.has(r));
  if (missing.length) return { outcome: "unsupported", detail: `requires ${missing.join(", ")}`, requires: missing };

  try {
    await adapter.call("views.open", {
      view: scenario.view,
      model: expand(scenario.arrange?.model ?? {}),
      inputBytes: scenario.options?.inputBytes ?? null,
    });
  } catch (e) {
    if (e instanceof AdapterError && e.code === "unsupported") return { outcome: "unsupported", detail: e.message };
    throw e;
  }
  const run = new ScenarioRun(adapter, scenario, timing);
  try {
    const steps: any[] = expand(scenario.steps) as any[];
    for (let index = 0; index < steps.length; index++) {
      const step = steps[index];
      if (isUnordered(step)) {
        const group: [number, any][] = [];
        for (let next = index; next < steps.length; next++) {
          if (isUnordered(steps[next])) group.push([next, steps[next]]);
          else if (!isSkipped(steps[next])) break;
        }
        try {
          await run.unordered(group);
        } catch (e) {
          if (e instanceof ScenarioFailure)
            return fail(`the unordered steps ${group.map(([i]) => i + 1).join(", ")}:\n  ${e.message}`, group[0][0] + 1);
          throw e;
        }
        index = group[group.length - 1][0];
        continue;
      }
      try {
        await run.step(step);
      } catch (e) {
        if (e instanceof ScenarioFailure) return fail(`step ${index + 1} ${abbreviate(step, 300)}:\n  ${e.message}`, index + 1);
        if (e instanceof AdapterError && e.code === "unsupported")
          return { outcome: "unsupported", detail: `step ${index + 1}: the adapter does not support it: ${e.message}` };
        throw e;
      }
    }
    const leftover = await run.unmatched();
    if (leftover) return fail(leftover, steps.length);
    return { outcome: "pass" };
  } finally {
    if (adapter.alive) await adapter.call("views.dispose", {}).catch(() => {});
  }
}

const fail = (detail: string, step: number): Outcome => ({ outcome: "fail", detail, step });

class ScenarioRun {
  private messages: Messages;
  private connections = new Map<string, boolean>(); // name -> open
  private first: string | null = null;
  private requests = new Map<string, string>(); // request step id -> connection
  private requestRoutes = new Map<string, string | null>();
  private requestKinds = new Map<string, string | null>();
  private closes = new Set<string>();
  private flushes = new Set<string>();

  constructor(private readonly adapter: Adapter, private readonly scenario: any, private readonly timing: Timing) {
    const ids: Record<string, Id> = scenario.ids ?? {};
    this.messages = new Messages(ids, async (name) => (await adapter.call("views.provide", { name }))?.value ?? null);
  }

  async step(step: any) {
    const a = this.adapter;
    if (step.role === "client") return;
    if ("connect" in step) {
      const name = step.connect;
      this.messages.id(name, "connection");
      if (this.connections.has(name)) throw new ScenarioFailure(`The connection ${name} was connected before.`);
      await a.call("views.connect", { conn: name });
      this.connections.set(name, true);
      this.first ??= name;
    } else if ("disconnect" in step) {
      const name = this.open(step.disconnect);
      this.connections.set(name, false);
      await a.call("views.disconnect", { conn: name });
    } else if ("send" in step) {
      const id = step.id;
      this.messages.id(id, "request");
      if (this.requests.has(id)) throw new ScenarioFailure(`The request ${id} was sent before.`);
      const conn = this.connectionOf(step);
      const text = await this.messages.write(step.send);
      this.requests.set(id, conn);
      this.requestRoutes.set(id, typeof step.send?.route === "string" ? step.send.route : null);
      this.requestKinds.set(id, typeof step.send?.kind === "string" ? step.send.kind : null);
      await a.call("views.send", { conn, tag: id, text });
    } else if ("receive" in step) {
      const conn = this.connectionOf(step);
      const replyTo = this.replyTo(step, conn);
      const what = replyTo ? `the reply to ${replyTo}` : "a push";
      const message = await this.next(conn, `${what}: ${abbreviate(step.receive, 300)}`);
      if (message.replyTo !== replyTo)
        throw new ScenarioFailure(`Expected ${what}, but the next message was ${describe(message)}: ${abbreviate(message.text)}`);
      const actual = this.withoutRelease(step.receive, parse(message), replyTo);
      try {
        this.messages.match(step.receive, actual, this.routeOf(replyTo));
      } catch (e) {
        if (e instanceof ScenarioFailure) throw new ScenarioFailure(`${e.message}\n  actual: ${abbreviate(message.text, 1200)}`);
        throw e;
      }
    } else if ("advance" in step) {
      await a.call("views.advance", { ms: step.advance });
    } else if ("delivery" in step) {
      await a.call("views.delivery", { hold: step.delivery === "hold" });
    } else if ("mutate" in step) {
      for (const m of step.mutate) if (m?.op === "waitPresented") this.messages.id(m.id, "wait");
      await a.call("views.mutate", { mutations: step.mutate, each: step.units === "each" });
    } else if ("flushFields" in step) {
      const id = step.flushFields.id;
      this.messages.id(id, "flush");
      if (this.flushes.has(id)) throw new ScenarioFailure(`The flush ${id} was started before.`);
      this.flushes.add(id);
      await a.call("views.flushFields", { id });
    } else if ("closeWindow" in step) {
      const id = step.closeWindow.id;
      this.messages.id(id, "close");
      if (this.closes.has(id)) throw new ScenarioFailure(`The close ${id} was started before.`);
      this.closes.add(id);
      await a.call("views.closeWindow", { id, timeout: step.closeWindow.timeout ?? null });
    } else if ("setLocale" in step) {
      await a.call("views.setLocale", { locale: step.setLocale });
    } else if ("setAppearance" in step) {
      await a.call("views.setAppearance", { appearance: step.setAppearance });
    } else if ("expect" in step) {
      await this.settle();
      const e = step.expect;
      if (e.model) await this.expectModel(e.model);
      if (e.presented) await this.expectResult("waitResult", "wait", e.presented.id, e.presented.result);
      if (e.closed) await this.expectResult("closeResult", "close", e.closed.id, e.closed.acknowledged);
      if (e.flushed) await this.expectResult("flushResult", "flush", e.flushed.id, e.flushed.answered);
      if (e.languages) {
        const conn = this.open(e.languages.connection);
        const actual = (await a.call("views.languages", { conn }))?.value;
        if (!valueEqual(actual, e.languages.value))
          throw new ScenarioFailure(`The connection's languages are ${JSON.stringify(actual)}, not ${JSON.stringify(e.languages.value)}.`);
      }
    } else if (!("call" in step)) {
      throw new ScenarioFailure(`The step has no action a producer runner knows: ${Object.keys(step).join(", ")}`);
    }
  }

  // expect.model waits: read again until every member matches, up to the receive timeout.
  private async expectModel(model: Record<string, unknown>) {
    const deadline = Date.now() + this.timing.receiveMs;
    for (;;) {
      try {
        for (const [member, value] of Object.entries(model)) {
          const actual = (await this.adapter.call("views.readMember", { member }))?.value;
          this.messages.route = null;
          this.messages.matchAt(value, actual, `model.${member}`, null);
        }
        return;
      } catch (e) {
        if (!(e instanceof ScenarioFailure) || Date.now() >= deadline) throw e;
        await Bun.sleep(5);
      }
    }
  }

  // presented, closed and flushed: "pending" checks now; a boolean waits for completion up to the receive timeout.
  private async expectResult(op: string, kind: string, id: string, expected: unknown) {
    this.messages.id(id, kind);
    const pending = typeof expected === "string";
    const result = (await this.adapter.call(`views.${op}`, { id, waitMs: pending ? 0 : this.timing.receiveMs }, this.timing.receiveMs + 30_000))?.result;
    if (pending) {
      if (result !== null && result !== undefined) throw new ScenarioFailure(`The ${kind} ${id} completed with ${result}; expected it pending.`);
      return;
    }
    if (result === null || result === undefined) throw new ScenarioFailure(`The ${kind} ${id} did not complete within ${this.timing.receiveMs} ms.`);
    if (result !== expected) throw new ScenarioFailure(`The ${kind} ${id} completed with ${result}, expected ${expected}.`);
  }

  async unordered(group: [number, any][]) {
    const conn = this.connectionOf(group[0][1]);
    const remaining = group.map(([index, step]) => {
      if (this.connectionOf(step) !== conn) throw new ScenarioFailure(`Step ${index + 1} is on another connection than step ${group[0][0] + 1}.`);
      return { index, replyTo: this.replyTo(step, conn), expected: step.receive };
    });
    this.messages.beginGroup();
    try {
      await this.matchGroup(conn, remaining);
    } finally {
      this.messages.endGroup();
    }
  }

  private async matchGroup(conn: string, remaining: { index: number; replyTo: string | null; expected: any }[]) {
    while (remaining.length) {
      const message = await this.next(conn, remaining.map((r) => `step ${r.index + 1}: ${abbreviate(r.expected, 200)}`).join("; "));
      const parsed = parse(message);
      const failures: string[] = [];
      let matched = -1;
      for (let c = 0; c < remaining.length && matched < 0; c++) {
        const r = remaining[c];
        if (r.replyTo !== message.replyTo) continue;
        let failure: string | null;
        try {
          failure = this.messages.tryMatch(r.expected, this.withoutRelease(r.expected, parsed, r.replyTo), this.routeOf(r.replyTo));
        } catch (e) {
          if (!(e instanceof ScenarioFailure)) throw e;
          failure = e.message;
        }
        if (failure) failures.push(`step ${r.index + 1}: ${failure}`);
        else matched = c;
      }
      if (matched < 0)
        throw new ScenarioFailure(
          `The producer sent ${describe(message)}, which none of the remaining steps ${remaining.map((r) => r.index + 1).join(", ")} expects: ${abbreviate(message.text)}` +
            failures.map((f) => `\n  ${f}`).join(""),
        );
      remaining.splice(matched, 1);
    }
  }

  // views.settle (PROTOCOL.md): lets an asynchronous producer finish the work it has in flight before the driver
  // reads state or checks that nothing is left unmatched (R6 F8). Not settling within the budget is not a failure by
  // itself; the check that follows reports what is missing or left over.
  private async settle() {
    await this.adapter.call("views.settle", { waitMs: this.timing.receiveMs }, this.timing.receiveMs + 30_000);
  }

  // At the end: no connection may have a message no receive step matched.
  async unmatched(): Promise<string | null> {
    await this.settle();
    for (const [name, open] of this.connections) {
      if (!open) continue;
      const message: Msg | null = (await this.adapter.call("views.next", { conn: name, waitMs: 0 }))?.message ?? null;
      if (message) return `${this.scenario.id}: the connection ${name} has a message no receive step matched (${describe(message)}): ${abbreviate(message.text)}`;
    }
    return null;
  }

  private async next(conn: string, expected: string): Promise<Msg> {
    const r = await this.adapter.call("views.next", { conn, waitMs: this.timing.receiveMs }, this.timing.receiveMs + 30_000);
    if (!r?.message) throw new ScenarioFailure(`The connection ${conn} received no message within ${this.timing.receiveMs} ms; expected ${expected}`);
    return r.message;
  }

  private replyTo(step: any, conn: string): string | null {
    const id = step.replyTo;
    if (id === undefined || id === null) return null;
    this.messages.id(id, "request");
    if (this.requests.get(id) !== conn) throw new ScenarioFailure(`The request ${id} was not sent on the connection ${conn}.`);
    return id;
  }

  // README "Comparing messages", Release: a reply to a hello may carry release, a string; unless the step's message
  // lists release, the reply is compared without it.
  private withoutRelease(expected: any, actual: any, replyTo: string | null): any {
    if (!replyTo || this.requestKinds.get(replyTo) !== "hello" || !actual || typeof actual !== "object" || Array.isArray(actual)) return actual;
    if (!("release" in actual) || (expected && typeof expected === "object" && "release" in expected)) return actual;
    if (typeof actual.release !== "string") throw new ScenarioFailure(`$.release: a hello reply's release is a string, actual ${abbreviate(actual.release)}`);
    const { release: _, ...rest } = actual;
    return rest;
  }

  private routeOf(replyTo: string | null) {
    return replyTo ? this.requestRoutes.get(replyTo) ?? null : null;
  }

  private connectionOf(step: any): string {
    if (typeof step.connection === "string") return this.open(step.connection);
    if (!this.first) throw new ScenarioFailure("The step uses a connection before the scenario connected one.");
    if (!this.connections.get(this.first)) throw new ScenarioFailure(`The connection ${this.first} is closed.`);
    return this.first;
  }

  private open(name: string): string {
    this.messages.id(name, "connection");
    if (!this.connections.has(name)) throw new ScenarioFailure(`The connection ${name} is not connected.`);
    if (!this.connections.get(name)) throw new ScenarioFailure(`The connection ${name} is closed.`);
    return name;
  }
}

const describe = (m: Msg) => (m.replyTo === null ? "a push" : `the reply to ${m.replyTo}`);

function parse(m: Msg): unknown {
  try {
    return JSON.parse(m.text);
  } catch (e) {
    throw new ScenarioFailure(`The producer sent a message that is not JSON (${(e as Error).message}): ${abbreviate(m.text)}`);
  }
}
