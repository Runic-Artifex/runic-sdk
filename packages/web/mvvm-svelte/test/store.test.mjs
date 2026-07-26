import assert from "node:assert/strict";
import test from "node:test";

import {
  MvvmCollection,
  MvvmCommandWithArgument,
  MvvmReadonlyProperty,
} from "@webuitoolkit/mvvm";
import {
  createSvelteMvvmStore,
  derivedMvvmCollection,
  derivedMvvmCommand,
  derivedMvvmProperty,
  derivedMvvmValidation,
  disposeSvelteMvvmStoreOnDestroy,
} from "../dist/esm/index.js";

function snapshot(revision, amount = 0) {
  return Object.freeze({
    phase: "open",
    synchronized: true,
    revision: BigInt(revision),
    properties: new Map([[1, amount]]),
    collections: new Map([[3, Object.freeze(["first", "second"])]]),
    commands: new Map([[2, Object.freeze({ canExecute: true, isExecuting: false })]]),
    validation: new Map([[1, Object.freeze(["required"])]]),
  });
}

class FakeProjection {
  snapshot = snapshot(0);
  listeners = new Set();
  subscribeCount = 0;
  unsubscribeCount = 0;
  disposeCount = 0;
  setCalls = [];
  executeCalls = [];

  property(member) { return this.snapshot.properties.get(member); }
  collection(member) { return this.snapshot.collections.get(member); }
  command(member) { return this.snapshot.commands.get(member); }
  validation(member) { return this.snapshot.validation.get(member); }
  subscribe(listener) {
    this.subscribeCount += 1;
    this.listeners.add(listener);
    let active = true;
    return () => {
      if (!active) return;
      active = false;
      this.unsubscribeCount += 1;
      this.listeners.delete(listener);
    };
  }
  async setProperty(member, value) {
    this.setCalls.push([member, value]);
    return { request: "set-1", revision: 1n };
  }
  execute(member, options = {}) {
    this.executeCalls.push([member, options]);
    return {
      request: "execute-1",
      completion: Promise.resolve({ revision: 2n, value: { submissions: 1 } }),
      cancel: async () => ({ disposition: "cancelled" }),
    };
  }
  dispose() { this.disposeCount += 1; }
  state(next) {
    this.snapshot = next;
    for (const listener of [...this.listeners]) {
      listener({ type: "state", snapshot: next });
    }
  }
  fault() {
    for (const listener of [...this.listeners]) {
      listener({ type: "fault", error: new Error("fixture") });
    }
  }
}

test("generated handles produce lazy typed derived readables", () => {
  const projection = new FakeProjection();
  const store = createSvelteMvvmStore(projection);
  const amount = derivedMvvmProperty(store, new MvvmReadonlyProperty(projection, 1));
  const items = derivedMvvmCollection(store, new MvvmCollection(projection, 3));
  const submit = derivedMvvmCommand(store, new MvvmCommandWithArgument(projection, 2));
  const validation = derivedMvvmValidation(store, new MvvmReadonlyProperty(projection, 1));
  const values = { amount: [], items: [], submit: [], validation: [] };
  const unsubscribers = [
    amount.subscribe((value) => values.amount.push(value)),
    items.subscribe((value) => values.items.push(value)),
    submit.subscribe((value) => values.submit.push(value)),
    validation.subscribe((value) => values.validation.push(value)),
  ];
  assert.equal(projection.subscribeCount, 1);
  projection.state(snapshot(1, 9));
  assert.deepEqual(values.amount, [0, 9]);
  assert.deepEqual(values.items.at(-1), ["first", "second"]);
  assert.deepEqual(values.submit.at(-1), { canExecute: true, isExecuting: false });
  assert.deepEqual(values.validation.at(-1), ["required"]);
  for (const unsubscribe of unsubscribers) unsubscribe();
  assert.equal(projection.unsubscribeCount, 1);
});

