// One case of the Runic Desktop presentation contract (specs/desktop/conformance/README.md). For a scenario the driver
// hands each `arrange` and `steps` operation to the adapter (`desktop.step`), in order, then asks for what the host did
// (`desktop.observe`) until its events match or the timeout passes, and compares:
// - `events`, the exact ordered sequence of the steps' normative events: an event matches by `type` and `subject`, and
//   each member of its expected `data` (an event may carry more);
// - `absentEvents`, of which none may match an event;
// - `states`, each subject's terminal state;
// - `outcomes`, the terminal outcomes (failed or cancelled), exactly, one per subject.
// A vector runs as `desktop.decode`, and a vector of a profile the adapter does not list is excluded (`profile:<id>`).
import { AdapterError, type Adapter, type Outcome } from "../core";
import { deepEqual } from "../host/guest";

type Event = { type: string; subject: string; data?: Record<string, unknown> };

const show = (value: unknown) => JSON.stringify(value);

export function eventMatches(expected: Event, actual: Event): boolean {
  if (expected.type !== actual.type || expected.subject !== actual.subject) return false;
  return Object.entries(expected.data ?? {}).every(([key, value]) => deepEqual(value, actual.data?.[key]));
}

// The first difference between the expected and the observed events, or null.
export function compareEvents(expected: Event[], actual: Event[]): string | null {
  for (let i = 0; i < Math.max(expected.length, actual.length); i++) {
    if (i >= actual.length) return `event ${i + 1}: expected ${show(expected[i])}, the host reported no more`;
    if (i >= expected.length) return `event ${i + 1}: the host reported ${show(actual[i])} after the expected events`;
    if (!eventMatches(expected[i], actual[i])) return `event ${i + 1}: expected ${show(expected[i])}, the host reported ${show(actual[i])}`;
  }
  return null;
}

export function compareObservation(expect: any, observed: { events: Event[]; states: Record<string, string>; outcomes: any[] }): string | null {
  const events = compareEvents(expect.events ?? [], observed.events);
  if (events) return events;
  for (const absent of expect.absentEvents ?? []) {
    const found = observed.events.find((event) => eventMatches(absent, event));
    if (found) return `the host reported ${show(found)}, an absent event`;
  }
  for (const { subject, state } of expect.states ?? []) {
    if (observed.states[subject] !== state) return `${subject} is ${observed.states[subject] ?? "unknown"}, not ${state}`;
  }
  const outcomes: any[] = expect.outcomes ?? [];
  for (const outcome of outcomes) {
    const actual = observed.outcomes.find((o) => o.subject === outcome.subject);
    if (!deepEqual(actual, outcome)) return `the outcome of ${outcome.subject} is ${show(actual ?? null)}, not ${show(outcome)}`;
  }
  const extra = observed.outcomes.filter((o) => !outcomes.some((e) => e.subject === o.subject));
  if (extra.length) return `the host reported outcomes the scenario does not expect: ${show(extra)}`;
  return null;
}

export async function runPresentationCase(adapter: Adapter, c: { kind: string; body: any }, supports: Set<string>, timing: { receiveMs: number }): Promise<Outcome> {
  const body = c.body;
  if (c.kind === "vector") {
    const profiles: string[] = adapter.describe?.profiles ?? [];
    if (!profiles.includes(body.profile)) return { outcome: "excluded", detail: `profile:${body.profile}` };
    const result = await adapter.call("desktop.decode", { profile: body.profile, operation: body.operation, input: body.input });
    if (body.expect.error) {
      if (!deepEqual(result.error ?? null, body.expect.error)) return { outcome: "fail", detail: `the decode gave ${show(result)}, not the error ${show(body.expect.error)}` };
    } else if (!deepEqual(result.value ?? null, body.expect.value.value)) return { outcome: "fail", detail: `the decode gave ${show(result)}, not ${show(body.expect.value.value)}` };
    return { outcome: "pass" };
  }
  const missing = (body.requires ?? []).filter((r: string) => !supports.has(r));
  if (missing.length) return { outcome: "unsupported", detail: `the implementation lists no ${missing.join(", ")}`, requires: missing };
  const operations = [...body.arrange.map((op: any) => ({ ...op, phase: "arrange" })), ...body.steps.map((op: any) => ({ ...op, phase: "step" }))];
  let index = 0;
  try {
    await adapter.call("desktop.start", { policy: body.policy ?? null, ids: body.ids }, 60_000);
    for (; index < operations.length; index++) {
      const { op, target, arguments: args, phase } = operations[index];
      await adapter.call("desktop.step", { op, target, arguments: args ?? {}, phase }, timing.receiveMs * 2 + 30_000);
    }
    const deadline = Date.now() + timing.receiveMs * 2;
    for (;;) {
      const observed = await adapter.call("desktop.observe", {});
      const problem = compareObservation(body.expect, observed);
      if (!problem) return { outcome: "pass" };
      if (Date.now() >= deadline) return { outcome: "fail", detail: `${problem}\n  events: ${show(observed.events)}` };
      await Bun.sleep(25);
    }
  } catch (e) {
    if (e instanceof AdapterError && e.code === "unsupported") return { outcome: "unsupported", detail: `${operations[index]?.op}: ${e.message}`, step: index + 1 };
    throw e;
  } finally {
    try {
      await adapter.call("desktop.stop", {}, 60_000);
    } catch {}
  }
}
