// Command handles that a View creates on demand (#1317).
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  bridgeFailure, bridgeSuccess, createCommandHandles, type BridgeOutcome, type CommandArguments, type CommandHandleController, type CommandName,
  type CommandResult, type ViewClient, type viewCommands,
} from "../dist/index.js";

type State = { readonly title: string };
// The shape the generator emits for a View with `RefreshCommand` and `ArchiveCommand(long id)`, whose `archive`
// declares a failure, next to a setter and the `canArchive(id)` availability.
interface OrdersClient extends ViewClient<State> {
  readonly [viewCommands]?: "refresh" | "archive";
  setTitle(value: string): Promise<State>;
  refresh(): Promise<State>;
  archive(id: bigint): Promise<BridgeOutcome<State, "missing">>;
  canArchive(id: bigint): Promise<boolean>;
}

type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends (<T>() => T extends B ? 1 : 2) ? true : false;
const assertType = <T extends true>(): T | undefined => undefined;
assertType<Equal<CommandName<OrdersClient>, "refresh" | "archive">>();
assertType<Equal<CommandArguments<OrdersClient, "archive">, [id: bigint]>>();
assertType<Equal<CommandResult<OrdersClient, "archive">, BridgeOutcome<State, "missing">>>();
// A client without the list, such as a hand-written one, offers no commands.
assertType<Equal<CommandName<ViewClient<State> & { refresh(): Promise<State> }>, never>>();

function ordersClient(title: string, calls: string[] = []): OrdersClient {
  const state = { title };
  return {
    snapshot: state,
    subscribe: listener => { listener(state); return () => {}; },
    dispose() {},
    async setTitle(value) { calls.push(`setTitle ${value}`); return state; },
    async refresh() { calls.push(`refresh ${title}`); return state; },
    async archive(id) {
      calls.push(`archive ${id}`);
      return id === 0n ? bridgeFailure("missing" as const) : bridgeSuccess(state);
    },
    async canArchive() { return true; },
  };
}

function handles(client: () => OrdersClient | undefined) {
  let made = 0;
  const set = createCommandHandles({ client, handle: (controller: CommandHandleController, name) => ({ name, controller, made: ++made }) });
  return { set, made: () => made };
}

test("a name's handle is made on first access and kept", () => {
  const { set, made } = handles(() => ordersClient("a"));
  assert.equal(made(), 0);
  const refresh = set.handles["refresh"]!;
  assert.equal(refresh.name, "refresh");
  assert.equal(made(), 1);
  assert.equal(set.handles["refresh"], refresh);
  assert.notEqual(set.handles["archive"], refresh);
  assert.equal(made(), 2);
});

test("a run before the client connected resolves to undefined and sets no error", async () => {
  let client: OrdersClient | undefined;
  const calls: string[] = [];
  const { set } = handles(() => client);
  const { controller } = set.handles["refresh"]!;
  assert.equal(await controller.run(), undefined);
  assert.deepEqual(controller.current, { pending: false, error: undefined, failure: undefined });
  client = ordersClient("a", calls);
  await controller.run();
  assert.deepEqual(calls, ["refresh a"]);
});

test("a run calls the current client with its arguments, and each handle has its own state", async () => {
  let client = ordersClient("a");
  const calls: string[] = [];
  const { set } = handles(() => client);
  const archive = set.handles["archive"]!.controller;
  const refresh = set.handles["refresh"]!.controller;
  const outcome = await archive.run(0n);
  assert.equal((outcome as BridgeOutcome<State, "missing">).ok, false);
  assert.equal(archive.current.failure, "missing");
  assert.equal(refresh.current.failure, undefined);
  client = ordersClient("b", calls);
  const pending = refresh.run();
  assert.equal(refresh.current.pending, true);
  assert.equal(archive.current.pending, false);
  await pending;
  assert.deepEqual(calls, ["refresh b"]);
});

test("a name the client has no method for ends the run in error, and never rejects", async () => {
  const { set } = handles(() => ordersClient("a"));
  const missing = set.handles["missing"]!.controller;
  assert.equal(await missing.run(), undefined);
  assert.ok(missing.current.error instanceof TypeError);
  assert.match(String(missing.current.error), /no command named "missing"/);
});

test("a failed run ends in error and never rejects", async () => {
  const failure = new Error("offline");
  const client = { ...ordersClient("a"), refresh: () => Promise.reject(failure) };
  const { set } = handles(() => client);
  const refresh = set.handles["refresh"]!.controller;
  assert.equal(await refresh.run(), undefined);
  assert.equal(refresh.current.error, failure);
});

test("framework probes and reserved names are not command names", async () => {
  const { set, made } = handles(() => ordersClient("a"));
  const target = set.handles as Record<string | symbol, unknown>;
  for (const name of ["then", "toJSON", "toString", "constructor", "__v_isRef", "$$typeof", "_internal"])
    assert.equal(target[name], undefined, name);
  assert.equal(target[Symbol.iterator], undefined);
  assert.equal(made(), 0);
  // An `await` and a deep proxy's prototype check leave the set alone.
  assert.equal(await Promise.resolve(set.handles), set.handles);
  assert.equal(Object.getPrototypeOf(set.handles), null);
  // A framework marker is kept, such as Vue's markRaw.
  Object.defineProperty(set.handles, "__v_skip", { value: true, configurable: true });
  assert.equal(target["__v_skip"], true);
  assert.throws(() => { "use strict"; (set.handles as Record<string, unknown>)["refresh"] = 1; }, TypeError);
});

test("subscribe reports each handle's change; snapshots make a handle again after its change", async () => {
  const set = createCommandHandles({
    client: () => ordersClient("a"), snapshots: true,
    handle: controller => ({ pending: controller.current.pending, run: controller.run }),
  });
  let changes = 0;
  const stop = set.subscribe(() => changes++);
  const before = set.handles["refresh"]!;
  assert.equal(set.handles["refresh"], before);
  const archive = set.handles["archive"]!;
  const version = set.version;
  const done = before.run();
  assert.ok(changes > 0);
  assert.ok(set.version > version);
  const running = set.handles["refresh"]!;
  assert.notEqual(running, before);
  assert.equal(running.pending, true);
  assert.equal(running.run, before.run);
  // Another command's handle is unchanged.
  assert.equal(set.handles["archive"], archive);
  await done;
  assert.equal(set.handles["refresh"]!.pending, false);
  stop();
  const seen = changes;
  await set.handles["refresh"]!.run();
  assert.equal(changes, seen);
});

test("dispose stops publishing; runs in flight still settle", async () => {
  const { set } = handles(() => ordersClient("a"));
  const refresh = set.handles["refresh"]!.controller;
  let changes = 0;
  set.subscribe(() => changes++);
  set.dispose();
  const result = await refresh.run();
  assert.deepEqual(result, { title: "a" });
  assert.equal(changes, 0);
  assert.equal(refresh.current.pending, false);
  // A handle made after dispose runs but publishes nothing.
  const late = set.handles["archive"]!.controller;
  await late.run(1n);
  assert.equal(late.current.pending, false);
});
