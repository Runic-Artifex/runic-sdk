import { describe, expect, test, vi } from "vitest";
import {
  createViteApplicationBridgeObserver,
  disposeViteHmrResource,
  preserveViteHmrResource,
} from "../src/vite.js";

describe("Vite bridge integration", () => {
  test("adapts the public DevTools observer and preserves HMR resources until disposed", async () => {
    const observer = createViteApplicationBridgeObserver();
    observer.state({ connection: { state: "connected", transport: "fixture" } });
    observer.trace({ kind: "connection", label: "fixture" });

    const key = `svelte-fixture-${crypto.randomUUID()}`;
    const first = preserveViteHmrResource(key, () => ({ value: 1 }));
    const second = preserveViteHmrResource(key, () => ({ value: 2 }));
    expect(second).toBe(first);

    const dispose = vi.fn();
    await disposeViteHmrResource(key, dispose);
    expect(dispose).toHaveBeenCalledWith(first);
    expect(preserveViteHmrResource(key, () => ({ value: 3 }))).toEqual({ value: 3 });
    await disposeViteHmrResource(key);
  });
});
