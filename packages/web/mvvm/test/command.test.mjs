import assert from "node:assert/strict";
import test from "node:test";

import { createMvvmCommandExecution } from "../dist/esm/index.js";

test("command execution exposes stable success and reset transitions", async () => {
  const pending = deferred();
  const execution = createMvvmCommandExecution({
    execute: () => ({
      request: "00000000-0000-4000-8000-000000000801",
      completion: pending.promise,
      cancel: async () => {
        throw new Error("cancel should not run");
      },
    }),
  });
  const transitions = [];
  execution.subscribe(() => transitions.push(execution.snapshot.status));

  const invocation = execution.execute();
  assert.equal(execution.snapshot.status, "running");
  assert.equal(execution.snapshot.canCancel, true);
  pending.resolve({
    request: invocation.request,
    revision: 2n,
    valuePresent: true,
    value: { saved: true },
  });
  await invocation.completion;
  await Promise.resolve();

  assert.equal(execution.snapshot.status, "succeeded");
  assert.deepEqual(execution.snapshot.result?.value, { saved: true });
  assert.equal(execution.snapshot.transition, 2);
  execution.reset();
  assert.equal(execution.snapshot.status, "idle");
  assert.equal(execution.snapshot.transition, 3);
  assert.deepEqual(transitions, ["running", "succeeded", "idle"]);
});

test("command cancellation is idempotent and classifies rejected completion", async () => {
  const pending = deferred();
  let cancelCount = 0;
  const execution = createMvvmCommandExecution({
    execute: (_argument) => ({
      request: "00000000-0000-4000-8000-000000000802",
      completion: pending.promise,
      cancel: async () => {
        cancelCount++;
        return {
          request: "00000000-0000-4000-8000-000000000803",
          targetRequest: "00000000-0000-4000-8000-000000000802",
          revision: 3n,
          accepted: true,
        };
      },
    }),
  });

  const invocation = execution.execute(7);
  const first = execution.cancel();
  const second = execution.cancel();
  assert.equal(first, second);
  assert.equal(execution.snapshot.cancellationRequested, true);
  assert.equal(execution.snapshot.canCancel, false);
  await first;
  pending.reject(new Error("operation canceled"));
  await assert.rejects(invocation.completion, /operation canceled/);
  await Promise.resolve();

  assert.equal(cancelCount, 1);
  assert.equal(execution.snapshot.status, "canceled");
  assert.match(String(execution.snapshot.error), /operation canceled/);
});

test("late older completion cannot replace the current invocation", async () => {
  const first = deferred();
  const second = deferred();
  let invocation = 0;
  const execution = createMvvmCommandExecution({
    execute: () => {
      invocation++;
      const current = invocation === 1 ? first : second;
      return {
        request: `00000000-0000-4000-8000-00000000080${invocation + 3}`,
        completion: current.promise,
        cancel: async () => {
          throw new Error("unused");
        },
      };
    },
  });

  execution.execute();
  execution.execute();
  first.resolve({
    request: "00000000-0000-4000-8000-000000000804",
    revision: 1n,
    valuePresent: true,
    value: "old",
  });
  await Promise.resolve();
  assert.equal(execution.snapshot.status, "running");

  second.resolve({
    request: "00000000-0000-4000-8000-000000000805",
    revision: 2n,
    valuePresent: true,
    value: "current",
  });
  await Promise.resolve();
  assert.equal(execution.snapshot.status, "succeeded");
  assert.equal(execution.snapshot.result?.value, "current");
});

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}
