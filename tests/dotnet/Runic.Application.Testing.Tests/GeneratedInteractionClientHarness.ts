import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const [generatedDirectory] = Bun.argv.slice(2);
if (!generatedDirectory) throw new Error("Usage: GeneratedInteractionClientHarness.ts <generated-dir>");
const source = await Bun.file(resolve(generatedDirectory, "generatedInteraction.ts")).text();
const contract = /confirm: { contract: "([^"]+)"/.exec(source)?.[1];
if (!contract) throw new Error("Generated interaction contract was not emitted.");
const host = globalThis as typeof globalThis & { window?: Record<string, unknown> };
host.window = host as unknown as Record<string, unknown>;
const generated = await import(pathToFileURL(resolve(generatedDirectory, "generatedInteraction.ts")).href);

type Poll = "__runicInteractionWait" | "__runicInteractionControlWait";
type View = { dispose(): void; ask(): Promise<unknown>; startAsk(): Promise<unknown>;
  interactions: { confirm: { handle(handler: (input: unknown, context: { signal: AbortSignal }) => boolean | Promise<boolean>): () => void } } };

// Finite, explicitly released queues model server long polls. An empty queue
// holds ONE poll; it never manufactures another request. Each scenario owns
// its transport, diagnostics, pending polls and handlers and drains all of them.
class MockBridge {
  connected = true;
  mountToken = "";
  failure: Error | undefined;
  readonly counts = new Map<string, number>();
  readonly history: string[] = [];
  readonly pending = new Map<Poll, (value: string) => void>();
  readonly queued = new Map<Poll, object[]>();
  readonly handlers = new Set<() => void>();
  totalCalls = 0;
  inFlight = 0;
  replies = 0;
  capabilities = 0;
  maxHandlers = 0;

  isConnected = () => this.connected;
  call = async (route: string, ...args: unknown[]): Promise<string> => {
    this.totalCalls++;
    if (this.totalCalls > 64) {
      this.failure ??= new Error("Mock exceeded 64 calls: " + this.history.join(", "));
      this.close();
      throw this.failure;
    }
    this.counts.set(route, (this.counts.get(route) ?? 0) + 1);
    if (this.history.length === 16) this.history.shift();
    this.history.push(route);
    this.inFlight++;
    try {
      // Real transport yields to I/O. This also prevents a broken generated
      // polling loop from starving test deadlines with resolved microtasks.
      await Bun.sleep(1);
      if (!this.connected) return JSON.stringify({ kind: "disconnected" });
      switch (route) {
        case "generatedInteractionSnapshot": return JSON.stringify({ ok: true, state: {
          revision: 0, attempt: 0, acceptedCount: 0, canAsk: true, isAskExecuting: false }, error: null });
        case "generatedInteractionMount": this.mountToken = String(args[0]); return "ok";
        case "generatedInteractionUnmount": return "ok";
        case "generatedInteractionAsk":
          expect(this.capabilities > 0, "Same-tick command preceded handler capability registration.");
          return JSON.stringify({ ok: true, state: {
            revision: 1, attempt: 1, acceptedCount: 0, canAsk: true, isAskExecuting: false }, error: null });
        case "generatedInteractionStartAsk": {
          expect(this.capabilities > 0, "Same-tick operation preceded handler capability registration.");
          return JSON.stringify({ requestId: String(args[0]), kind: "accepted" });
        }
        case "__runicInteractionControl": this.capabilities++; return JSON.stringify({ kind: "ok" });
        case "__runicInteractionReply": this.replies++; return JSON.stringify({ kind: "ok" });
        case "__runicInteractionWait":
        case "__runicInteractionControlWait": {
          const queued = this.queued.get(route)?.shift();
          if (queued) return JSON.stringify(queued);
          expect(!this.pending.has(route), `Overlapping ${route} polls.`);
          return await new Promise<string>(resolve => this.pending.set(route, resolve));
        }
        default: throw new Error(`Unexpected interaction harness route ${route}.`);
      }
    } finally { this.inFlight--; }
  };

  send(route: Poll, message: object): void {
    const waiting = this.pending.get(route);
    if (waiting) { this.pending.delete(route); waiting(JSON.stringify(message)); return; }
    const queue = this.queued.get(route) ?? [];
    expect(queue.length < 2, "Mock queue exceeded two messages.");
    queue.push(message); this.queued.set(route, queue);
  }
  request(id: string, expiresAt = new Date(Date.now() + 10_000).toISOString()): object {
    return { kind: "request", requestId: id, route: "generatedInteraction", presentationId: this.mountToken,
      ownerEpoch: 1, name: "confirm", contract, expiresAt, input: { title: "Generated request", attempt: 1 } };
  }
  cancel(id: string): void {
    this.send("__runicInteractionControlWait", { kind: "cancelled", requestId: id,
      route: "generatedInteraction", presentationId: this.mountToken, ownerEpoch: 1, reason: "cancelled" });
  }
  holdHandler(signal: AbortSignal): Promise<boolean> {
    expect(this.handlers.size < 2, "Mock exceeded two outstanding handlers.");
    return new Promise(resolve => {
      const finish = () => { signal.removeEventListener("abort", finish); this.handlers.delete(finish); resolve(false); };
      this.handlers.add(finish);
      this.maxHandlers = Math.max(this.maxHandlers, this.handlers.size);
      signal.addEventListener("abort", finish, { once: true });
      if (signal.aborted) finish();
    });
  }
  close(): void {
    this.connected = false;
    for (const resolve of this.pending.values()) resolve(JSON.stringify({ kind: "disconnected" }));
    this.pending.clear(); this.queued.clear();
    for (const finish of [...this.handlers]) finish();
  }
}

