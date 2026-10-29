import {
  BridgeError, waitForBridge, type BridgeOperation, type BridgeStreamOperation, type CollectionViewport,
  type CollectionViewportController, type ViewClient,
} from "@runic-artifex/views";
import { bridgeOperations, connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";
import { installMockBridge, mockFailure, type MockBridge, type MockCall, type MockView } from "@runic-artifex/views/mock";
import { Cause, Context, Effect, Exit, Fiber, Layer, ManagedRuntime, Schedule, Scope, Stream } from "effect";
import { TestClock } from "effect/testing";
import { afterEach, beforeEach, describe, expect, expectTypeOf, test } from "vitest";
import {
  catchCase, clientLayer, command, connect, connectionStates, followViewport, fromBridgeError, operation, states, ViewFailed, ViewDisconnected, ViewDomainFailure, ViewReleased,
  type ViewError, type ViewOperationError, type ViewportRange,
} from "../dist/index.js";

const contract = "Tests.CounterViewModel:fingerprint";

interface CounterState { readonly count: number; readonly title: string }
interface CounterClient extends ViewClient<CounterState> {
  increment(): Promise<CounterState>;
  setTitle(value: string): Promise<CounterState>;
  startRun(options?: { readonly requestId?: string }): Promise<BridgeOperation<number>>;
}

const members = { count: (value: unknown) => bridgeWire.integer(value, 0, 1000), title: bridgeWire.string };

// Shaped like a generated module, so the adapter is tested against the same surface.
async function connectCounter(): Promise<CounterClient> {
  const view = await connectView<CounterState>({ contract, route: "counter", members, operations: bridgeOperations });
  const start = (requestId: string) => view.start<number>("run", requestId, undefined, { decode: value => bridgeWire.integer(value, 0, 1000) });
  return {
    get snapshot() { return view.snapshot; },
    subscribe: view.subscribe,
    dispose: view.dispose,
    increment: () => view.command("increment"),
    setTitle: value => {
      if (typeof value !== "string") throw new RangeError("A title is required.");
      return view.set("title", value);
    },
    startRun: (options?: { readonly requestId?: string }) => start(options?.requestId ?? globalThis.crypto.randomUUID()),
  };
}

type Reply = Readonly<Record<string, unknown>>;
let bridge: MockBridge;
let counter: MockView;
// Each `watch` of an operation, which the test answers with a status: the producer's first status of the watch.
let waits: { requestId: string; resolve: (status: object) => void; reject: (cause: unknown) => void }[];
// Answers `watch`, `status` and `cancel` requests; returning undefined lets the mock answer.
let answers: { watch?: (call: MockCall) => Reply | Promise<Reply>; status?: (call: MockCall) => Reply; cancel?: (call: MockCall) => Reply };
// A terminal status carries the revision of the first state that includes the operation's effects.
const status = (kind: string, extra: object = {}) =>
  ({ kind, ...(["succeeded", "failed", "domain-failed", "cancelled"].includes(kind) ? { revision: counter.revision } : {}), ...extra });
const operationOf = (call: MockCall) => call.message["operation"] as string;
const kinds = () => bridge.calls.map(call => call.kind);
const ofKind = (kind: string, route = "counter") => bridge.calls.filter(call => call.kind === kind && call.route === route);
const cancels = () => ofKind("cancel").map(operationOf);
const starts = () => ofKind("start");
const until = async (condition: () => boolean) => {
  for (let attempt = 0; attempt < 200 && !condition(); attempt++) await new Promise(resolve => setTimeout(resolve, 1));
  expect(condition()).toBe(true);
};

beforeEach(() => {
  bridge = installMockBridge();
  waits = [];
  answers = {};
  counter = bridge.view("counter", {
    contract,
    state: { count: 1, title: "One" },
    commands: {
      increment: state => ({ count: (state.count as number) + 1 }),
      fail: () => { throw new Error("Boom"); },
    },
    // Operations stay running: the test decides each outcome through the `watch` it answers.
    operations: { run: "manual" },
  });
  bridge.intercept(call => {
    if (call.route !== "counter") return undefined;
    if (call.kind === "watch") return answers.watch ? answers.watch(call) : new Promise<Reply>((resolve, reject) => {
      waits.push({ requestId: operationOf(call), resolve: status => resolve({ ok: true, status }), reject });
    });
    if (call.kind === "status") return answers.status?.(call);
    if (call.kind === "cancel") return answers.cancel?.(call);
    return undefined;
  });
});
afterEach(() => { delete (globalThis as { __runicBridge?: unknown }).__runicBridge; });

describe("connect", () => {
  test("closing the scope disposes the client", async () => {
    const client = await Effect.runPromise(Effect.scoped(Effect.gen(function* () {
      const client = yield* connect(() => connectCounter());
      expect(yield* command(() => client.increment())).toEqual({ count: 2, title: "One" });
      expect(ofKind("close")).toHaveLength(0);
      return client;
    })));
    expect(ofKind("close")).toHaveLength(1);
    // The Bridge is still connected: only this View was released.
    const exit = await Effect.runPromiseExit(command(() => client.increment()));
    expect(exit).toStrictEqual(Exit.fail(expect.any(ViewReleased)));
  });

  test("accepts a page reference and maps a failed connection", async () => {
    const reference = { kind: "counter", connect: () => connectCounter() };
    const scope = Scope.makeUnsafe();
    const client = await Effect.runPromise(connect(reference).pipe(Scope.provide(scope)));
    expect(client.snapshot.count).toBe(1);
    await Effect.runPromise(Scope.close(scope, Exit.void));
    bridge.view("broken", { state: {} });
    bridge.failNext({ route: "broken", kind: "open" }, { kind: "rejected", message: "Not now" });
    const rejected = await Effect.runPromise(Effect.flip(Effect.scoped(connect(() =>
      connectView<object>({ contract, route: "broken", members: {} })))));
    expect(rejected).toMatchObject({ _tag: "ViewRejected", message: "Not now", route: "broken" });
    delete (globalThis as { __runicBridge?: unknown }).__runicBridge;
    const unavailable = await Effect.runPromise(Effect.flip(command(() => waitForBridge({ timeout: 0 }))));
    expect(unavailable._tag).toBe("ViewUnavailable");
  });
});

describe("clientLayer", () => {
  class Counter extends Context.Service<Counter, CounterClient>()("test/Counter") {}

  test("provides the connected client and disposes it when the runtime is disposed", async () => {
    const runtime = ManagedRuntime.make(clientLayer(Counter, () => connectCounter()));
    const count = await runtime.runPromise(Effect.gen(function* () {
      const counter = yield* Counter;
      return (yield* command(() => counter.increment())).count;
    }));
    expect(count).toBe(2);
    expect(ofKind("open")).toHaveLength(1);
    // The layer is built once per runtime.
    await runtime.runPromise(Counter.use(counter => command(() => counter.increment())));
    expect(ofKind("open")).toHaveLength(1);
    expect(ofKind("close")).toHaveLength(0);
    await runtime.dispose();
    expect(ofKind("close")).toHaveLength(1);
  });

  test("accepts a page reference or a function-style service, and fails with a ViewError", async () => {
    const reference = { kind: "counter", connect: () => connectCounter() };
    const FunctionCounter = Context.Service<CounterClient>("test/FunctionCounter");
    const layer: Layer.Layer<CounterClient, ViewError> = clientLayer(FunctionCounter, reference);
    expect(await Effect.runPromise(Effect.provide(FunctionCounter.useSync(client => client.snapshot.count), layer))).toBe(1);
    // A generated connect function returns the client with Symbol.dispose.
    const generated = () => connectCounter() as Promise<CounterClient & Disposable>;
    expectTypeOf(clientLayer(Counter, generated)).toEqualTypeOf<Layer.Layer<Counter, ViewError>>();
    delete (globalThis as { __runicBridge?: unknown }).__runicBridge;
    const failed = await Effect.runPromise(Effect.flip(Effect.provide(Counter.useSync(counter => counter), clientLayer(Counter, () =>
      waitForBridge({ timeout: 0 }).then(() => connectCounter())))));
    expect(failed._tag).toBe("ViewUnavailable");
  });
});

describe("command", () => {
  test("a call on a disconnected Bridge fails with ViewDisconnected", async () => {
    const client = await connectCounter();
    bridge.disconnect();
    const error = await Effect.runPromise(Effect.flip(command(() => client.increment())));
    expect(error).toBeInstanceOf(ViewDisconnected);
    expect(error.cause).toBeInstanceOf(BridgeError);
    const recovered = await Effect.runPromise(command(() => client.increment()).pipe(
      Effect.catchTag("ViewDisconnected", () => Effect.succeed("offline"))));
    expect(recovered).toBe("offline");
  });

  test(".NET failures keep their detail and other errors are defects", async () => {
    const client = await connectCounter();
    const view = await connectView<CounterState>({ contract, route: "counter", members });
    const failed = await Effect.runPromise(Effect.flip(command(() => view.command("fail"))));
    expect(failed).toBeInstanceOf(ViewFailed);
    expect(failed).toMatchObject({ _tag: "ViewFailed", message: "Boom", route: "counter", detail: { message: "Boom" } });
    const defect = await Effect.runPromiseExit(command(() => client.setTitle(undefined as unknown as string)));
    expect(Exit.isFailure(defect) && Cause.squash(defect.cause)).toBeInstanceOf(RangeError);
  });
});

describe("states", () => {
  test("streams the current and pushed states and survives a silent resync", async () => {
    // Drops pushes while `dropping`, as if the page missed the producer's frames.
    let dropping = false;
    const onMessage = bridge.onMessage.bind(bridge);
    Object.assign(bridge, { onMessage: (listener: (message: string) => void) => onMessage(message => { if (!dropping) listener(message); }) });
    const client = await connectCounter();
    const seen: CounterState[] = [];
    const fiber = Effect.runFork(Stream.runForEach(states(client), state => Effect.sync(() => { seen.push(state); })));
    await until(() => seen.length === 1);
    dropping = true;
    counter.update({ title: "Pushed" }); // changes the producer's state; the page misses its frame
    dropping = false;
    // A frame whose base revision does not match makes the runtime re-read the snapshot.
    counter.push({ kind: "frame", baseRevision: counter.revision + 10, revision: counter.revision + 11,
      changes: [{ field: "title", kind: "set", value: "Lost" }] });
    await until(() => seen.at(-1)?.title === "Pushed");
    expect(ofKind("snapshot")).toHaveLength(1);
    await Effect.runPromise(command(() => client.increment()));
    await until(() => seen.at(-1)?.count === 2);
    expect(fiber.pollUnsafe()).toBeUndefined();
    await Effect.runPromise(Fiber.interrupt(fiber));
    client.dispose();
    expect(seen[0]).toEqual({ count: 1, title: "One" });
    expect(seen.map(state => state.title)).not.toContain("Lost");
  });
});

describe("connectionStates", () => {
  test("streams the connection to .NET and its changes until interrupted", async () => {
    const seen: string[] = [];
    const fiber = Effect.runFork(Stream.runForEach(connectionStates({ lostAfter: 20 }), state => Effect.sync(() => { seen.push(state); })));
    await until(() => seen.at(-1) === "connected");
    bridge.disconnect();
    await until(() => seen.at(-1) === "reconnecting");
    await until(() => seen.at(-1) === "lost");
    bridge.reconnect();
    await until(() => seen.at(-1) === "connected");
    await Effect.runPromise(Fiber.interrupt(fiber));
    bridge.disconnect();
    await new Promise(resolve => setTimeout(resolve, 30));
    expect(seen.at(-1)).toBe("connected");
    expect(seen).toEqual(["connected", "reconnecting", "lost", "connected"]);
  });
});

describe("operation", () => {
  test("succeeds with the result of a succeeded status", async () => {
    const client = await connectCounter();
    const running = Effect.runPromise(operation(() => client.startRun()));
    await until(() => waits.length === 1);
    waits[0]!.resolve(status("succeeded", { result: 7 }));
    expect(await running).toBe(7);
    expect(kinds()).not.toContain("cancel");
  });

  test("maps failed and cancelled statuses", async () => {
    const client = await connectCounter();
    const failed = Effect.runPromise(Effect.flip(operation(() => client.startRun())));
    await until(() => waits.length === 1);
    waits[0]!.resolve(status("failed", { error: { kind: "failed", message: "Disk full" } }));
    expect(await failed).toMatchObject({ _tag: "ViewOperationFailed", message: "Disk full" });
    const cancelled = Effect.runPromise(Effect.flip(operation(() => client.startRun())));
    await until(() => waits.length === 2);
    waits[1]!.resolve(status("cancelled"));
    expect((await cancelled)._tag).toBe("ViewOperationCancelled");
  });

  test("interruption sends the cancellation request", async () => {
    const client = await connectCounter();
    const fiber = Effect.runFork(operation(() => client.startRun()));
    await until(() => waits.length === 1);
    await Effect.runPromise(Fiber.interrupt(fiber));
    const cancel = bridge.calls.find(call => call.kind === "cancel");
    expect(cancel?.message).toMatchObject({ member: "run", operation: waits[0]!.requestId });
    // The mock's operation saw the request through its signal.
    expect(counter.operations("run")[0]!.signal.aborted).toBe(true);
  });

  test("Effect.timeout driven by TestClock interrupts and cancels", async () => {
    const client = await connectCounter();
    const exit = await Effect.runPromise(Effect.gen(function* () {
      const fiber = yield* Effect.forkChild(operation(() => client.startRun()).pipe(Effect.timeout("5 seconds")));
      yield* Effect.promise(() => until(() => waits.length === 1));
      yield* TestClock.adjust("4 seconds");
      expect(kinds()).not.toContain("cancel");
      yield* TestClock.adjust("1 second");
      return yield* Fiber.await(fiber);
    }).pipe(Effect.provide(TestClock.layer())));
    expect(Exit.isFailure(exit) && Cause.squash(exit.cause)).toSatisfy(Cause.isTimeoutError);
    expect(cancels()).toHaveLength(1);
  });

  test("the timeout option fails with ViewOperationTimedOut after cancelling", async () => {
    const client = await connectCounter();
    const error = await Effect.runPromise(Effect.gen(function* () {
      const fiber = yield* Effect.forkChild(Effect.flip(operation(() => client.startRun(), { timeout: "2 seconds" })));
      yield* Effect.promise(() => until(() => waits.length === 1));
      yield* TestClock.adjust("2 seconds");
      return yield* Fiber.join(fiber);
    }).pipe(Effect.provide(TestClock.layer())));
    expect(error).toMatchObject({ _tag: "ViewOperationTimedOut", requestId: waits[0]!.requestId, cancellation: "cancellation-requested" });
    expect(cancels()).toEqual([waits[0]!.requestId]);
  });

  test("an outer timeout waits for the start reply, then cancels the admitted operation", async () => {
    const client = await connectCounter();
    let admit!: () => void;
    // Holds the start, then lets the mock admit it.
    bridge.intercept(call => call.kind === "start"
      ? new Promise<undefined>(resolve => { admit = () => resolve(undefined); }) : undefined);
    const exit = await Effect.runPromise(Effect.gen(function* () {
      const fiber = yield* Effect.forkChild(operation(() => client.startRun()).pipe(Effect.timeout("1 second")));
      yield* Effect.promise(() => until(() => admit !== undefined));
      yield* TestClock.adjust("1 second");
      expect(fiber.pollUnsafe()).toBeUndefined();
      admit();
      return yield* Fiber.await(fiber);
    }).pipe(Effect.provide(TestClock.layer())));
    expect(Exit.isFailure(exit) && Cause.squash(exit.cause)).toSatisfy(Cause.isTimeoutError);
    expect(cancels()).toHaveLength(1);
  });

  test("interrupting between retries cancels the started operation once", async () => {
    const client = await connectCounter();
    answers.watch = () => Promise.reject(new Error("Socket closed"));
    await Effect.runPromise(Effect.gen(function* () {
      const fiber = yield* Effect.forkChild(operation(id => client.startRun({ requestId: id }),
        { requestId: "request-3", retry: Schedule.spaced("1 second") }));
      yield* Effect.promise(() => until(() => ofKind("watch").length === 1));
      yield* TestClock.adjust("1 second");
      yield* Effect.promise(() => until(() => ofKind("watch").length === 2));
      // Now waiting for the next attempt.
      expect(cancels()).toEqual([]);
      yield* Fiber.interrupt(fiber);
    }).pipe(Effect.provide(TestClock.layer())));
    expect(cancels()).toEqual(["request-3"]);
  });

  test("exhausted retries cancel the last unobserved operation once", async () => {
    const client = await connectCounter();
    answers.watch = () => Promise.reject(new Error("Socket closed"));
    const error = await Effect.runPromise(Effect.flip(operation(id => client.startRun({ requestId: id }),
      { requestId: "request-4", retry: Schedule.recurs(2) })));
    expect(error._tag).toBe("ViewOperationUncertain");
    expect(starts()).toHaveLength(3);
    expect(cancels()).toEqual(["request-4"]);
  });

  test("`while` stops retrying", async () => {
    const client = await connectCounter();
    answers.watch = () => Promise.reject(new Error("Socket closed"));
    await Effect.runPromise(Effect.flip(operation(id => client.startRun({ requestId: id }),
      { requestId: "request-5", retry: Schedule.recurs(5), while: () => false })));
    expect(starts()).toHaveLength(1);
  });

  test("a timeout that races completion reports the real outcome", async () => {
    const client = await connectCounter();
    answers.cancel = () => ({ ok: true, cancellation: "not-running" });
    answers.status = () => ({ ok: true, status: status("succeeded", { result: 9 }) });
    const result = await Effect.runPromise(Effect.gen(function* () {
      const fiber = yield* Effect.forkChild(operation(() => client.startRun(), { timeout: "1 second" }));
      yield* Effect.promise(() => until(() => waits.length === 1));
      yield* TestClock.adjust("1 second");
      return yield* Fiber.join(fiber);
    }).pipe(Effect.provide(TestClock.layer())));
    expect(result).toBe(9);
  });

  test("retries a transient failure with the same explicit request ID", async () => {
    const client = await connectCounter();
    let observations = 0;
    // The first watch fails as if the transport dropped; the second observes the result.
    answers.watch = () => ++observations === 1 ? Promise.reject(new Error("Socket closed")) : { ok: true, status: status("succeeded", { result: 3 }) };
    const result = await Effect.runPromise(operation(id => client.startRun({ requestId: id }),
      { requestId: "request-1", retry: Schedule.recurs(2) }));
    expect(result).toBe(3);
    expect(starts().map(operationOf)).toEqual(["request-1", "request-1"]);
    expect(kinds()).not.toContain("cancel");
  });

  test("does not retry without a schedule or after a terminal failure", async () => {
    const client = await connectCounter();
    answers.watch = () => ({ ok: true, status: status("failed", { error: { kind: "failed", message: "No" } }) });
    const failed = await Effect.runPromise(Effect.flip(operation(id => client.startRun({ requestId: id }),
      { requestId: "request-2", retry: Schedule.recurs(3) })));
    expect(failed._tag).toBe("ViewOperationFailed");
    answers.watch = () => Promise.reject(new Error("Socket closed"));
    const uncertain = await Effect.runPromise(Effect.flip(operation(() => client.startRun())));
    expect(uncertain._tag).toBe("ViewOperationUncertain");
    expect(uncertain).toMatchObject({ status: undefined });
    expect(starts()).toHaveLength(2);
  });

  // VFX-03: an uncertain outcome from a terminal status carries that status, like the other operation errors.
  test("an expired operation is uncertain with its status", async () => {
    const client = await connectCounter();
    answers.watch = () => ({ ok: true, status: status("expired") });
    const uncertain = await Effect.runPromise(Effect.flip(operation(id => client.startRun({ requestId: id }), { requestId: "request-6" })));
    expect(uncertain).toMatchObject({ _tag: "ViewOperationUncertain", requestId: "request-6", status: { kind: "expired", requestId: "request-6" } });
  });
});

// VFX-01: every ViewError tag is "View" + the BridgeError kind in PascalCase.
describe("tag names", () => {
  test("follow the BridgeError kinds", () => {
    const cause = new BridgeError("timeout", "Late");
    expect(fromBridgeError(cause)._tag).toBe("ViewTimeout");
    expect(fromBridgeError(new BridgeError("failed", "Boom"))._tag).toBe("ViewFailed");
  });
});

describe("followViewport", () => {
  function fakeViewport() {
    const listeners = new Set<() => void>();
    let current: CollectionViewport = { start: 0, size: 10, offset: 0, totalSize: 1000 };
    const controller: CollectionViewportController = {
      get current() { return current; },
      subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
      attach() {}, update() {}, dispose() {},
    };
    return { controller, listeners, move(next: Partial<CollectionViewport>) { current = { ...current, ...next }; for (const listener of [...listeners]) listener(); } };
  }

  test("is exported from the root entry (graduated in 0.10)", async () => {
    const root = await import("../dist/index.js");
    expect(typeof root.followViewport).toBe("function");
    expect(typeof root.viewportChanges).toBe("function");
    expectTypeOf(root.viewportChanges).returns.toEqualTypeOf<Stream.Stream<CollectionViewport>>();
  });

  test("requests the latest range and interrupts a stale request", async () => {
    const viewport = fakeViewport();
    const started: ViewportRange[] = [];
    const interrupted: ViewportRange[] = [];
    const fiber = Effect.runFork(followViewport(viewport.controller, range => Effect.sync(() => { started.push(range); }).pipe(
      Effect.andThen(Effect.never), Effect.onInterrupt(() => Effect.sync(() => { interrupted.push(range); })))));
    await until(() => started.length === 1);
    viewport.move({ offset: 40, totalSize: 2000 });
    viewport.move({ start: 5 });
    await until(() => started.length === 2);
    expect(started).toEqual([{ start: 0, size: 10 }, { start: 5, size: 10 }]);
    expect(interrupted).toEqual([{ start: 0, size: 10 }]);
    await Effect.runPromise(Fiber.interrupt(fiber));
    expect(interrupted).toHaveLength(2);
    expect(viewport.listeners.size).toBe(0);
  });

  test("cancels a superseded viewport operation in .NET", async () => {
    const client = await connectCounter();
    const viewport = fakeViewport();
    const fiber = Effect.runFork(followViewport(viewport.controller, () => operation(() => client.startRun())));
    await until(() => waits.length === 1);
    viewport.move({ start: 20 });
    await until(() => waits.length === 2);
    expect(cancels()[0]).toBe(waits[0]!.requestId);
    await Effect.runPromise(Fiber.interrupt(fiber));
  });
});

describe("operation types", () => {
  // Compile-time: a generated stream start{X}() and an operation that declares a
  // failure are both accepted, as a stream operation was before 0.7.
  test("accepts stream operations and operations with a declared failure", () => {
    interface FeedOperation extends BridgeStreamOperation<number> {}
    interface SaveOperation extends BridgeOperation<void, { readonly $case: "titleRequired" }> {}
    interface Client { startFeed(): Promise<FeedOperation>; startSave(): Promise<SaveOperation> }
    const programs = (client: Client) => {
      const feed: Effect.Effect<void, unknown> = operation(() => client.startFeed());
      const save: Effect.Effect<void, unknown> = operation(() => client.startSave(), { timeout: "1 second" });
      return [feed, save];
    };
    expect(typeof programs).toBe("function");
  });
});

describe("declared failures", () => {
  type SaveFailure = { readonly $case: "titleRequired" } | { readonly $case: "titleTaken"; readonly existingTitle: string };
  const decodeFailure = (wire: unknown) => wire as SaveFailure;

  test("command fails with ViewDomainFailure and succeeds with the outcome's value", async () => {
    bridge.view("note", {
      contract,
      state: { count: 0, title: "" },
      commands: {
        save: state => {
          if ((state.title as string).length === 0) throw mockFailure({ $case: "titleRequired" });
          return { count: 1 };
        },
      },
    });
    const view = await connectView<CounterState>({ contract, route: "note", members });
    const save = () => view.commandOutcome("save", decodeFailure);
    const failing = command(save);
    expectTypeOf(failing).toEqualTypeOf<Effect.Effect<CounterState, ViewError | ViewDomainFailure<SaveFailure>>>();
    expectTypeOf(command(() => view.command("save"))).toEqualTypeOf<Effect.Effect<CounterState, ViewError>>();
    const failure = await Effect.runPromise(Effect.flip(failing));
    expect(failure).toBeInstanceOf(ViewDomainFailure);
    expect(failure).toMatchObject({ _tag: "ViewDomainFailure", failure: { $case: "titleRequired" }, requestId: undefined });
    await Effect.runPromise(command(() => view.set("title", "Groceries")));
    expect(await Effect.runPromise(command(save))).toMatchObject({ count: 1, title: "Groceries" });
    view.dispose();
  });

  test("operation fails with ViewDomainFailure; a failure that was not delivered is ViewOperationFailed", async () => {
    const client = await connectCounter();
    const view = await connectView<CounterState>({ contract, route: "counter", members, operations: bridgeOperations });
    const start = (requestId: string) => view.start<void, SaveFailure>("run", requestId, undefined, { decode: () => undefined, decodeFailure });
    const declared = operation(start, { requestId: "declared" });
    expectTypeOf(declared).toEqualTypeOf<Effect.Effect<void, ViewOperationError | ViewDomainFailure<SaveFailure>>>();
    expectTypeOf(operation(() => client.startRun())).toEqualTypeOf<Effect.Effect<number, ViewOperationError>>();
    const failing = Effect.runPromise(Effect.flip(declared));
    await until(() => waits.length === 1);
    waits[0]!.resolve(status("domain-failed", { failure: { $case: "titleTaken", existingTitle: "Todo" } }));
    expect(await failing).toMatchObject({ _tag: "ViewDomainFailure", requestId: "declared", failure: { $case: "titleTaken", existingTitle: "Todo" } });

    const dropped = Effect.runPromise(Effect.flip(operation(start, { requestId: "dropped" })));
    await until(() => waits.length === 2);
    waits[1]!.resolve(status("domain-failed", { delivery: { kind: "result-too-large", message: "Too large." } }));
    expect(await dropped).toMatchObject({ _tag: "ViewOperationFailed", message: "Too large." });

    // A declared failure is a terminal outcome: retry never starts the operation again.
    const before = starts().length;
    const retried = Effect.runPromise(Effect.flip(operation(start, { requestId: "retried", retry: Schedule.recurs(3) })));
    await until(() => waits.length === 3);
    waits[2]!.resolve(status("domain-failed", { failure: { $case: "titleRequired" } }));
    expect(await retried).toMatchObject({ _tag: "ViewDomainFailure", requestId: "retried" });
    expect(starts().length - before).toBe(1);
    expect(waits.length).toBe(3);
    expect(cancels()).toEqual([]);
    view.dispose();
  });

  test("catchCase handles every case and passes other errors through", async () => {
    const failWith = (failure: SaveFailure) =>
      Effect.fail(new ViewDomainFailure({ message: "declared", failure, requestId: undefined }));
    expectTypeOf(new ViewDomainFailure({ message: "declared", failure: { $case: "titleRequired" } as SaveFailure, requestId: undefined }))
      .toEqualTypeOf<ViewDomainFailure<SaveFailure>>();
    const handle = (effect: Effect.Effect<number, ViewDomainFailure<SaveFailure> | ViewDisconnected>) => catchCase(effect, {
      titleRequired: () => Effect.succeed("needs a title"),
      titleTaken: taken => Effect.fail(`taken: ${taken.existingTitle}`),
    });
    expectTypeOf(handle(Effect.succeed(1))).toEqualTypeOf<Effect.Effect<number | string, ViewDisconnected | string>>();
    expect(await Effect.runPromise(handle(failWith({ $case: "titleRequired" })))).toBe("needs a title");
    expect(await Effect.runPromise(Effect.flip(handle(failWith({ $case: "titleTaken", existingTitle: "Todo" }))))).toBe("taken: Todo");
    const disconnected = new ViewDisconnected({ message: "gone", route: undefined, cause: new BridgeError("disconnected", "gone") });
    expect(await Effect.runPromise(Effect.flip(handle(Effect.fail(disconnected))))).toBe(disconnected);
    // @ts-expect-error every case needs a handler
    catchCase(failWith({ $case: "titleRequired" }), { titleRequired: () => Effect.succeed(0) });
    // @ts-expect-error a misspelled case is not a handler
    catchCase(failWith({ $case: "titleRequired" }), { titleRequired: () => Effect.succeed(0), titleTaken: () => Effect.succeed(0), titelTaken: () => Effect.succeed(0) });
    // A declared failure without $case (a DTO or enum) is not a case union: catchCase does not apply and the error stays.
    const quota = Effect.fail(new ViewDomainFailure({ message: "declared", failure: { limit: 3 }, requestId: undefined }));
    // @ts-expect-error a DTO failure has no cases
    catchCase(quota, {});
    // Only the case union is removed from the error channel.
    const mixed = Effect.fail(new ViewDomainFailure({ message: "declared", failure: { limit: 3 }, requestId: undefined })) as
      Effect.Effect<number, ViewDomainFailure<{ readonly limit: number }> | ViewDomainFailure<SaveFailure>>;
    expectTypeOf(catchCase(mixed, { titleRequired: () => Effect.succeed(0), titleTaken: () => Effect.succeed(1) }))
      .toEqualTypeOf<Effect.Effect<number, ViewDomainFailure<{ readonly limit: number }>>>();
    expect(await Effect.runPromise(Effect.flip(catchCase(mixed, { titleRequired: () => Effect.succeed(0), titleTaken: () => Effect.succeed(1) }))))
      .toMatchObject({ _tag: "ViewDomainFailure", failure: { limit: 3 } });
  });
});
