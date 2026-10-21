// @vitest-environment happy-dom

import { flushSync, mount, unmount } from "svelte";
import { describe, expect, expectTypeOf, test } from "vitest";
import { BridgeError, bridgeSuccess, type BridgeOperation, type BridgeOperationStatus, type BridgeOutcome } from "@runic-artifex/views";
import { useLatestOperation, type LatestOperationHandle } from "../src/views/index.js";
import LatestOperationOwner from "./fixtures/LatestOperationOwner.svelte";

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(yes => { resolve = yes; });
  return { promise, resolve };
}
const settle = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); flushSync(); };

function work(requestId: string, result: number) {
  const terminal = deferred<BridgeOperationStatus<number>>();
  let cancellations = 0;
  const operation: BridgeOperation<number> = {
    requestId,
    status: () => terminal.promise,
    get completion() { return terminal.promise; },
    wait: () => terminal.promise,
    async cancel() { cancellations++; return { contract: "test", requestId, kind: "cancellation-requested" }; },
    async outcome() {
      const status = await terminal.promise;
      if (status.kind === "cancelled") throw new BridgeError("cancelled", "Cancelled");
      return bridgeSuccess(result);
    },
  };
  return {
    operation, cancellations: () => cancellations,
    finish: () => terminal.resolve({ contract: "test", requestId, kind: "succeeded", result }),
    cancel: () => terminal.resolve({ contract: "test", requestId, kind: "cancelled" }),
  };
}

describe("useLatestOperation", () => {
  test("admits only the newest selection after the previous read finishes, reactively", async () => {
    let latest!: LatestOperationHandle<number>;
    const pending: boolean[] = [];
    const destroy = $effect.root(() => {
      latest = useLatestOperation<number>();
      $effect(() => { pending.push(latest.pending); });
    });
    flushSync();
    expectTypeOf(latest.blocked).toEqualTypeOf<boolean>();
    expectTypeOf(latest.run).returns.resolves.toEqualTypeOf<BridgeOutcome<number, never> | undefined>();
    expectTypeOf(latest.clear).parameter(0).toEqualTypeOf<"cancel" | "replace" | undefined>();
    const first = work("first", 1);
    const middle = work("middle", 2);
    const newest = work("newest", 3);
    const started: string[] = [];
    const intent = (current: ReturnType<typeof work>) =>
      ({ start: () => { started.push(current.operation.requestId); return current.operation; } });
    const a = latest.run(intent(first));
    await settle();
    const b = latest.run(intent(middle));
    const c = latest.run(intent(newest));
    await settle();
    expect(first.cancellations()).toBe(1);
    expect(await b).toBeUndefined();
    expect(started).toEqual(["first"]);
    first.cancel();
    expect(await a).toBeUndefined();
    await settle();
    expect(started).toEqual(["first", "newest"]);
    newest.finish();
    expect(await c).toEqual(bridgeSuccess(3));
    flushSync();
    expect(latest.outcome).toEqual(bridgeSuccess(3));
    expect(latest.pending).toBe(false);
    expect(pending).toContain(true);
    expect(pending.at(-1)).toBe(false);
    destroy();
  });

  test("blocks after uncertain admission until clear(\"replace\")", async () => {
    let latest!: LatestOperationHandle<number>;
    const destroy = $effect.root(() => { latest = useLatestOperation<number>(); });
    flushSync();
    await latest.run({ start: () => Promise.reject(new Error("lost Start reply")) });
    flushSync();
    expect(latest.blocked).toBe(true);
    const next = work("next", 4);
    let started = 0;
    expect(await latest.run({ start: () => { started++; return next.operation; } })).toBeUndefined();
    expect(started).toBe(0);
    latest.clear("replace");
    flushSync();
    expect(latest.blocked).toBe(false);
    const run = latest.run({ start: () => { started++; return next.operation; } });
    next.finish();
    expect(await run).toEqual(bridgeSuccess(4));
    expect(started).toBe(1);
    destroy();
  });

  test("owner destruction settles observation without cancelling accepted work", async () => {
    const accepted = work("accepted", 1);
    let completions = 0;
    let latest!: LatestOperationHandle<number>;
    const destroy = $effect.root(() => { latest = useLatestOperation<number>(); });
    flushSync();
    const result = latest.run({ start: () => accepted.operation, waitForCompletion: () => { completions++; } });
    await settle();
    expect(latest.pending).toBe(true);
    destroy();
    expect(await result).toBeUndefined();
    accepted.finish();
    await settle();
    expect(accepted.cancellations()).toBe(0);
    expect(completions).toBe(0);
    expect(latest.outcome).toBeUndefined();
  });

  test("a component unmounted before its first effect flush disposes its controller", async () => {
    const accepted = work("unmounted", 1);
    const admission = deferred<BridgeOperation<number>>();
    const component = mount(LatestOperationOwner, { target: document.createElement("div") });
    const latest: LatestOperationHandle<number> = component.latest;
    const result = latest.run({ start: () => admission.promise });
    await unmount(component);
    expect(await result).toBeUndefined();
    admission.resolve(accepted.operation);
    await settle();
    expect(accepted.cancellations()).toBe(0);
    expect(latest.operation).toBeUndefined();
  });
});
