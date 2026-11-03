import * as React from "react";
import { createElement, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, test } from "vitest";
import type { QueryCollectionState } from "@runic-artifex/views";
import { connectView, decodeQueryCollectionState, defineQueryCollection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { useQueryCollection, type QueryCollectionHandle } from "../dist/index.js";

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

type Row = { readonly id: number; readonly label: string };
const decode = (wire: unknown) => wire as Row;
let root: Root | undefined;
afterEach(async () => { await act(async () => root?.unmount()); root = undefined; });

describe("useQueryCollection", () => {
  test("loads the rows its container shows, with placeholders, and scrolls to a key", async () => {
    const bridge = installMockBridge(createMockBridge());
    bridge.view("catalog", {
      state: {},
      queries: { rows: { rows: Array.from({ length: 10_000 }, (_, id) => ({ id, label: `row ${id}` })), key: row => String((row as Row).id) } },
    });
    const client = await connectView<{ readonly rows: QueryCollectionState }>({ contract: "catalog:query", route: "catalog", members: { rows: decodeQueryCollectionState } });
    const collection = client.queryCollection("rows", defineQueryCollection(decode));
    let handle: QueryCollectionHandle<Row> | undefined;
    function List(): ReactNode {
      handle = useQueryCollection(collection, { estimateSize: 20, overscan: 0 });
      return createElement("div", { ref: handle.ref },
        handle.items.map(item => createElement("div", { key: item.index, ref: handle!.measure(item.index) }, item.row?.label ?? "…")));
    }
    const host = document.body.appendChild(document.createElement("div"));
    await act(async () => { root = createRoot(host); root.render(createElement(List)); });
    const container = host.firstElementChild as HTMLDivElement;
    Object.defineProperty(container, "clientHeight", { configurable: true, value: 100 });
    await act(async () => { handle!.ref(null); handle!.ref(container); });
    for (let round = 0; round < 50 && !handle!.items.every(item => item.status === "loaded"); round++)
      await act(async () => { await bridge.flush(); });
    expect(handle!.items.map(item => item.row?.id)).toEqual([0, 1, 2, 3, 4, 5]);
    expect(handle!.totalSize).toBe(200_000);
    await act(async () => { expect(await handle!.scrollToKey("5000")).toBe(true); });
    expect(container.scrollTop).toBe(100_000);
    for (let round = 0; round < 50 && !(handle!.items[0]?.index === 5000 && handle!.items.every(item => item.status === "loaded")); round++)
      await act(async () => { await bridge.flush(); });
    expect(handle!.items[0]?.row?.label).toBe("row 5000");
    client.dispose();
  });
});
