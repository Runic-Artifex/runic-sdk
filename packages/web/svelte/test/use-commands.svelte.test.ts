// @vitest-environment happy-dom

import { flushSync, mount, unmount } from "svelte";
import { describe, expect, expectTypeOf, test } from "vitest";
import type { BridgeOutcome } from "@runic-artifex/views";
import { useCommands, type CommandHandle, type CommandHandles } from "../src/views/index.js";
import CommandsOwner from "./fixtures/CommandsOwner.svelte";
import { ordersClient, type ArchiveFailure, type OrdersClient, type OrdersState } from "./commands.js";

function setup(initial?: OrdersClient) {
  const view = $state<{ client: OrdersClient | undefined }>({ client: initial });
  let commands!: CommandHandles<OrdersClient>;
  const destroy = $effect.root(() => { commands = useCommands(() => view); });
  return { view, commands, destroy };
}

describe("useCommands (#1317)", () => {
  test("offers the generated client's commands, typed from it", () => {
    const { commands, destroy } = setup();
    expectTypeOf(commands.refresh).toEqualTypeOf<CommandHandle<[], OrdersState>>();
    expectTypeOf(commands.archive).toEqualTypeOf<CommandHandle<[id: bigint], BridgeOutcome<OrdersState, ArchiveFailure>>>();
    expectTypeOf(commands.archive.failure).toEqualTypeOf<ArchiveFailure | undefined>();
    // @ts-expect-error A setter is not a command.
    void commands.setTitle;
    // @ts-expect-error The availability of a command with an argument is not a command.
    void commands.canArchive;
    // @ts-expect-error The argument is the command's.
    void commands.archive.run("1");
    destroy();
  });

  test("makes a handle on first access and keeps it", () => {
    const { commands, destroy } = setup();
    const refresh = commands.refresh;
    expect(commands.refresh).toBe(refresh);
    expect(commands.archive).not.toBe(refresh);
    // A $state proxy that holds the set leaves it alone.
    const holder = $state({ commands });
    expect(holder.commands).toBe(commands);
    destroy();
  });

  test("run() before the client connected resolves to undefined and sets no error", async () => {
    const { view, commands, destroy } = setup();
    expect(await commands.refresh.run()).toBeUndefined();
    expect(commands.refresh.pending).toBe(false);
    expect(commands.refresh.error).toBeUndefined();
    const orders = ordersClient("a");
    view.client = orders.client;
    const run = commands.refresh.run();
    orders.settle();
    expect(await run).toEqual({ title: "a" });
    expect(orders.calls).toEqual(["a.refresh()"]);
    destroy();
  });

  test("the markup reads each handle's own pending and error; a failed run ends in error and never rejects", async () => {
    const orders = ordersClient("a");
    const target = document.createElement("div");
    document.body.append(target);
    const owner = mount(CommandsOwner, { target, props: { client: orders.client } });
    flushSync();
    const commands = owner.commands as CommandHandles<OrdersClient>;
    expect(target.textContent).toBe("false::false");
    const run = commands.refresh.run();
    flushSync();
    expect(target.textContent).toBe("true::false");
    orders.settle(new Error("offline"));
    expect(await run).toBeUndefined();
    flushSync();
    expect(target.textContent).toBe("false:offline:false");
    const outcome = await commands.archive.run(0n);
    expect(outcome?.ok).toBe(false);
    expect(commands.archive.failure).toEqual({ $case: "missing" });
    commands.refresh.reset();
    flushSync();
    expect(target.textContent).toBe("false::false");
    void unmount(owner);
    target.remove();
  });

  test("a run calls the client connected when it starts", async () => {
    const first = ordersClient("a");
    const second = ordersClient("b");
    const { view, commands, destroy } = setup(first.client);
    view.client = second.client;
    await commands.archive.run(1n);
    expect(first.calls).toEqual([]);
    expect(second.calls).toEqual(["b.archive(1)"]);
    destroy();
  });

  test("destroying the owner stops the handles' state; a run in flight still settles", async () => {
    const orders = ordersClient("a");
    const { commands, destroy } = setup(orders.client);
    const run = commands.refresh.run();
    destroy();
    orders.settle(new Error("offline"));
    expect(await run).toBeUndefined();
    expect(commands.refresh.error).toBeUndefined();
  });
});
