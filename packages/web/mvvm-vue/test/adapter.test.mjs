import assert from "node:assert/strict";
import test from "node:test";

import {
  createRenderer,
  defineComponent,
  effectScope,
  h,
} from "vue";
import {
  MvvmCollection,
  MvvmCommandWithArgument,
  MvvmProperty,
} from "@webuitoolkit/mvvm";
import {
  createScopedVueMvvmAdapter,
  createVueMvvmAdapter,
  provideVueMvvm,
  provideVueMvvmAdapter,
  toVueMvvmCollection,
  toVueMvvmCommand,
  toVueMvvmProperty,
  toVueMvvmValidation,
  useVueMvvm,
  useVueMvvmCollection,
  useVueMvvmCommand,
  useVueMvvmProperty,
  useVueMvvmValidation,
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
  setPropertyCalls = [];
  executeCalls = [];
  disposeCalls = 0;

  property(member) { return this.snapshot.properties.get(member); }
  collection(member) { return this.snapshot.collections.get(member); }
  command(member) { return this.snapshot.commands.get(member); }
  validation(member) { return this.snapshot.validation.get(member); }
  subscribe(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }
  async setProperty(member, value) {
    this.setPropertyCalls.push([member, value]);
    return { request: "set", revision: 1n };
  }
  execute(member, options = {}) {
    this.executeCalls.push([member, options]);
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
  emit(event) {
    if (event.type === "state") this.snapshot = event.snapshot;
    for (const listener of [...this.listeners]) listener(event);
  }
}

test("generated handles adapt to typed computed refs without new subscriptions", () => {
  const projection = new FakeProjection();
  const adapter = createVueMvvmAdapter(projection);
  const amount = toVueMvvmProperty(adapter, new MvvmProperty(projection, 1));
  const items = toVueMvvmCollection(adapter, new MvvmCollection(projection, 2));
  const submit = toVueMvvmCommand(adapter, new MvvmCommandWithArgument(projection, 3));
  const validation = toVueMvvmValidation(adapter, new MvvmProperty(projection, 1));
  assert.equal(amount.value, 1);
  assert.deepEqual(items.value, ["a"]);
  assert.deepEqual(submit.value, { canExecute: true, isExecuting: false });
  assert.deepEqual(validation.value, []);
  projection.emit({ type: "state", snapshot: snapshot(1n, 8) });
  assert.equal(amount.value, 8);
  assert.equal(projection.listeners.size, 1);
  projection.emit({
    type: "state",
    snapshot: Object.freeze({
      ...snapshot(2n, 9),
      collections: new Map(),
    }),
  });
  assert.deepEqual(items.value, []);
  adapter.dispose();
});

test("typed composables resolve generated handles from the provided adapter", () => {
  const projection = new FakeProjection();
  const adapter = createVueMvvmAdapter(projection);
  const amountHandle = new MvvmProperty(projection, 1);
  const itemsHandle = new MvvmCollection(projection, 2);
  const submitHandle = new MvvmCommandWithArgument(projection, 3);
  let observed;
  const Child = defineComponent({
    setup() {
      observed = {
        amount: useVueMvvmProperty(amountHandle),
        items: useVueMvvmCollection(itemsHandle),
        submit: useVueMvvmCommand(submitHandle),
        validation: useVueMvvmValidation(amountHandle),
      };
      return () => h("span");
    },
  });
  const Parent = defineComponent({
    setup() {
      provideVueMvvmAdapter(adapter);
      return () => h(Child);
    },
  });
  const renderer = testRenderer();
  const app = renderer.createApp(Parent);
  app.mount({ children: [], parent: null });
  assert.equal(observed.amount.value, 1);
  assert.deepEqual(observed.items.value, ["a"]);
  assert.deepEqual(observed.submit.value, { canExecute: true, isExecuting: false });
  assert.deepEqual(observed.validation.value, []);
  app.unmount();
  adapter.dispose();
});

test("state changes are atomic and member accessors are stable computed refs", () => {
  const projection = new FakeProjection();
  const adapter = createVueMvvmAdapter(projection);
  const amount = adapter.property(1);
  const items = adapter.collection(2);
  const submit = adapter.command(3);
  const errors = adapter.validation(1);
  assert.equal(adapter.property(1), amount);
  assert.equal(amount.value, 1);
  assert.deepEqual(items.value, ["a"]);
  assert.deepEqual(submit.value, { canExecute: true, isExecuting: false });
  assert.deepEqual(errors.value, []);

  const accepted = snapshot(1n, 7);
  let observed;
  adapter.subscribe((event) => {
    if (event.type === "state") {
      observed = {
        sameSnapshot: adapter.state.value === event.snapshot,
        revision: adapter.state.value.revision,
        amount: amount.value,
      };
    }
  });
  projection.emit({ type: "state", snapshot: accepted });

  assert.deepEqual(observed, { sameSnapshot: true, revision: 1n, amount: 7 });
  adapter.dispose();
});

test("subscriber failures are isolated and protocol operations pass through", async () => {
  const projection = new FakeProjection();
  const adapter = createVueMvvmAdapter(projection);
  const events = [];
  adapter.subscribe(() => { throw new Error("view failure"); });
  adapter.subscribe((event) => events.push(event));

  const fault = { type: "protocolError", error: new Error("bad frame") };
  projection.emit(fault);
  assert.deepEqual(events, [fault]);

  assert.deepEqual(await adapter.setProperty(1, 9), { request: "set", revision: 1n });
  const invocation = adapter.execute(3, { argument: 9 });
  assert.equal(invocation.request, "execute");
  assert.deepEqual((await invocation.completion).value, { submissions: 1 });
  assert.deepEqual(projection.setPropertyCalls, [[1, 9]]);
  assert.deepEqual(projection.executeCalls, [[3, { argument: 9 }]]);
  adapter.dispose();
});

test("disposal is idempotent and projection ownership is explicit", () => {
  const sharedProjection = new FakeProjection();
  const shared = createVueMvvmAdapter(sharedProjection);
  shared.dispose();
  shared.dispose();
  assert.equal(sharedProjection.disposeCalls, 0);
  assert.equal(sharedProjection.listeners.size, 0);
  assert.equal(shared.disposed.value, true);
  assert.throws(() => shared.property(1), /disposed/);
  assert.throws(() => shared.execute(3), /disposed/);

  const ownedProjection = new FakeProjection();
  const owned = createVueMvvmAdapter(ownedProjection, { ownsProjection: true });
  owned.dispose();
  owned.dispose();
  assert.equal(ownedProjection.disposeCalls, 1);
});

test("an active effect scope owns and disposes its scoped adapter", () => {
  const projection = new FakeProjection();
  const scope = effectScope();
  const adapter = scope.run(() => createScopedVueMvvmAdapter(projection));
  assert.ok(adapter);
  assert.equal(projection.listeners.size, 1);
  scope.stop();
  assert.equal(adapter.disposed.value, true);
  assert.equal(projection.listeners.size, 0);
  assert.throws(
    () => createScopedVueMvvmAdapter(new FakeProjection()),
    /active Vue effect scope/,
  );
});

test("component provide/use wiring disposes scope-owned adapters on unmount", () => {
  const projection = new FakeProjection();
  let injected;
  const Child = defineComponent({
    setup() {
      injected = useVueMvvm();
      return () => h("span");
    },
  });
  const Parent = defineComponent({
    setup() {
      provideVueMvvm(projection);
      return () => h(Child);
    },
  });
  const renderer = testRenderer();
  const container = { children: [], parent: null };
  const app = renderer.createApp(Parent);
  app.mount(container);
  assert.ok(injected);
  assert.equal(projection.listeners.size, 1);
  app.unmount();
  assert.equal(injected.disposed.value, true);
  assert.equal(projection.listeners.size, 0);
});

test("providing a caller-owned adapter does not transfer ownership", () => {
  const projection = new FakeProjection();
  const adapter = createVueMvvmAdapter(projection);
  const Child = defineComponent({
    setup() {
      assert.equal(useVueMvvm(), adapter);
      return () => h("span");
    },
  });
  const Parent = defineComponent({
    setup() {
      provideVueMvvmAdapter(adapter);
      return () => h(Child);
    },
  });
  const renderer = testRenderer();
  const app = renderer.createApp(Parent);
  app.mount({ children: [], parent: null });
  app.unmount();
  assert.equal(adapter.disposed.value, false);
  adapter.dispose();
});

function testRenderer() {
  return createRenderer({
    patchProp() {},
    insert(child, parent, anchor = null) {
      child.parent = parent;
      const index = anchor === null ? -1 : parent.children.indexOf(anchor);
      if (index < 0) parent.children.push(child);
      else parent.children.splice(index, 0, child);
    },
    remove(child) {
      const parent = child.parent;
      if (parent === null) return;
      const index = parent.children.indexOf(child);
      if (index >= 0) parent.children.splice(index, 1);
      child.parent = null;
    },
    createElement(type) { return { type, children: [], parent: null, text: "" }; },
    createText(text) { return { type: "text", children: [], parent: null, text }; },
    createComment(text) { return { type: "comment", children: [], parent: null, text }; },
    setText(node, text) { node.text = text; },
    setElementText(node, text) { node.text = text; },
    parentNode(node) { return node.parent; },
    nextSibling(node) {
      const parent = node.parent;
      if (parent === null) return null;
      const index = parent.children.indexOf(node);
      return parent.children[index + 1] ?? null;
    },
  });
}
