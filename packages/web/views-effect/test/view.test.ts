import { BridgeError, waitForBridge, type BridgeOperation, type BridgeStreamOperation, type CollectionViewport,
  type CollectionViewportController, type ViewClient } from "@runic-artifex/views";
import { bridgeOperations, connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";
import { installMockBridge, type MockBridge } from "@runic-artifex/views/mock";
import { Cause, Effect, Exit, Fiber, Schedule, Scope, Stream } from "effect";
import { TestClock } from "effect/testing";
import { afterEach, beforeEach, describe, expect, test } from "vitest";
import {
  command, connect, followViewport, operation, states, ViewCommandFailed, ViewDisconnected, type ViewportRange,
} from "../dist/index.js";

const contract = "Tests.CounterViewModel:fingerprint";
const contractId = `${contract}:counter`;

interface CounterState { readonly count: number; readonly title: string }
interface CounterClient extends ViewClient<CounterState> {
  increment(): Promise<CounterState>;
  setTitle(value: string): Promise<CounterState>;
  startRun(): Promise<BridgeOperation<number>>;
  startRunWithRequestId(requestId: string): Promise<BridgeOperation<number>>;
}

// Shaped like a generated module, so the adapter is tested against the same surface.
async function connectCounter(mount = false): Promise<CounterClient> {
  const view = await connectView<CounterState>({
    contract, route: "counter", mount, operations: bridgeOperations,
    hydrate: (wire: { count: unknown; title: unknown }) => ({ count: bridgeWire.integer(wire.count, 0, 1000), title: bridgeWire.string(wire.title) }),
  });
  const start = (requestId: string) => view.startOperation<number>("Run", requestId, () => requestId, value => bridgeWire.integer(value, 0, 1000));
  return {
    get snapshot() { return view.snapshot; },
    subscribe: view.subscribe,
    dispose: view.dispose,
    increment: () => view.command("counterIncrement"),
    setTitle: value => {
      if (typeof value !== "string") throw new RangeError("A title is required.");
      return view.invoke("counterSetTitle", value);
    },
    startRun: () => start(globalThis.crypto.randomUUID()),
    startRunWithRequestId: start,
  };
}

let bridge: MockBridge;
let waits: { requestId: string; resolve: (json: string) => void; reject: (cause: unknown) => void }[];
const status = (requestId: string, kind: string, extra: object = {}) => JSON.stringify({ contract: contractId, requestId, kind, ...extra });
const names = () => bridge.calls.map(call => call.name);
const cancels = () => bridge.calls.filter(call => call.name === "__runicOperationCancel").map(call => JSON.parse(call.args[0] as string).requestId as string);
const until = async (condition: () => boolean) => {
  for (let attempt = 0; attempt < 200 && !condition(); attempt++) await new Promise(resolve => setTimeout(resolve, 1));
  expect(condition()).toBe(true);
};

beforeEach(() => {
  bridge = installMockBridge();
  waits = [];
  bridge.view("counter", {
    state: { count: 1, title: "One" },
    routes: {
      Increment: state => ({ count: (state.count as number) + 1 }),
      Fail: () => { throw new Error("Boom"); },
      StartRun: () => JSON.stringify({ kind: "accepted" }),
    },
  });
  bridge.route("__runicOperationWait", identity => new Promise<string>((resolve, reject) => {
    waits.push({ requestId: JSON.parse(identity as string).requestId, resolve, reject });
  }));
  bridge.route("__runicOperationCancel", identity => {
    const { requestId } = JSON.parse(identity as string);
    return JSON.stringify({ contract: contractId, requestId, kind: "cancellation-requested" });
  });
});
afterEach(() => { delete (globalThis as { __runicBridge?: unknown }).__runicBridge; });

describe("connect", () => {
  test("closing the scope disposes the client", async () => {
    const client = await Effect.runPromise(Effect.scoped(Effect.gen(function* () {
      const client = yield* connect(() => connectCounter(true));
      expect(yield* command(() => client.increment())).toEqual({ count: 2, title: "One" });
      expect(names()).not.toContain("counterUnmount");
      return client;
    })));
    expect(names()).toContain("counterUnmount");
    const exit = await Effect.runPromiseExit(command(() => client.increment()));
    expect(exit).toStrictEqual(Exit.fail(expect.any(ViewDisconnected)));
  });

  test("accepts a page reference and maps a failed connection", async () => {
    const reference = { kind: "counter", connect: () => connectCounter() };
    const scope = Scope.makeUnsafe();
    const client = await Effect.runPromise(connect(reference).pipe(Scope.provide(scope)));
    expect(client.snapshot.count).toBe(1);
    await Effect.runPromise(Scope.close(scope, Exit.void));
    bridge.view("broken", { state: {}, routes: { Snapshot: () => { throw Object.assign(new Error("Not now"), { kind: "rejected" }); } } });
    const rejected = await Effect.runPromise(Effect.flip(Effect.scoped(connect(() =>
      connectView({ contract, route: "broken", mount: false, hydrate: (wire: object) => wire })))));
    expect(rejected).toMatchObject({ _tag: "ViewRejected", message: "Not now", route: "brokenSnapshot" });
    delete (globalThis as { __runicBridge?: unknown }).__runicBridge;
    const unavailable = await Effect.runPromise(Effect.flip(command(() => waitForBridge({ timeout: 0 }))));
    expect(unavailable._tag).toBe("ViewUnavailable");
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
    const view = await connectView<CounterState>({ contract, route: "counter", mount: false, hydrate: (wire: CounterState) => wire });
    const failed = await Effect.runPromise(Effect.flip(command(() => view.command("counterFail"))));
    expect(failed).toBeInstanceOf(ViewCommandFailed);
    expect(failed).toMatchObject({ _tag: "ViewCommandFailed", message: "Boom", route: "counterFail", detail: { message: "Boom" } });
    const defect = await Effect.runPromiseExit(command(() => client.setTitle(undefined as unknown as string)));
    expect(Exit.isFailure(defect) && Cause.squash(defect.cause)).toBeInstanceOf(RangeError);
  });
});

describe("states", () => {
  test("streams the current and pushed states and survives a silent resync", async () => {
    const client = await connectCounter();
    const seen: CounterState[] = [];
    const fiber = Effect.runFork(Stream.runForEach(states(client), state => Effect.sync(() => { seen.push(state); })));
    await until(() => seen.length === 1);
    await bridge.call("counterSetTitle", "Pushed"); // changes .NET state without a push
    const callback = (globalThis as unknown as Record<string, (state: unknown) => void>)["__counterChanged"]!;
    // A collection frame whose base revision does not match makes the runtime re-read the snapshot.
    callback({ __runicDelta: 1, revision: 999, baseRevision: 998, changes: [] });
    await until(() => seen.at(-1)?.title === "Pushed");
    await Effect.runPromise(command(() => client.increment()));
    await until(() => seen.at(-1)?.count === 2);
    expect(fiber.pollUnsafe()).toBeUndefined();
    await Effect.runPromise(Fiber.interrupt(fiber));
    client.dispose();
    expect(seen[0]).toEqual({ count: 1, title: "One" });
  });
});

describe("operation", () => {
  test("succeeds with the result of a succeeded status", async () => {
    const client = await connectCounter();
    const running = Effect.runPromise(operation(() => client.startRun()));
    await until(() => waits.length === 1);
    waits[0]!.resolve(status(waits[0]!.requestId, "succeeded", { result: 7 }));
    expect(await running).toBe(7);
    expect(names()).not.toContain("__runicOperationCancel");
  });

  test("maps failed and cancelled statuses", async () => {
    const client = await connectCounter();
    const failed = Effect.runPromise(Effect.flip(operation(() => client.startRun())));
    await until(() => waits.length === 1);
    waits[0]!.resolve(status(waits[0]!.requestId, "failed", { error: { kind: "failed", message: "Disk full" } }));
    expect(await failed).toMatchObject({ _tag: "ViewOperationFailed", message: "Disk full" });
    const cancelled = Effect.runPromise(Effect.flip(operation(() => client.startRun())));
    await until(() => waits.length === 2);
    waits[1]!.resolve(status(waits[1]!.requestId, "cancelled"));
    expect((await cancelled)._tag).toBe("ViewOperationCancelled");
  });

  test("interruption sends the cancellation request", async () => {
    const client = await connectCounter();
    const fiber = Effect.runFork(operation(() => client.startRun()));
    await until(() => waits.length === 1);
    await Effect.runPromise(Fiber.interrupt(fiber));
    const cancel = bridge.calls.find(call => call.name === "__runicOperationCancel");
    expect(JSON.parse(cancel!.args[0] as string)).toMatchObject({ member: "Run", requestId: waits[0]!.requestId });
  });

  test("Effect.timeout driven by TestClock interrupts and cancels", async () => {
    const client = await connectCounter();
    const exit = await Effect.runPromise(Effect.gen(function* () {
      const fiber = yield* Effect.forkChild(operation(() => client.startRun()).pipe(Effect.timeout("5 seconds")));
      yield* Effect.promise(() => until(() => waits.length === 1));
      yield* TestClock.adjust("4 seconds");
      expect(names()).not.toContain("__runicOperationCancel");
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
    bridge.view("counter", { state: { count: 1, title: "One" }, routes: {
      StartRun: () => new Promise<string>(resolve => { admit = () => resolve(JSON.stringify({ kind: "accepted" })); }),
    } });
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
    bridge.route("__runicOperationWait", () => Promise.reject(new Error("Socket closed")));
    await Effect.runPromise(Effect.gen(function* () {
      const fiber = yield* Effect.forkChild(operation(id => client.startRunWithRequestId(id),
        { requestId: "request-3", retry: Schedule.spaced("1 second") }));
      yield* Effect.promise(() => until(() => names().filter(name => name === "__runicOperationWait").length === 1));
      yield* TestClock.adjust("1 second");
      yield* Effect.promise(() => until(() => names().filter(name => name === "__runicOperationWait").length === 2));
      // Now waiting for the next attempt.
      expect(cancels()).toEqual([]);
      yield* Fiber.interrupt(fiber);
    }).pipe(Effect.provide(TestClock.layer())));
    expect(cancels()).toEqual(["request-3"]);
  });

  test("exhausted retries cancel the last unobserved operation once", async () => {
    const client = await connectCounter();
    bridge.route("__runicOperationWait", () => Promise.reject(new Error("Socket closed")));
    const error = await Effect.runPromise(Effect.flip(operation(id => client.startRunWithRequestId(id),
      { requestId: "request-4", retry: Schedule.recurs(2) })));
    expect(error._tag).toBe("ViewOperationUncertain");
    expect(bridge.calls.filter(call => call.name === "counterStartRun")).toHaveLength(3);
    expect(cancels()).toEqual(["request-4"]);
  });

  test("`while` stops retrying", async () => {
    const client = await connectCounter();
    bridge.route("__runicOperationWait", () => Promise.reject(new Error("Socket closed")));
    await Effect.runPromise(Effect.flip(operation(id => client.startRunWithRequestId(id),
      { requestId: "request-5", retry: Schedule.recurs(5), while: () => false })));
    expect(bridge.calls.filter(call => call.name === "counterStartRun")).toHaveLength(1);
  });

  test("a timeout that races completion reports the real outcome", async () => {
    const client = await connectCounter();
    bridge.route("__runicOperationCancel", identity =>
      JSON.stringify({ contract: contractId, requestId: JSON.parse(identity as string).requestId, kind: "not-running" }));
    bridge.route("__runicOperationStatus", identity => status(JSON.parse(identity as string).requestId, "succeeded", { result: 9 }));
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
    // The first wait fails as if the transport dropped; the second observes the result.
    bridge.route("__runicOperationWait", identity => {
      const { requestId } = JSON.parse(identity as string);
      return ++observations === 1 ? Promise.reject(new Error("Socket closed")) : Promise.resolve(status(requestId, "succeeded", { result: 3 }));
    });
    const result = await Effect.runPromise(operation(id => client.startRunWithRequestId(id),
      { requestId: "request-1", retry: Schedule.recurs(2) }));
    expect(result).toBe(3);
    const starts = bridge.calls.filter(call => call.name === "counterStartRun").map(call => call.args[0]);
    expect(starts).toEqual(["request-1", "request-1"]);
    expect(names()).not.toContain("__runicOperationCancel");
  });

  test("does not retry without a schedule or after a terminal failure", async () => {
    const client = await connectCounter();
    bridge.route("__runicOperationWait", identity =>
      Promise.resolve(status(JSON.parse(identity as string).requestId, "failed", { error: { kind: "failed", message: "No" } })));
    const failed = await Effect.runPromise(Effect.flip(operation(id => client.startRunWithRequestId(id),
      { requestId: "request-2", retry: Schedule.recurs(3) })));
    expect(failed._tag).toBe("ViewOperationFailed");
    bridge.route("__runicOperationWait", () => Promise.reject(new Error("Socket closed")));
    const uncertain = await Effect.runPromise(Effect.flip(operation(() => client.startRun())));
    expect(uncertain._tag).toBe("ViewOperationUncertain");
    expect(bridge.calls.filter(call => call.name === "counterStartRun")).toHaveLength(2);
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
    const cancel = bridge.calls.find(call => call.name === "__runicOperationCancel");
    expect(JSON.parse(cancel!.args[0] as string).requestId).toBe(waits[0]!.requestId);
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
