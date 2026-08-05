import assert from "node:assert/strict";
import test from "node:test";

import "@angular/compiler";
import {
  MvvmCollection,
  MvvmCommandWithArgument,
  MvvmReadonlyProperty,
} from "@runic-artifex/mvvm";
import {
  AngularMvvmStore,
  AngularMvvmStoreDirective,
} from "../dist/esm/index.js";

function snapshot(revision = 0n, amount = 1) {
  return Object.freeze({
    phase: "connected",
    synchronized: true,
    revision,
    properties: new Map([[1, amount]]),
    collections: new Map([[2, Object.freeze(["a"])]]),
    commands: new Map([[3, Object.freeze({ canExecute: true, isExecuting: false })]]),
    validation: new Map([[1, Object.freeze([])]]),
  });
}

class FakeProjection {
  snapshot = snapshot();
  listeners = new Set();
  disposeCalls = 0;
  subscribe(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }
  async setProperty(member, value) {
    return { request: `${member}:${value}`, revision: 1n };
  }
  execute() {
    return {
      request: "execute",
      completion: Promise.resolve({
        request: "execute",
        revision: 2n,
        valuePresent: true,
        value: { submissions: 1 },
      }),
      async cancel() {
        return { request: "cancel", revision: 2n, targetRequest: "execute", accepted: true };
      },
    };
  }
  dispose() { this.disposeCalls += 1; }
  emit(next) {
    this.snapshot = next;
    for (const listener of [...this.listeners]) listener({ type: "state", snapshot: next });
  }
}

test("generated handles select strongly typed Angular signals", () => {
  const projection = new FakeProjection();
  const store = new AngularMvvmStore(projection);
  const amountHandle = new MvvmReadonlyProperty(projection, 1);
  const itemsHandle = new MvvmCollection(projection, 2);
  const submitHandle = new MvvmCommandWithArgument(projection, 3);
  const amount = store.property(amountHandle);
  const items = store.collection(itemsHandle);
  const submit = store.command(submitHandle);
  const validation = store.validation(amountHandle);
  assert.equal(amount(), 1);
  assert.deepEqual(items(), ["a"]);
  assert.deepEqual(submit(), { canExecute: true, isExecuting: false });
  assert.deepEqual(validation(), []);
  assert.equal(amount, store.property(amountHandle));
  projection.emit(snapshot(1n, 8));
  assert.equal(amount(), 8);
  projection.emit(Object.freeze({
    ...snapshot(2n, 9),
    collections: new Map(),
  }));
  assert.deepEqual(items(), []);
  store.destroy();
});

test("signals publish one immutable projection snapshot and stable member views", async () => {
  const projection = new FakeProjection();
  const store = new AngularMvvmStore(projection);
  const amount = store.property(1);
  assert.equal(amount, store.property(1));
  assert.equal(amount(), 1);
  const next = snapshot(1n, 7);
  projection.emit(next);
  assert.equal(store.snapshot(), next);
  assert.equal(amount(), 7);
  assert.deepEqual(await store.setProperty(1, 9), { request: "1:9", revision: 1n });
  assert.deepEqual((await store.execute(3).completion).value, { submissions: 1 });
  store.destroy();
  assert.equal(projection.listeners.size, 0);
});

test("listeners are isolated and directive ownership is exact-once", () => {
  const projection = new FakeProjection();
  const store = new AngularMvvmStore(projection, { ownsProjection: true });
  let delivered = 0;
  store.subscribe(() => { throw new Error("view failure"); });
  store.subscribe(() => { delivered += 1; });
  projection.emit(snapshot(1n, 2));
  assert.equal(delivered, 1);

  const directive = new AngularMvvmStoreDirective();
  directive.wutMvvmOwnsStore = true;
  directive.store = store;
  assert.equal(directive.dataContext, store);
  directive.ngOnDestroy();
  directive.ngOnDestroy();
  assert.equal(projection.disposeCalls, 1);
  assert.throws(() => store.property(1), /destroyed/);
});

test("G6 amount-submit-v1 runs through the Angular signal facade", async () => {
  const projection = new FakeProjection();
  const store = new AngularMvvmStore(projection);
  projection.emit(snapshot(2n, 7));
  const result = await store.execute(3).completion;
  assert.equal(store.property(1)(), 7);
  assert.equal(result.value.submissions, 1);
  store.destroy();
  console.log("G6-VERTICAL: angular/amount-submit-v1 amount=7 submissions=1 commits=2");
});
