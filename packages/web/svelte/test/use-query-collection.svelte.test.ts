// @vitest-environment happy-dom

import { flushSync } from "svelte";
import { describe, expect, test } from "vitest";
import type { QueryCollectionState } from "@runic-artifex/views";
import { connectView, decodeQueryCollectionState, defineQueryCollection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { useQueryCollection } from "../src/views/index.js";

type Row = { readonly id: number; readonly label: string };

describe("useQueryCollection", () => {
  test("loads the rows its container shows and scrolls to a key", async () => {
    const bridge = installMockBridge(createMockBridge());
    bridge.view("catalog", {
      state: {},
      queries: { rows: { rows: Array.from({ length: 10_000 }, (_, id) => ({ id, label: `row ${id}` })), key: row => String((row as Row).id) } },
    });
    const client = await connectView<{ readonly rows: QueryCollectionState }>({ contract: "catalog:query", route: "catalog", members: { rows: decodeQueryCollectionState } });
    const collection = client.queryCollection("rows", defineQueryCollection(wire => wire as Row));
    let list!: ReturnType<typeof useQueryCollection<Row>>;
    const destroy = $effect.root(() => { list = useQueryCollection(() => collection, () => ({ estimateSize: 20, overscan: 0 })); });
    flushSync();
    const element = document.createElement("div");
    Object.defineProperty(element, "clientHeight", { configurable: true, value: 100 });
    const detach = list.attach(element);
    for (let round = 0; round < 50 && !list.viewport.items.every(item => item.status === "loaded"); round++) await bridge.flush();
    expect(list.viewport.items.map(item => item.row?.id)).toEqual([0, 1, 2, 3, 4, 5]);
    expect(await list.scrollToKey("9000")).toBe(true);
    expect(element.scrollTop).toBe(180_000);
    if (typeof detach === "function") detach();
    destroy();
    client.dispose();
  });
});
