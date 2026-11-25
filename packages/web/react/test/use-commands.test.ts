import * as React from "react";
import { createElement, memo, StrictMode, useState, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, expectTypeOf, test } from "vitest";
import type { BridgeOutcome } from "@runic-artifex/views";
import { useCommands, type CommandHandle, type CommandHandles } from "../dist/index.js";
import { ordersClient, type ArchiveFailure, type OrdersClient, type OrdersState } from "./commands.js";

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

let root: Root | undefined;
afterEach(async () => {
  await act(async () => { root?.unmount(); });
  root = undefined;
  document.body.innerHTML = "";
});

const text = () => document.querySelector("output")?.textContent;

async function setup(initial?: OrdersClient) {
  let commands!: CommandHandles<OrdersClient>;
  let setClient!: (client: OrdersClient | undefined) => void;
  let renders = 0;
  function Orders(): ReactNode {
    const [client, set] = useState<OrdersClient | undefined>(initial);
    setClient = set;
    commands = useCommands({ client });
    renders++;
    const { refresh } = commands;
    return createElement("output", null, `${refresh.pending}:${refresh.error ? (refresh.error as Error).message : ""}`);
  }
  await act(async () => {
    root = createRoot(document.body.appendChild(document.createElement("div")));
    root.render(createElement(StrictMode, null, createElement(Orders)));
  });
  return {
    commands: () => commands,
    renders: () => renders,
    setClient: (client: OrdersClient | undefined) => act(async () => { setClient(client); }),
  };
}

describe("useCommands (#1317)", () => {
  test("offers the generated client's commands, typed from it", async () => {
    const { commands } = await setup();
    expectTypeOf(commands().refresh).toEqualTypeOf<CommandHandle<[], OrdersState>>();
    expectTypeOf(commands().archive).toEqualTypeOf<CommandHandle<[id: bigint], BridgeOutcome<OrdersState, ArchiveFailure>>>();
    expectTypeOf(commands().archive.failure).toEqualTypeOf<ArchiveFailure | undefined>();
    // @ts-expect-error A setter is not a command.
    void commands().setTitle;
    // @ts-expect-error The availability of a command with an argument is not a command.
    void commands().canArchive;
    // @ts-expect-error The argument is the command's.
    void commands().archive.run("1");
  });

  test("a handle is made on first access and stays the same across renders until its state changes", async () => {
    const orders = ordersClient("a");
    const { commands, renders, setClient } = await setup(orders.client);
    const refresh = commands().refresh;
    const archive = commands().archive;
    expect(commands().refresh).toBe(refresh);
    expect(archive).not.toBe(refresh);
    const before = renders();
    await setClient(ordersClient("b").client);
    expect(renders()).toBeGreaterThan(before);
    expect(commands().refresh).toBe(refresh);
    let run: Promise<unknown> | undefined;
    await act(async () => { run = commands().refresh.run(); });
    expect(text()).toBe("true:");
    expect(commands().refresh).not.toBe(refresh);
    expect(commands().refresh.run).toBe(refresh.run);
    expect(commands().archive).toBe(archive);
    void run;
  });

  test("run before the client connected resolves to undefined and sets no error", async () => {
    const { commands, setClient } = await setup();
    let result: unknown = "unset";
    await act(async () => { result = await commands().refresh.run(); });
    expect(result).toBeUndefined();
    expect(text()).toBe("false:");
    const orders = ordersClient("a");
    await setClient(orders.client);
    await act(async () => { void commands().refresh.run(); });
    expect(orders.calls).toEqual(["a.refresh()"]);
  });

  test("each handle has its own pending and error; a failed run ends in error and never rejects", async () => {
    const orders = ordersClient("a");
    const { commands } = await setup(orders.client);
    let run: Promise<unknown> | undefined;
    await act(async () => { run = commands().refresh.run(); });
    expect(commands().refresh.pending).toBe(true);
    expect(commands().archive.pending).toBe(false);
    await act(async () => { orders.settle(new Error("offline")); });
    expect(await run).toBeUndefined();
    expect(text()).toBe("false:offline");
    expect(commands().archive.error).toBeUndefined();
    const outcome = await act(async () => commands().archive.run(0n));
    expect(outcome?.ok).toBe(false);
    expect(commands().archive.failure).toEqual({ $case: "missing" });
    await act(async () => { commands().refresh.reset(); });
    expect(text()).toBe("false:");
  });

  test("a run calls the latest client", async () => {
    const first = ordersClient("a");
    const second = ordersClient("b");
    const { commands, setClient } = await setup(first.client);
    await setClient(second.client);
    await act(async () => { void commands().archive.run(1n); });
    expect(first.calls).toEqual([]);
    expect(second.calls).toEqual(["b.archive(1)"]);
  });

  test("a memoized child given one handle renders its changes", async () => {
    const orders = ordersClient("a");
    let commands!: CommandHandles<OrdersClient>;
    const Button = memo(({ refresh }: { readonly refresh: CommandHandle<[], OrdersState> }) =>
      createElement("output", null, String(refresh.pending)));
    function Orders(): ReactNode {
      commands = useCommands({ client: orders.client });
      return createElement(Button, { refresh: commands.refresh });
    }
    await act(async () => {
      root = createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(Orders));
    });
    expect(text()).toBe("false");
    await act(async () => { void commands.refresh.run(); });
    expect(text()).toBe("true");
    await act(async () => { orders.settle(); });
    expect(text()).toBe("false");
  });

  test("unmounting stops the handles' state; a run in flight still settles", async () => {
    const orders = ordersClient("a");
    const { commands } = await setup(orders.client);
    let run: Promise<unknown> | undefined;
    await act(async () => { run = commands().refresh.run(); });
    await act(async () => { root?.unmount(); });
    root = undefined;
    orders.settle(new Error("offline"));
    expect(await run).toBeUndefined();
  });
});
