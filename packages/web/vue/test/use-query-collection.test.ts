// @vitest-environment happy-dom

import { effectScope, nextTick } from "vue";
import { describe, expect, test } from "vitest";
import type { QueryCollectionState } from "@runic-artifex/views";
import { connectView, decodeQueryCollectionState, defineQueryCollection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { useQueryCollection } from "../dist/index.js";

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
    const scope = effectScope();
    const list = scope.run(() => useQueryCollection(collection, { estimateSize: 20, overscan: 0 }))!;
    const element = document.createElement("div");
    Object.defineProperty(element, "clientHeight", { configurable: true, value: 100 });
    list.attach(element);
    for (let round = 0; round < 50 && !list.viewport.value.items.every(item => item.status === "loaded"); round++) await bridge.flush();
    await nextTick();
    expect(list.viewport.value.items.map(item => item.row?.id)).toEqual([0, 1, 2, 3, 4, 5]);
    expect(list.viewport.value.total).toBe(10_000);
    expect(await list.scrollToKey("7000")).toBe(true);
    expect(element.scrollTop).toBe(140_000);
    await list.setQuery({ sort: [{ field: "id", descending: true }] });
    expect(list.viewport.value.query.sort).toEqual([{ field: "id", descending: true }]);
    scope.stop();
    client.dispose();
  });
});
