// @vitest-environment happy-dom

import { flushSync } from "svelte";
import { describe, expect, expectTypeOf, test } from "vitest";
import { BridgeError, bridgeFailure, bridgeSuccess, type BridgeOperation, type BridgeOperationStatus, type BridgeOutcome } from "@runic-artifex/views";
import { useOperation, type OperationHandle } from "../src/views/index.js";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
const settle = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); flushSync(); };

function work<T, F = never>(outcome: BridgeOutcome<T, F>) {
  const terminal = deferred<BridgeOperationStatus<T, F>>();
  let cancellations = 0;
  let outcomes = 0;
  const operation: BridgeOperation<T, F> = {
    requestId: "svelte-test",
    status: () => { throw new Error("No polling needed"); },
    get completion() { return terminal.promise; },
    wait: () => terminal.promise,
    async cancel() { cancellations++; return { contract: "test", requestId: "svelte-test", kind: "cancellation-requested" }; },
    async outcome() {
      outcomes++;
      const status = await terminal.promise;
      if (status.kind === "cancelled") throw new BridgeError("cancelled", "Cancelled");
      return outcome;
    },
  };
  return { operation, terminal, cancellations: () => cancellations, outcomes: () => outcomes,
    finish: () => terminal.resolve({ contract: "test", requestId: operation.requestId, kind: "succeeded" } as BridgeOperationStatus<T, F>) };
}

describe("useOperation", () => {
  test("infers typed outcomes and publishes admission, Cancel and terminal feedback reactively", async () => {
    type Failure = { readonly $case: "conflict"; readonly target: string };
    const accepted = work<number, Failure>(bridgeFailure({ $case: "conflict", target: "draft" }));
    const admission = deferred<BridgeOperation<number, Failure>>();
    let operation!: OperationHandle<[string], number, Failure>;
    const pending: boolean[] = [];
    const destroy = $effect.root(() => {
      operation = useOperation((name: string) => { expect(name).toBe("save"); return admission.promise; });
      $effect(() => { pending.push(operation.pending); });
    });
    flushSync();
    expectTypeOf(operation.failure).toEqualTypeOf<Failure | undefined>();
    expectTypeOf(operation.run).returns.resolves.toEqualTypeOf<BridgeOutcome<number, Failure> | undefined>();
    const result = operation.run("save");
    flushSync();
    expect(operation.admitting).toBe(true);
    const cancel = operation.cancel();
    expect(operation.cancellationRequested).toBe(true);
    admission.resolve(accepted.operation);
    expect((await cancel)?.kind).toBe("cancellation-requested");
    expect(operation.pending).toBe(true);
    expect(operation.operation).toBe(accepted.operation);
    accepted.finish();
    expect((await result)?.ok).toBe(false);
    flushSync();
    expect(operation.failure).toEqual({ $case: "conflict", target: "draft" });
    expect(operation.error).toBeUndefined();
    expect(operation.status?.kind).toBe("succeeded");
    expect(pending[0]).toBe(false);
    expect(pending).toContain(true);
    expect(pending.at(-1)).toBe(false);
    operation.reset();
    expect(operation.failure).toBeUndefined();
    destroy();
  });

  test("observes generated void controls and waits for application drain before ending pending", async () => {
    const accepted = work(bridgeSuccess(undefined));
    const drain = deferred<void>();
    let cancels = 0;
    let operation!: OperationHandle<[], void>;
    const destroy = $effect.root(() => {
      operation = useOperation(() => accepted.operation, {
        cancel: async captured => { expect(captured).toBe(accepted.operation); cancels++; },
        waitForCompletion: async () => { await drain.promise; },
      });
    });
    flushSync();
    const result = operation.run();
    await settle();
    operation.reset();
    expect(operation.pending).toBe(true);
    await operation.cancel();
    expect(cancels).toBe(1);
    expect(accepted.cancellations()).toBe(0);
    accepted.finish();
    await settle();
    expect(operation.pending).toBe(true);
    drain.resolve();
    await result;
    expect(operation.pending).toBe(false);
    expect(operation.outcome).toBeUndefined();
    destroy();
  });

  test("owner destruction settles pending observation without cancelling accepted work or reporting late errors", async () => {
    const accepted = work(bridgeSuccess(1));
    let operation!: OperationHandle<[], number>;
    const destroy = $effect.root(() => { operation = useOperation(() => accepted.operation); });
    flushSync();
    const result = operation.run();
    await settle();
    destroy();
    expect(await result).toBeUndefined();
    expect(accepted.cancellations()).toBe(0);
    accepted.terminal.reject(new Error("late observation failure"));
    await settle();
    expect(operation.error).toBeUndefined();
    expect(accepted.outcomes()).toBe(0);
  });

  test("disposal suppresses Cancel retained before admission and consumes late Start rejection", async () => {
    const accepted = work(bridgeSuccess(1));
    const admission = deferred<BridgeOperation<number>>();
    let operation!: OperationHandle<[], number>;
    const destroy = $effect.root(() => { operation = useOperation(() => admission.promise); });
    flushSync();
    const result = operation.run();
    const cancellation = operation.cancel();
    destroy();
    expect(await result).toBeUndefined();
    expect(await cancellation).toBeUndefined();
    admission.resolve(accepted.operation);
    await settle();
    expect(accepted.cancellations()).toBe(0);

    const late = deferred<BridgeOperation<number>>();
    let rejected!: OperationHandle<[], number>;
    const stop = $effect.root(() => { rejected = useOperation(() => late.promise); });
    flushSync();
    const failed = rejected.run();
    stop();
    expect(await failed).toBeUndefined();
    late.reject(new Error("late receipt failure"));
    await settle();
  });

  test("a getter may return an absent client, and unexpected Start errors never reject", async () => {
    let operation!: OperationHandle<[], void>;
    const destroy = $effect.root(() => { operation = useOperation(() => undefined); });
    flushSync();
    expect(await operation.run()).toBeUndefined();
    expect(operation.pending).toBe(false);
    expectTypeOf(operation.failure).toEqualTypeOf<undefined>();
    destroy();
    const failure = new Error("unavailable");
    let broken!: OperationHandle<[], void>;
    const stop = $effect.root(() => { broken = useOperation((): BridgeOperation<void> => { throw failure; }); });
    flushSync();
    expect(await broken.run()).toBeUndefined();
    expect(broken.error).toBe(failure);
    stop();
  });

  test("owner destruction before the first effect flush settles delayed admission and Cancel", async () => {
    const accepted = work(bridgeSuccess(1));
    const admission = deferred<BridgeOperation<number>>();
    let operation!: OperationHandle<[], number>;
    const destroy = $effect.root(() => { operation = useOperation(() => admission.promise); });
    const result = operation.run();
    const cancellation = operation.cancel();
    destroy();
    expect(await result).toBeUndefined();
    expect(await cancellation).toBeUndefined();
    admission.resolve(accepted.operation);
    await settle();
    expect(accepted.cancellations()).toBe(0);
    expect(accepted.outcomes()).toBe(0);
  });
});
