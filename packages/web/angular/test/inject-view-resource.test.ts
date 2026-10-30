import "@angular/compiler";
import { Injector, signal } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, test } from "vitest";
import { injectView, injectViewResource } from "../dist/esm/index.js";

type State = { readonly count: number };

function fakeClient(initial: number) {
  let current: State = { count: initial };
  const listeners = new Set<(state: State) => void>();
  const client = {
    disposed: 0,
    get snapshot() { return current; },
    subscribe(listener: (state: State) => void) { listener(current); listeners.add(listener); return () => { listeners.delete(listener); }; },
    dispose() { client.disposed++; listeners.clear(); },
    push(count: number) { current = { count }; for (const listener of [...listeners]) listener(current); },
  };
  return client;
}
type FakeClient = ReturnType<typeof fakeClient>;

function fakeReference(start = 1) {
  const clients: FakeClient[] = [];
  let failures = 0;
  const reference = {
    clients,
    connects: 0,
    failNext() { failures++; },
    connect: async () => {
      reference.connects++;
      if (failures > 0) { failures--; throw new Error("offline"); }
      const client = fakeClient(start + clients.length);
      clients.push(client);
      return client;
    },
  };
  return reference;
}

const settle = async () => {
  for (let index = 0; index < 5; index++) { await Promise.resolve(); TestBed.tick(); }
  await new Promise(resolve => setTimeout(resolve, 0));
  TestBed.tick();
};
const wait = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => TestBed.resetTestingModule());

describe("injectViewResource", () => {
  test("loads until the View connected, then resolves with its state and follows it", async () => {
    const reference = fakeReference(3);
    const view = TestBed.runInInjectionContext(() => injectViewResource(reference, { keepAlive: 0 }));
    TestBed.tick();
    expect(view.isLoading()).toBe(true);
    expect(view.hasValue()).toBe(false);
    await settle();
    expect(view.status()).toBe("resolved");
    expect(view.value()).toEqual({ count: 3 });
    expect(view.client()).toBe(reference.clients[0]);
    reference.clients[0]!.push(4);
    TestBed.tick();
    expect(view.value()).toEqual({ count: 4 });
    TestBed.resetTestingModule();
    expect(reference.clients[0]!.disposed).toBe(1);
  });

  test("reports a failed connection as the resource's error and connects again on reload", async () => {
    const reference = fakeReference(5);
    reference.failNext();
    const view = TestBed.runInInjectionContext(() => injectViewResource(reference, { keepAlive: 0 }));
    await settle();
    expect(view.status()).toBe("error");
    expect(view.error()?.message).toBe("offline");
    expect(view.reload()).toBe(true);
    await settle();
    expect(view.status()).toBe("resolved");
    expect(view.value()).toEqual({ count: 5 });
  });

  test("a changed reference releases the previous client, and null is idle", async () => {
    const first = fakeReference(10);
    const second = fakeReference(20);
    const selected = signal<typeof first | null>(first);
    const view = TestBed.runInInjectionContext(() => injectViewResource(selected, { keepAlive: 0 }));
    await settle();
    expect(view.value()).toEqual({ count: 10 });
    expect(first.connects).toBe(1);
    // A new wrapper with the same connect function is the same reference.
    selected.set({ ...first, connect: first.connect });
    await settle();
    expect(first.connects).toBe(1);
    selected.set(second);
    await settle();
    expect(first.clients[0]!.disposed).toBe(1);
    expect(view.value()).toEqual({ count: 20 });
    selected.set(null);
    await settle();
    expect(view.status()).toBe("idle");
    expect(second.clients[0]!.disposed).toBe(1);
  });

  test("resolves at once with a client that another injector let go of within its keepAlive period", async () => {
    const reference = fakeReference(7);
    const parent = TestBed.inject(Injector);
    const firstInjector = Injector.create({ providers: [], parent });
    const first = injectView(reference, { injector: firstInjector, keepAlive: 50 });
    await settle();
    expect(first.state()).toEqual({ count: 7 });
    (firstInjector as unknown as { destroy(): void }).destroy();
    const view = TestBed.runInInjectionContext(() => injectView(reference, { keepAlive: 50 }));
    // Before the effect followed the reference.
    expect(view.pending()).toBe(false);
    expect(view.state()).toEqual({ count: 7 });
    TestBed.tick();
    expect(view.client()).toBe(reference.clients[0]);
    expect(reference.connects).toBe(1);
    TestBed.resetTestingModule();
    await wait(80);
    expect(reference.clients[0]!.disposed).toBe(1);
  });
});