async function scenario(name: string, test: (mock: MockBridge, view: View) => Promise<void>): Promise<void> {
  const mock = new MockBridge();
  host.window!.__runicBridge = mock;
  let view: View | undefined;
  try {
    view = await generated.connectGeneratedInteraction();
    await test(mock, view!);
    if (mock.failure) throw mock.failure;
  } finally {
    view?.dispose();
    mock.close();
    await eventually(() => mock.inFlight === 0 && mock.handlers.size === 0, `${name}: work did not drain`);
    expect(mock.pending.size === 0 && mock.queued.size === 0, `${name}: polls or queued messages leaked`);
  }
  console.log(`PASS ${name} (${mock.totalCalls} calls, ${mock.maxHandlers} held handlers)`);
}

await scenario("same-tick command", async (mock, view) => {
  view.interactions.confirm.handle(() => false);
  await view.ask();
  expect(mock.capabilities === 1, "Handler was not advertised exactly once.");
});

await scenario("same-tick operation", async (mock, view) => {
  view.interactions.confirm.handle(() => false);
  await view.startAsk();
});

await scenario("cancel before delivery", async (mock, view) => {
  let signal: AbortSignal | undefined;
  view.interactions.confirm.handle((_input, context) => { signal = context.signal; return false; });
  mock.cancel("before");
  await eventually(() => (mock.counts.get("__runicInteractionControlWait") ?? 0) === 2, "Cancellation was not consumed");
  mock.send("__runicInteractionWait", mock.request("before"));
  await eventually(() => signal !== undefined, "Prompt was not delivered");
  expect(signal?.aborted, "Cancellation preceding the prompt did not abort its signal.");
});

await scenario("active cancellation", async (mock, view) => {
  let signal: AbortSignal | undefined;
  view.interactions.confirm.handle((_input, context) => { signal = context.signal; return mock.holdHandler(signal); });
  mock.send("__runicInteractionWait", mock.request("active"));
  await eventually(() => signal !== undefined, "Handler did not start");
  expect(!signal?.aborted, "Handler started cancelled.");
  mock.cancel("active");
  await eventually(() => signal?.aborted === true && mock.handlers.size === 0, "Active cancellation did not drain handler");
});

await scenario("expired request", async (mock, view) => {
  let signal: AbortSignal | undefined;
  view.interactions.confirm.handle((_input, context) => { signal = context.signal; return false; });
  mock.send("__runicInteractionWait", mock.request("expired", "2000-01-01T00:00:00Z"));
  await eventually(() => signal !== undefined, "Expired prompt was not delivered");
  expect(signal?.aborted, "Expired signal was not aborted before handler invocation.");
});

await scenario("active timeout", async (mock, view) => {
  let signal: AbortSignal | undefined;
  view.interactions.confirm.handle((_input, context) => { signal = context.signal; return mock.holdHandler(signal); });
  mock.send("__runicInteractionWait", mock.request("timeout", new Date(Date.now() + 100).toISOString()));
  await eventually(() => signal !== undefined, "Timeout handler did not start");
  expect(!signal?.aborted, "Timeout handler was already expired at delivery.");
  await eventually(() => signal?.aborted === true && mock.handlers.size === 0, "Deadline did not drain handler");
});

await scenario("replace handler during poll", async (mock, view) => {
  let original = false, replacement = false;
  const dispose = view.interactions.confirm.handle(() => { original = true; return false; });
  await eventually(() => mock.pending.has("__runicInteractionWait"), "Initial poll not held");
  dispose();
  view.interactions.confirm.handle(() => { replacement = true; return false; });
  await eventually(() => mock.capabilities === 3, "Replacement capabilities not synchronized");
  mock.send("__runicInteractionWait", mock.request("replacement"));
  await eventually(() => replacement, "Replacement handler not invoked");
  expect(!original, "Stale handler received prompt.");
});

await scenario("two concurrent prompts", async (mock, view) => {
  view.interactions.confirm.handle((_input, context) => mock.holdHandler(context.signal));
  mock.send("__runicInteractionWait", mock.request("first"));
  mock.send("__runicInteractionWait", mock.request("second"));
  await eventually(() => mock.handlers.size === 2, "Second prompt waited for first handler");
  expect(mock.maxHandlers === 2, "Unexpected concurrent handler count.");
  for (const finish of [...mock.handlers]) finish();
  await eventually(() => mock.replies === 2, "Handlers did not reply independently");
});

for (const kind of ["ignored", "unsupported", "disconnected"] as const) {
  await scenario(`${kind} stops polling`, async (mock, view) => {
    view.interactions.confirm.handle(() => false);
    const route: Poll = kind === "disconnected" ? "__runicInteractionControlWait" : "__runicInteractionWait";
    mock.send(route, { kind });
    await Bun.sleep(40);
    expect(mock.counts.get(route) === 1, `${kind} response restarted the poll.`);
  });
}

await scenario("late prompt after disconnect", async (mock, view) => {
  let invoked = false;
  view.interactions.confirm.handle(() => { invoked = true; return false; });
  await eventually(() => mock.pending.size === 2, "Both polls did not start");
  mock.send("__runicInteractionControlWait", { kind: "disconnected" });
  await Bun.sleep(10);
  mock.send("__runicInteractionWait", mock.request("late"));
  await Bun.sleep(10);
  expect(!invoked, "Late prompt invoked handler after disconnect.");
});

console.log("GENERATED_INTERACTION_CLIENT_OK");
function expect(condition: unknown, message: string): asserts condition { if (!condition) throw new Error(message); }
async function eventually(condition: () => boolean, message: string): Promise<void> {
  const deadline = Date.now() + 1000;
  while (!condition()) { if (Date.now() >= deadline) throw new Error(message); await Bun.sleep(2); }
}