test("one atomic projection state produces one complete store update", () => {
  const projection = new FakeProjection();
  const store = createSvelteMvvmStore(projection);
  const observations = [];
  const invalidations = [];
  const unsubscribe = store.subscribe(
    (value) => observations.push([value.revision, value.properties.get(1)]),
    () => invalidations.push("invalidated"),
  );

  projection.fault();
  projection.state(snapshot(1, 7));

  assert.deepEqual(observations, [[0n, 0], [1n, 7]]);
  assert.deepEqual(invalidations, ["invalidated"]);
  assert.equal(store.snapshot, projection.snapshot);
  unsubscribe();
});

test("multiple subscribers share one lazy upstream subscription without leaks", () => {
  const projection = new FakeProjection();
  const store = createSvelteMvvmStore(projection);
  assert.equal(projection.subscribeCount, 0);

  const firstValues = [];
  const secondValues = [];
  const { subscribe } = store;
  const first = subscribe((value) => firstValues.push(value.revision));
  const second = store.subscribe((value) => secondValues.push(value.revision));
  assert.equal(projection.subscribeCount, 1);

  projection.state(snapshot(1, 4));
  first();
  assert.equal(projection.unsubscribeCount, 0);
  projection.state(snapshot(2, 5));
  second();
  assert.equal(projection.unsubscribeCount, 1);
  assert.equal(projection.listeners.size, 0);

  projection.state(snapshot(3, 6));
  const thirdValues = [];
  const third = store.subscribe((value) => thirdValues.push(value.revision));
  assert.equal(projection.subscribeCount, 2);
  assert.deepEqual(firstValues, [0n, 1n]);
  assert.deepEqual(secondValues, [0n, 1n, 2n]);
  assert.deepEqual(thirdValues, [3n]);
  third();
  assert.equal(projection.unsubscribeCount, 2);
});

test("member reads and operations pass through only the public projection", async () => {
  const projection = new FakeProjection();
  const store = createSvelteMvvmStore(projection);

  assert.equal(store.property(1), 0);
  assert.deepEqual(store.collection(3), ["first", "second"]);
  assert.deepEqual(store.command(2), { canExecute: true, isExecuting: false });
  assert.deepEqual(store.validation(1), ["required"]);
  assert.deepEqual(await store.setProperty(1, 9), { request: "set-1", revision: 1n });
  const invocation = store.execute(2, { argument: 9 });
  assert.equal(invocation.request, "execute-1");
  assert.deepEqual(await invocation.completion, { revision: 2n, value: { submissions: 1 } });
  assert.deepEqual(projection.setCalls, [[1, 9]]);
  assert.deepEqual(projection.executeCalls, [[2, { argument: 9 }]]);
});

test("disposal is idempotent and projection ownership is explicit", () => {
  const borrowedProjection = new FakeProjection();
  const borrowed = createSvelteMvvmStore(borrowedProjection);
  borrowed.dispose();
  borrowed.dispose();
  assert.equal(borrowedProjection.disposeCount, 0);
  assert.throws(() => borrowed.subscribe(() => undefined), /disposed/);
  assert.throws(() => borrowed.property(1), /disposed/);

  const ownedProjection = new FakeProjection();
  const owned = createSvelteMvvmStore(ownedProjection, { ownsProjection: true });
  owned.subscribe(() => undefined);
  owned.dispose();
  owned.dispose();
  assert.equal(ownedProjection.unsubscribeCount, 1);
  assert.equal(ownedProjection.disposeCount, 1);
  assert.equal(ownedProjection.listeners.size, 0);
});

test("lifecycle helper registers an idempotent component destroy cleanup", () => {
  const projection = new FakeProjection();
  const store = createSvelteMvvmStore(projection, { ownsProjection: true });
  let cleanup;
  disposeSvelteMvvmStoreOnDestroy(store, (registered) => { cleanup = registered; });
  assert.equal(typeof cleanup, "function");
  cleanup();
  cleanup();
  assert.equal(projection.disposeCount, 1);
});
