import assert from "node:assert/strict";
import test from "node:test";

import { createSvelteMvvmStore } from "../dist/esm/index.js";

function verticalSnapshot(revision, amount) {
  return Object.freeze({
    phase: "open",
    synchronized: true,
    revision: BigInt(revision),
    properties: new Map([[1, amount]]),
    collections: new Map(),
    commands: new Map([[2, Object.freeze({ canExecute: true, isExecuting: false })]]),
    validation: new Map(),
  });
}

class AmountSubmitProjection {
  snapshot = verticalSnapshot(0, 0);
  listeners = new Set();
  submissions = 0;

  property(member) { return this.snapshot.properties.get(member); }
  collection(member) { return this.snapshot.collections.get(member); }
  command(member) { return this.snapshot.commands.get(member); }
  validation(member) { return this.snapshot.validation.get(member); }
  subscribe(listener) { this.listeners.add(listener); return () => this.listeners.delete(listener); }
  async setProperty(member, value) {
    assert.equal(member, 1);
    this.commit(verticalSnapshot(1, value));
    return { request: "set-amount", revision: 1n };
  }
  execute(member) {
    assert.equal(member, 2);
    this.submissions += 1;
    this.commit(verticalSnapshot(2, this.property(1)));
    return {
      request: "submit",
      completion: Promise.resolve({ revision: 2n, value: { submissions: this.submissions } }),
      cancel: async () => ({ disposition: "completed" }),
    };
  }
  dispose() {}
  commit(next) {
    this.snapshot = next;
    for (const listener of [...this.listeners]) listener({ type: "state", snapshot: next });
  }
}

test("G5 amount-submit-v1 runs through the Svelte readable adapter", async () => {
  const projection = new AmountSubmitProjection();
  const store = createSvelteMvvmStore(projection, { ownsProjection: true });
  const revisions = [];
  const unsubscribe = store.subscribe((value) => revisions.push(value.revision));

  const property = await store.setProperty(1, 7);
  const invocation = store.execute(2);
  const result = await invocation.completion;

  assert.equal(property.revision, 1n);
  assert.equal(store.property(1), 7);
  assert.equal(store.snapshot.revision, 2n);
  assert.deepEqual(result.value, { submissions: 1 });
  assert.deepEqual(revisions, [0n, 1n, 2n]);
  unsubscribe();
  store.dispose();
  console.log("G5-VERTICAL: svelte/amount-submit-v1 amount=7 submissions=1 commits=2");
});
