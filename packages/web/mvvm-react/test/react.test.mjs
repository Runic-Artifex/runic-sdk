import assert from "node:assert/strict";
import test from "node:test";

import { createElement, StrictMode } from "react";
import TestRenderer, { act } from "react-test-renderer";

import {
  ReactMvvmProvider,
  createReactMvvmStore,
  useMvvmCollection,
  useMvvmCommand,
  useMvvmProperty,
  useMvvmSnapshot,
  useMvvmValidation,
} from "../dist/esm/index.js";

globalThis.IS_REACT_ACT_ENVIRONMENT = true;

function snapshot(revision, amount) {
  return Object.freeze({
    phase: "open",
    synchronized: true,
    revision: BigInt(revision),
    properties: new Map([[1, amount]]),
    collections: new Map([[3, Object.freeze(["a", "b"])]]),
    commands: new Map([[2, Object.freeze({ canExecute: true, isExecuting: false })]]),
    validation: new Map([[1, Object.freeze(amount < 0 ? ["positive"] : [])]]),
  });
}

class FakeProjection {
  snapshot = snapshot(0, 0);
  listeners = new Set();
  disposeCalls = 0;

  subscribe(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  emit(next) {
    this.snapshot = next;
    for (const listener of [...this.listeners]) {
      listener({ type: "state", snapshot: next });
    }
  }

  setProperty() { return Promise.resolve({ request: "set", revision: 1n }); }
  execute() {
    return {
      request: "execute",
      completion: Promise.resolve({ revision: 1n }),
      cancel: () => Promise.resolve({ accepted: true }),
    };
  }
  property(member) { return this.snapshot.properties.get(member); }
  collection(member) { return this.snapshot.collections.get(member); }
  command(member) { return this.snapshot.commands.get(member); }
  validation(member) { return this.snapshot.validation.get(member); }
  dispose() { this.disposeCalls += 1; }
}

function Consumer({ renders }) {
  const snapshotValue = useMvvmSnapshot();
  const property = useMvvmProperty(1);
  const collection = useMvvmCollection(3);
  const command = useMvvmCommand(2);
  const validation = useMvvmValidation(1);
  renders.push({
    revision: snapshotValue.revision,
    property,
    items: collection?.length,
    canExecute: command?.canExecute,
    errors: validation?.length,
  });
  return createElement("output", null, String(property));
}

test("provider hooks subscribe, render accepted state, and clean up owned lifetimes", async () => {
  const projection = new FakeProjection();
  const store = createReactMvvmStore(projection, { ownsProjection: true });
  const renders = [];
  let root;

  await act(async () => {
    root = TestRenderer.create(
      createElement(
        ReactMvvmProvider,
        { store, ownsStore: true },
        createElement(Consumer, { renders }),
      ),
    );
  });

  assert.deepEqual(renders.at(-1), {
    revision: 0n,
    property: 0,
    items: 2,
    canExecute: true,
    errors: 0,
  });

  await act(async () => projection.emit(snapshot(1, 7)));
  assert.deepEqual(renders.at(-1), {
    revision: 1n,
    property: 7,
    items: 2,
    canExecute: true,
    errors: 0,
  });

  await act(async () => root.unmount());
  assert.equal(projection.listeners.size, 0);
  assert.equal(projection.disposeCalls, 1);
});

test("hooks fail clearly outside a provider", async () => {
  function MissingProvider() {
    useMvvmSnapshot();
    return null;
  }

  await assert.rejects(
    async () => {
      await act(async () => {
        TestRenderer.create(createElement(MissingProvider));
      });
    },
    /ReactMvvmProvider/,
  );
});

test("owned provider distinguishes StrictMode effect replay from final unmount", async () => {
  const projection = new FakeProjection();
  const store = createReactMvvmStore(projection, { ownsProjection: true });
  let root;

  await act(async () => {
    root = TestRenderer.create(
      createElement(
        StrictMode,
        null,
        createElement(
          ReactMvvmProvider,
          { store, ownsStore: true },
          createElement(Consumer, { renders: [] }),
        ),
      ),
    );
  });
  assert.equal(projection.disposeCalls, 0);
  assert.equal(projection.listeners.size, 1);

  await act(async () => root.unmount());
  assert.equal(projection.disposeCalls, 1);
  assert.equal(projection.listeners.size, 0);
});
