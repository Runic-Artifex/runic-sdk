import "@angular/compiler";
import { signal } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, test } from "vitest";
import { injectView, type ViewSource } from "../dist/esm/index.js";

type State = { readonly count: number };

// Mirrors a generated client: subscribe delivers the current state first, and
// a disposed client keeps its last snapshot.
function fakeClient(initial: number) {
  let current: State = { count: initial };
  const listeners = new Set<(state: State) => void>();
  const client = {
    disposed: 0,
    get snapshot() { return current; },
    subscribe(listener: (state: State) => void) {
      listener(current);
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    dispose() { client.disposed++; listeners.clear(); },
    push(count: number) { current = { count }; for (const listener of [...listeners]) listener(current); },
    get listeners() { return listeners.size; },
  };
  return client;
}
type FakeClient = ReturnType<typeof fakeClient>;

function fakeReference(start = 1) {
  const clients: FakeClient[] = [];
  let fail = false;
  return {
    clients,
    failNext() { fail = true; },
    async connect() {
      if (fail) { fail = false; throw new Error("offline"); }
      const client = fakeClient(start + clients.length);
      clients.push(client);
      return client;
    },
  };
}

const settle = async () => { await Promise.resolve(); await Promise.resolve(); TestBed.tick(); };

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => TestBed.resetTestingModule());

describe("injectView", () => {
  test("connects a reference into signals and disposes it with its injector", async () => {
    const reference = fakeReference();
    const view = TestBed.runInInjectionContext(() => injectView(reference));
    TestBed.tick();
    expect(view.state()).toBeUndefined();
    await settle();
    expect(view.state()).toEqual({ count: 1 });
    expect(view.client()).toBe(reference.clients[0]);
    reference.clients[0]!.push(2);
    expect(view.state()).toEqual({ count: 2 });
    TestBed.resetTestingModule();
    expect(reference.clients[0]!.disposed).toBe(1);
    expect(reference.clients[0]!.listeners).toBe(0);
  });

  test("follows a signal and reconnects only when the connect function changes", async () => {
    const first = fakeReference(10);
    const second = fakeReference(20);
    const selected = signal<ViewSource<FakeClient>>({ connect: first.connect });
    const view = TestBed.runInInjectionContext(() => injectView(selected));
    TestBed.tick();
    await settle();
    selected.set({ connect: first.connect });
    await settle();
    expect(first.clients).toHaveLength(1);
    selected.set(second);
    TestBed.tick();
    await settle();
    expect(first.clients[0]!.disposed).toBe(1);
    expect(view.state()).toEqual({ count: 20 });
  });

  test("reads and observes a connected client without disposing it", () => {
    const client = fakeClient(3);
    const view = TestBed.runInInjectionContext(() => injectView(client));
    expect(view.state()).toEqual({ count: 3 });
    expect(view.client()).toBe(client);
    TestBed.tick();
    client.push(4);
    expect(view.state()).toEqual({ count: 4 });
    TestBed.resetTestingModule();
    expect(client.disposed).toBe(0);
    expect(client.listeners).toBe(0);
  });

  test("releases a connected client through the release option", async () => {
    const reference = fakeReference();
    const released: FakeClient[] = [];
    TestBed.runInInjectionContext(() => injectView(reference, { release: client => released.push(client) }));
    TestBed.tick();
    await settle();
    TestBed.resetTestingModule();
    expect(released).toEqual(reference.clients);
    expect(reference.clients[0]!.disposed).toBe(0);
  });

  test("reports a failed connection and retries it", async () => {
    const reference = fakeReference(7);
    reference.failNext();
    const view = TestBed.runInInjectionContext(() => injectView(reference));
    TestBed.tick();
    await settle();
    expect((view.error() as Error).message).toBe("offline");
    view.retry();
    TestBed.tick();
    expect(view.error()).toBeUndefined();
    await settle();
    expect(view.state()).toEqual({ count: 7 });
  });

  test("requires an injection context or an injector", () => {
    expect(() => injectView(fakeReference())).toThrow();
  });
});
