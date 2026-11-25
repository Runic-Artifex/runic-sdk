import "@angular/compiler";
import { createEnvironmentInjector, EnvironmentInjector, signal, type Signal } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, expectTypeOf, test } from "vitest";
import { injectCommands, type CommandHandle } from "@runic-artifex/angular";
import type { BridgeOutcome } from "@runic-artifex/views";
import { ordersClient, type ArchiveFailure, type OrdersClient, type OrdersState } from "./commands.js";

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => TestBed.resetTestingModule());

function setup(client?: OrdersClient) {
  const current = signal<OrdersClient | undefined>(client);
  const injector = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
  const commands = injectCommands({ client: current }, { injector });
  return { client: current, injector, commands };
}

describe("injectCommands (#1317)", () => {
  test("offers the generated client's commands, typed from it", () => {
    const { commands } = setup();
    expectTypeOf(commands.refresh).toEqualTypeOf<CommandHandle<[], OrdersState>>();
    expectTypeOf(commands.archive).toEqualTypeOf<CommandHandle<[id: bigint], BridgeOutcome<OrdersState, ArchiveFailure>>>();
    expectTypeOf(commands.archive.failure).toEqualTypeOf<Signal<ArchiveFailure | undefined>>();
    expectTypeOf(commands.refresh.failure).toEqualTypeOf<Signal<undefined>>();
    // @ts-expect-error A setter is not a command.
    void commands.setTitle;
    // @ts-expect-error The availability of a command with an argument is not a command.
    void commands.canArchive;
    // @ts-expect-error dispose is not a command.
    void commands.dispose;
    // @ts-expect-error The argument is the command's.
    void commands.archive.run("1");
  });

  test("makes a handle on first access and keeps it", () => {
    const { commands } = setup();
    const refresh = commands.refresh;
    expect(commands.refresh).toBe(refresh);
    expect(commands.archive).not.toBe(refresh);
    expect(refresh.pending()).toBe(false);
  });

  test("run() before the client connected resolves to undefined and sets no error", async () => {
    const { client, commands } = setup();
    expect(await commands.refresh.run()).toBeUndefined();
    expect(commands.refresh.pending()).toBe(false);
    expect(commands.refresh.error()).toBeUndefined();
    const orders = ordersClient("a");
    client.set(orders.client);
    const run = commands.refresh.run();
    expect(orders.calls).toEqual(["a.refresh()"]);
    orders.settle();
    expect(await run).toEqual({ title: "a" });
  });

  test("each handle has its own pending and error; a failed run ends in error and never rejects", async () => {
    const orders = ordersClient("a");
    const { commands } = setup(orders.client);
    const run = commands.refresh.run();
    expect(commands.refresh.pending()).toBe(true);
    expect(commands.archive.pending()).toBe(false);
    orders.settle(new Error("offline"));
    expect(await run).toBeUndefined();
    expect(commands.refresh.pending()).toBe(false);
    expect((commands.refresh.error() as Error).message).toBe("offline");
    expect(commands.archive.error()).toBeUndefined();
    commands.refresh.reset();
    expect(commands.refresh.error()).toBeUndefined();
  });

  test("passes the arguments and keeps a declared failure in failure", async () => {
    const orders = ordersClient("a");
    const { commands } = setup(orders.client);
    const outcome = await commands.archive.run(0n);
    expect(outcome?.ok).toBe(false);
    expect(commands.archive.failure()).toEqual({ $case: "missing" });
    expect(commands.archive.error()).toBeUndefined();
    await commands.archive.run(7n);
    expect(commands.archive.failure()).toBeUndefined();
    expect(orders.calls).toEqual(["a.archive(0)", "a.archive(7)"]);
  });

  test("a run calls the client connected when it starts", async () => {
    const first = ordersClient("a");
    const second = ordersClient("b");
    const { client, commands } = setup(first.client);
    const refresh = commands.refresh;
    client.set(second.client);
    const run = refresh.run();
    second.settle();
    await run;
    expect(first.calls).toEqual([]);
    expect(second.calls).toEqual(["b.refresh()"]);
    expect(commands.refresh).toBe(refresh);
  });

  test("destroying the context stops the handles' state; a run in flight still settles", async () => {
    const orders = ordersClient("a");
    const { injector, commands } = setup(orders.client);
    const run = commands.refresh.run();
    injector.destroy();
    orders.settle(new Error("offline"));
    expect(await run).toBeUndefined();
    expect(commands.refresh.error()).toBeUndefined();
  });

  test("requires an injection context or an injector", () => {
    expect(() => injectCommands({ client: signal<OrdersClient | undefined>(undefined) })).toThrow();
  });
});
