// @vitest-environment happy-dom

import { createApp, defineComponent, effectScope, h, isReactive, nextTick, reactive, shallowRef } from "vue";
import { describe, expect, expectTypeOf, test } from "vitest";
import type { BridgeOutcome } from "@runic-artifex/views";
import { useCommands, type CommandHandles, type CommandsHandle } from "../dist/index.js";
import { ordersClient, type ArchiveFailure, type OrdersClient, type OrdersState } from "./commands.js";

function setup(initial?: OrdersClient) {
  const client = shallowRef<OrdersClient | undefined>(initial);
  const scope = effectScope();
  const commands = scope.run(() => useCommands({ client }))!;
  return { client, scope, commands };
}

describe("useCommands (#1317)", () => {
  test("offers the generated client's commands, typed from it", () => {
    const { commands, scope } = setup();
    expectTypeOf(commands).toEqualTypeOf<CommandHandles<OrdersClient>>();
    expectTypeOf(commands.refresh).toEqualTypeOf<CommandsHandle<[], OrdersState>>();
    expectTypeOf(commands.archive).toEqualTypeOf<CommandsHandle<[id: bigint], BridgeOutcome<OrdersState, ArchiveFailure>>>();
    expectTypeOf(commands.archive.failure).toEqualTypeOf<ArchiveFailure | undefined>();
    // @ts-expect-error A setter is not a command.
    void commands.setTitle;
    // @ts-expect-error The availability of a command with an argument is not a command.
    void commands.canArchive;
    // @ts-expect-error The argument is the command's.
    void commands.archive.run("1");
    scope.stop();
  });

  test("makes a handle on first access and keeps it", () => {
    const { commands, scope } = setup();
    const refresh = commands.refresh;
    expect(commands.refresh).toBe(refresh);
    expect(commands.archive).not.toBe(refresh);
    // A reactive() that holds the set leaves it raw.
    const holder = reactive({ commands });
    expect(isReactive(holder.commands)).toBe(false);
    expect(holder.commands.refresh).toBe(refresh);
    scope.stop();
  });

  test("run() before the client connected resolves to undefined and sets no error", async () => {
    const { client, commands, scope } = setup();
    expect(await commands.refresh.run()).toBeUndefined();
    expect(commands.refresh.pending).toBe(false);
    expect(commands.refresh.error).toBeUndefined();
    const orders = ordersClient("a");
    client.value = orders.client;
    const run = commands.refresh.run();
    orders.settle();
    expect(await run).toEqual({ title: "a" });
    expect(orders.calls).toEqual(["a.refresh()"]);
    scope.stop();
  });

  test("a template reads each handle's own pending and error; a failed run ends in error and never rejects", async () => {
    const orders = ordersClient("a");
    let commands!: CommandHandles<OrdersClient>;
    // <output>{{ commands.refresh.pending }}:{{ commands.refresh.error?.message }}:{{ commands.archive.pending }}</output>
    const app = createApp(defineComponent({
      setup() {
        commands = useCommands({ client: shallowRef(orders.client) });
        return { commands };
      },
      render() {
        const { refresh, archive } = this.commands;
        return h("output", `${refresh.pending}:${(refresh.error as Error | undefined)?.message ?? ""}:${archive.pending}`);
      },
    }));
    const target = document.createElement("div");
    app.mount(target);
    expect(target.textContent).toBe("false::false");
    const run = commands.refresh.run();
    await nextTick();
    expect(target.textContent).toBe("true::false");
    orders.settle(new Error("offline"));
    expect(await run).toBeUndefined();
    await nextTick();
    expect(target.textContent).toBe("false:offline:false");
    const outcome = await commands.archive.run(0n);
    expect(outcome?.ok).toBe(false);
    expect(commands.archive.failure).toEqual({ $case: "missing" });
    commands.refresh.reset();
    await nextTick();
    expect(target.textContent).toBe("false::false");
    app.unmount();
  });

  test("a run calls the client connected when it starts", async () => {
    const first = ordersClient("a");
    const second = ordersClient("b");
    const { client, commands, scope } = setup(first.client);
    client.value = second.client;
    await commands.archive.run(1n);
    expect(first.calls).toEqual([]);
    expect(second.calls).toEqual(["b.archive(1)"]);
    scope.stop();
  });

  test("stopping the scope stops the handles' state; a run in flight still settles", async () => {
    const orders = ordersClient("a");
    const { commands, scope } = setup(orders.client);
    const run = commands.refresh.run();
    scope.stop();
    orders.settle(new Error("offline"));
    expect(await run).toBeUndefined();
    expect(commands.refresh.error).toBeUndefined();
  });
});
