import assert from "node:assert/strict";
import test from "node:test";

import { createReactMvvmStore } from "../dist/esm/index.js";

function snapshot(revision, amount, submissions = 0) {
  return Object.freeze({
    phase: "open",
    synchronized: true,
    revision: BigInt(revision),
    properties: new Map([[1, amount]]),
    collections: new Map([[4, Object.freeze([submissions])]]),
    commands: new Map([[2, Object.freeze({ canExecute: true, isExecuting: false })]]),
    validation: new Map([[1, Object.freeze([])]]),
  });
}

class FakeProjection {
  snapshot = snapshot(0, 0);
  listeners = new Set();
  setCalls = [];
  executeCalls = [];
  disposeCalls = 0;
  executeResult = Object.freeze({ revision: 2n, value: Object.freeze({ submissions: 1 }) });

  property(member) { return this.snapshot.properties.get(member); }
  collection(member) { return this.snapshot.collections.get(member); }
  command(member) { return this.snapshot.commands.get(member); }
  validation(member) { return this.snapshot.validation.get(member); }

  subscribe(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  setProperty(member, value) {
    this.setCalls.push({ member, value });
    return Promise.resolve(Object.freeze({ request: "set-1", revision: 1n }));
  }

  execute(member, options = {}) {
    this.executeCalls.push({ member, options });
    return Object.freeze({
      request: "execute-1",
      completion: Promise.resolve(this.executeResult),
      cancel: () => Promise.resolve(Object.freeze({ request: "cancel-1", accepted: true })),
    });
  }

  emit(next) {
    this.snapshot = next;
    for (const listener of [...this.listeners]) {
      listener(Object.freeze({ type: "state", snapshot: next }));
    }
  }

  fault() {
    for (const listener of [...this.listeners]) {
      listener(Object.freeze({ type: "protocolError", error: new Error("test") }));
    }
  }

  dispose() { this.disposeCalls += 1; }
}

test("external store publishes one atomic immutable snapshot per accepted state", () => {
  const projection = new FakeProjection();
  const store = createReactMvvmStore(projection);
  const initial = store.getSnapshot();
  const observed = [];
  const unsubscribe = store.subscribe(() => {
    const accepted = store.getSnapshot();
    observed.push({
      snapshot: accepted,
      revision: accepted.revision,
      amount: store.property(1),
      submissions: store.collection(4)[0],
      canExecute: store.command(2).canExecute,
      validation: store.validation(1),
    });
  });

  assert.equal(store.getServerSnapshot(), initial);
  projection.fault();
  assert.equal(store.getSnapshot(), initial);
  assert.equal(observed.length, 0);

  const next = snapshot(1, 7, 1);
  projection.emit(next);
  assert.equal(store.getSnapshot(), next);
  assert.deepEqual(observed, [{
    snapshot: next,
    revision: 1n,
    amount: 7,
    submissions: 1,
    canExecute: true,
    validation: [],
  }]);

  unsubscribe();
  projection.emit(snapshot(2, 8, 2));
  assert.equal(observed.length, 1);
  store.dispose();
});

test("subscriber failures are isolated and unsubscribe is idempotent", () => {
  const projection = new FakeProjection();
  const store = createReactMvvmStore(projection);
  let healthyCalls = 0;
  store.subscribe(() => { throw new Error("isolated"); });
  const unsubscribe = store.subscribe(() => { healthyCalls += 1; });

  projection.emit(snapshot(1, 7));
  assert.equal(healthyCalls, 1);
  unsubscribe();
  unsubscribe();
  projection.emit(snapshot(2, 8));
  assert.equal(healthyCalls, 1);
  store.dispose();
});

test("setProperty and execute preserve projection requests and options", async () => {
  const projection = new FakeProjection();
  const store = createReactMvvmStore(projection);

  assert.deepEqual(await store.setProperty(1, 7), { request: "set-1", revision: 1n });
  const invocation = store.execute(2, { argument: { source: "react" } });
  assert.equal(invocation.request, "execute-1");
  assert.deepEqual(await invocation.completion, {
    revision: 2n,
    value: { submissions: 1 },
  });
  assert.deepEqual(projection.setCalls, [{ member: 1, value: 7 }]);
  assert.deepEqual(projection.executeCalls, [{
    member: 2,
    options: { argument: { source: "react" } },
  }]);
  store.dispose();
});

test("dispose is idempotent and projection ownership is explicit", () => {
  const borrowed = new FakeProjection();
  const borrowedStore = createReactMvvmStore(borrowed);
  borrowedStore.dispose();
  borrowedStore.dispose();
  assert.equal(borrowed.disposeCalls, 0);
  assert.equal(borrowed.listeners.size, 0);
  assert.throws(() => borrowedStore.subscribe(() => {}), /disposed/);
  assert.throws(() => borrowedStore.execute(2), /disposed/);

  const owned = new FakeProjection();
  const ownedStore = createReactMvvmStore(owned, { ownsProjection: true });
  ownedStore.dispose();
  ownedStore.dispose();
  assert.equal(owned.disposeCalls, 1);
  assert.equal(owned.listeners.size, 0);
});

test("G5 React amount-submit-v1 vertical preserves two atomic commits", async () => {
  const projection = new FakeProjection();
  const store = createReactMvvmStore(projection);
  let commits = 0;
  store.subscribe(() => { commits += 1; });

  const property = store.setProperty(1, 7);
  projection.emit(snapshot(1, 7));
  assert.equal((await property).revision, 1n);

  const invocation = store.execute(2);
  projection.emit(snapshot(2, 7, 1));
  const result = await invocation.completion;

  assert.equal(store.property(1), 7);
  assert.equal(result.value.submissions, 1);
  assert.equal(store.getSnapshot().revision, 2n);
  assert.equal(commits, 2);
  store.dispose();
  console.log("G5-VERTICAL: react/amount-submit-v1 amount=7 submissions=1 commits=2");
});
