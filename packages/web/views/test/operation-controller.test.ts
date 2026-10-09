import assert from "node:assert/strict";
import { test } from "node:test";
import {
  BridgeError, BridgeOperationUncertainError, bridgeFailure, bridgeSuccess,
  createLatestOperationController, createOperationController, onBridgeDiagnostic,
  type BridgeOperation, type BridgeOperationStatus, type BridgeOutcome,
} from "../dist/index.js";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
const settle = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); };

function held<TResult = void, TFailure = never>(requestId: string, outcome: BridgeOutcome<TResult, TFailure> = bridgeSuccess(undefined as TResult)) {
  const terminal = deferred<BridgeOperationStatus<TResult, TFailure>>();
  let cancellations = 0;
  let waits = 0;
  let outcomes = 0;
  const operation: BridgeOperation<TResult, TFailure> = {
    requestId,
    status: () => { throw new Error("Use the public terminal wait, not polling."); },
    get completion() { return terminal.promise; },
    wait: () => { waits++; return terminal.promise; },
    cancel: async () => { cancellations++; return { contract: "test", requestId, kind: "cancellation-requested" }; },
    async outcome() {
      outcomes++;
      const status = await terminal.promise;
      if (status.kind === "cancelled") throw new BridgeError("cancelled", "Cancelled");
      if (status.kind === "failed") throw new BridgeError("failed", status.error.message);
      return outcome;
    },
  };
  return {
    operation, terminal, cancellations: () => cancellations, waits: () => waits, outcomes: () => outcomes,
    finish: () => terminal.resolve({ contract: "test", requestId, kind: "succeeded" } as BridgeOperationStatus<TResult, TFailure>),
  };
}

test("operation feedback spans delayed admission, cancellation response and terminal outcome", async () => {
  const admission = deferred<BridgeOperation<number>>();
  const work = held("save", bridgeSuccess(42));
  const controller = createOperationController((title: string) => { assert.equal(title, "note"); return admission.promise; });
  const result = controller.run("note");
  assert.equal(controller.current.pending, true);
  assert.equal(controller.current.admitting, true);
  const cancellation = controller.cancel();
  assert.equal(controller.current.cancellationRequested, true);
  assert.equal(work.cancellations(), 0);
  admission.resolve(work.operation);
  assert.equal((await cancellation)?.kind, "cancellation-requested");
  assert.equal(work.cancellations(), 1);
  assert.equal(controller.current.pending, true, "cancellation acknowledgement does not prove completion");
  assert.equal(controller.current.admitting, false);
  assert.equal(controller.current.operation, work.operation);
  work.finish();
  assert.deepEqual(await result, bridgeSuccess(42));
  assert.equal(controller.current.status?.kind, "succeeded");
  assert.deepEqual(controller.current.outcome, bridgeSuccess(42));
  assert.equal(controller.current.pending, false);
  assert.equal(work.waits(), 1);
});

test("reset clears feedback without losing accepted work or cancellation identity", async () => {
  const work = held("accepted");
  const controller = createOperationController(() => work.operation);
  const result = controller.run();
  await settle();
  controller.reset();
  assert.equal(controller.current.pending, true);
  assert.equal(controller.current.operation, work.operation);
  await controller.cancel();
  assert.equal(work.cancellations(), 1);
  work.finish();
  assert.equal((await result)?.ok, true);
  assert.equal(controller.current.outcome, undefined, "reset supersedes terminal feedback only");
  assert.equal(controller.current.pending, false);
});

test("cancellation failures remain separate from the operation outcome and use a captured control", async () => {
  const work = held("mutation");
  const failure = new Error("control callback failed");
  const controller = createOperationController(() => work.operation, { cancel: operation => {
    assert.equal(operation, work.operation);
    throw failure;
  } });
  const result = controller.run();
  await settle();
  assert.equal(await controller.cancel(), undefined);
  assert.equal(controller.current.cancelError, failure);
  assert.equal(controller.current.error, undefined);
  assert.equal(controller.current.pending, true);
  assert.equal(work.cancellations(), 0);
  work.finish();
  await result;
  assert.equal(controller.current.outcome?.ok, true);
});

test("an application completion barrier keeps mutation feedback pending after wrapper terminal", async () => {
  const work = held("drain");
  const drain = deferred<void>();
  const controller = createOperationController(() => work.operation, { waitForCompletion: (operation, status) => {
    assert.equal(operation, work.operation);
    assert.equal(status.kind, "succeeded");
    return drain.promise;
  } });
  const result = controller.run();
  work.finish();
  await settle();
  assert.equal(controller.current.pending, true);
  assert.equal(work.outcomes(), 0);
  drain.resolve();
  await result;
  assert.equal(controller.current.pending, false);
});

test("declared failures, unexpected failures and absent clients have distinct feedback", async () => {
  type Failure = { readonly $case: "conflict" };
  const work = held<number, Failure>("conflict", bridgeFailure({ $case: "conflict" }));
  const controller = createOperationController(() => work.operation);
  const result = controller.run();
  work.finish();
  assert.deepEqual(await result, bridgeFailure({ $case: "conflict" }));
  assert.deepEqual(controller.current.failure, { $case: "conflict" });
  assert.equal(controller.current.error, undefined);
  const error = new Error("Start rejected");
  const broken = createOperationController((): BridgeOperation<void> => { throw error; });
  assert.equal(await broken.run(), undefined);
  assert.equal(broken.current.error, error);
  const absent = createOperationController(() => undefined);
  assert.equal(await absent.run(), undefined);
  assert.equal(absent.current.pending, false);
  assert.equal(absent.current.error, undefined);
});

test("overlapping mutation observers publish latest feedback without cancelling earlier accepted work", async () => {
  const first = held("first");
  const second = held("second");
  const controller = createOperationController((which: boolean) => which ? first.operation : second.operation);
  const old = controller.run(true);
  const latest = controller.run(false);
  await settle();
  await controller.cancel();
  assert.equal(first.cancellations(), 0);
  assert.equal(second.cancellations(), 1);
  second.finish();
  await latest;
  assert.equal(controller.current.pending, true);
  first.terminal.reject(new Error("stale observation"));
  await old;
  assert.equal(controller.current.error, undefined);
  assert.equal(controller.current.pending, false);
});

test("dispose settles pending observation and Cancel, consumes late rejection, and never cancels delayed accepted work", async () => {
  const admission = deferred<BridgeOperation<void>>();
  const work = held("late");
  const controller = createOperationController(() => admission.promise);
  const result = controller.run();
  const cancellation = controller.cancel();
  controller.dispose();
  assert.equal(await result, undefined);
  assert.equal(await cancellation, undefined);
  admission.resolve(work.operation);
  await settle();
  assert.equal(work.cancellations(), 0);
  assert.equal(work.waits(), 0);

  const failingAdmission = deferred<BridgeOperation<void>>();
  const failed = createOperationController(() => failingAdmission.promise);
  const observation = failed.run();
  failed.dispose();
  assert.equal(await observation, undefined);
  failingAdmission.reject(new Error("late rejected Start"));
  await settle();

  const accepted = held("never-settles");
  const observer = createOperationController(() => accepted.operation);
  const acceptedResult = observer.run();
  await settle();
  observer.dispose();
  assert.equal(await acceptedResult, undefined);
  assert.equal(accepted.cancellations(), 0);
  accepted.terminal.reject(new Error("detached wait"));
  await settle();
  assert.equal(accepted.outcomes(), 0);
});

test("observer exceptions are diagnosed without breaking other observers or admission", async () => {
  const work = held("listeners");
  const controller = createOperationController(() => work.operation);
  const failure = new Error("observer");
  const errors: unknown[] = [];
  const stop = onBridgeDiagnostic(diagnostic => errors.push(diagnostic.error));
  let changes = 0;
  controller.subscribe(() => { throw failure; });
  controller.subscribe(() => { changes++; });
  const result = controller.run();
  work.finish();
  await result;
  assert.ok(changes >= 3);
  assert.ok(errors.includes(failure));
  stop();
});

test("latest intent coalesces input before the Start receipt and waits beyond cancellation response", async () => {
  const controller = createLatestOperationController();
  const admission = deferred<BridgeOperation<void>>();
  const oldWork = held("old");
  const newest = held("newest");
  const started: string[] = [];
  const old = controller.run({ start: () => admission.promise });
  const middle = controller.run({ start: () => { started.push("middle"); return newest.operation; } });
  const latest = controller.run({ start: () => { started.push("latest"); return newest.operation; } });
  assert.equal(await middle, undefined);
  admission.resolve(oldWork.operation);
  await settle();
  assert.equal(oldWork.cancellations(), 1);
  assert.deepEqual(started, [], "Cancel response is not an admission barrier");
  oldWork.finish();
  assert.equal(await old, undefined);
  await settle();
  assert.deepEqual(started, ["latest"]);
  newest.finish();
  assert.equal((await latest)?.ok, true);
  assert.equal(controller.current.pending, false);
});

test("failed selection cancellation and app drain still order the next Start", async () => {
  const controller = createLatestOperationController();
  const work = held("controlled");
  const next = held("next");
  const drained = deferred<void>();
  let starts = 0;
  const old = controller.run({ start: () => work.operation, cancel: () => { throw new Error("offline Cancel"); }, waitForCompletion: () => drained.promise });
  await settle();
  const latest = controller.run({ start: () => { starts++; return next.operation; } });
  work.finish();
  await settle();
  assert.equal(starts, 0);
  assert.equal(controller.current.pending, true);
  assert.equal((controller.current.cancelError as Error).message, "offline Cancel");
  drained.resolve();
  assert.equal(await old, undefined);
  await settle();
  assert.equal(starts, 1);
  next.finish();
  await latest;
});

test("explicit Cancel clears queued intent and cancels a delayed receipt once", async () => {
  const controller = createLatestOperationController();
  const admission = deferred<BridgeOperation<void>>();
  const work = held("cancel-delayed");
  let starts = 0;
  const old = controller.run({ start: () => admission.promise });
  const queued = controller.run({ start: () => { starts++; return work.operation; } });
  controller.clear();
  assert.equal(await queued, undefined);
  admission.resolve(work.operation);
  await settle();
  assert.equal(work.cancellations(), 1);
  work.finish();
  assert.equal(await old, undefined);
  assert.equal(starts, 0);
});

test("session invalidation skips queued Starts and delayed application-global cancellation", async () => {
  const controller = createLatestOperationController();
  const admission = deferred<BridgeOperation<void>>();
  const work = held("session");
  let current = true;
  let starts = 0;
  let globalCancels = 0;
  const old = controller.run({ start: () => admission.promise, isCurrent: () => current, cancel: () => { globalCancels++; } });
  const queued = controller.run({ start: () => { starts++; return work.operation; }, isCurrent: () => current });
  current = false;
  work.finish();
  admission.resolve(work.operation);
  assert.equal(await old, undefined);
  assert.equal(await queued, undefined);
  assert.equal(globalCancels, 0);
  assert.equal(starts, 0);
  assert.equal(controller.current.outcome, undefined);
});

test("replacement suppresses delayed same-session Cancel and ignores stale observation failures", async () => {
  const controller = createLatestOperationController();
  const admission = deferred<BridgeOperation<void>>();
  const work = held("old-replacement");
  const next = held("replacement");
  let globalCancels = 0;
  const old = controller.run({ start: () => admission.promise, cancel: () => { globalCancels++; } });
  controller.clear("replace");
  const latest = controller.run({ start: () => next.operation });
  admission.resolve(work.operation);
  await settle();
  assert.equal(globalCancels, 0);
  work.finish();
  assert.equal(await old, undefined);
  await settle();
  assert.equal(controller.current.blocked, false);
  next.finish();
  assert.equal((await latest)?.ok, true);

  const departing = held("departing-wait");
  const detached = controller.run({ start: () => departing.operation });
  await settle();
  controller.clear("replace");
  assert.equal(await detached, undefined);
  assert.equal((await controller.run({ start: () => next.operation }))?.ok, true);
  departing.terminal.reject(new Error("departed transport"));
  await settle();
  assert.equal(controller.current.blocked, false);
  assert.equal(controller.current.error, undefined);
});

test("definitive older Start failure admits latest; uncertain Start and terminal observation block admission", async () => {
  const controller = createLatestOperationController();
  const admission = deferred<BridgeOperation<void>>();
  const next = held("recover");
  const old = controller.run({ start: () => admission.promise });
  const latest = controller.run({ start: () => next.operation });
  admission.reject(new BridgeError("rejected", "unavailable"));
  assert.equal(await old, undefined);
  next.finish();
  assert.equal((await latest)?.ok, true);

  for (const kind of ["wait-rejection", "unknown", "uncertain-start", "lost-recovery"] as const) {
    const blocked = createLatestOperationController();
    const work = held(kind);
    const delayed = deferred<BridgeOperation<void>>();
    let starts = 0;
    const active = blocked.run({ start: () => delayed.promise });
    const pending = blocked.run({ start: () => { starts++; return next.operation; } });
    if (kind === "uncertain-start") delayed.reject(new BridgeOperationUncertainError("test", kind, "admission lost"));
    else if (kind === "lost-recovery") delayed.reject(new Error("Start reply and recovery status lost"));
    else {
      delayed.resolve(work.operation);
      if (kind === "unknown") work.terminal.resolve({ contract: "test", requestId: kind, kind: "unknown" });
      else work.terminal.reject(new Error("wait transport lost"));
    }
    await active;
    assert.equal(await pending, undefined);
    assert.equal(blocked.current.blocked, true);
    assert.equal(starts, 0);
    blocked.clear();
    assert.equal(blocked.current.blocked, true, "Cancel cannot establish fresh admission");
    assert.equal(await blocked.run({ start: () => { starts++; return next.operation; } }), undefined);
    blocked.clear("replace");
    assert.equal((await blocked.run({ start: () => next.operation }))?.ok, true);
  }
});

test("latest disposal settles active and queued callers without awaiting or cancelling accepted work", async () => {
  const controller = createLatestOperationController();
  const admission = deferred<BridgeOperation<void>>();
  const work = held("dispose");
  const old = controller.run({ start: () => admission.promise });
  const queued = controller.run({ start: () => work.operation });
  controller.dispose();
  assert.equal(await old, undefined);
  assert.equal(await queued, undefined);
  admission.reject(new Error("detached receipt"));
  await settle();
  assert.equal(work.cancellations(), 0);
  const observed = createLatestOperationController();
  const active = observed.run({ start: () => work.operation });
  await settle();
  observed.dispose();
  assert.equal(await active, undefined);
  assert.equal(work.cancellations(), 0);
  work.terminal.reject(new Error("detached observation"));
  await settle();
});

test("throwing validity and completion callbacks settle callers and leave useful error feedback", async () => {
  const invalid = createLatestOperationController();
  assert.equal(await invalid.run({ start: () => held("unused").operation, isCurrent: () => { throw new Error("validity failed"); } }), undefined);
  assert.equal((invalid.current.error as Error).message, "validity failed");
  const drain = createLatestOperationController();
  const work = held("barrier");
  const result = drain.run({ start: () => work.operation, waitForCompletion: () => { throw new Error("drain failed"); } });
  work.finish();
  await result;
  assert.equal(drain.current.blocked, true);
  assert.equal((drain.current.error as Error).message, "drain failed");
});

test("a cancellation subscriber can replace the session before an old global control is invoked", async () => {
  const controller = createLatestOperationController();
  const oldWork = held("old-global-control");
  const freshWork = held("fresh-session");
  let globalCancels = 0;
  let freshResult: Promise<BridgeOutcome<void, never> | undefined> | undefined;
  controller.subscribe(() => {
    if (!controller.current.cancelling) return;
    controller.clear("replace");
    freshResult = controller.run({ start: () => freshWork.operation });
  });
  const old = controller.run({ start: () => oldWork.operation, cancel: () => { globalCancels++; } });
  await settle();
  controller.clear();
  assert.equal(await old, undefined);
  await settle();
  assert.equal(globalCancels, 0);
  assert.equal(controller.current.operation, freshWork.operation);
  freshWork.finish();
  assert.equal((await freshResult)?.ok, true);
  oldWork.terminal.reject(new Error("detached old observer"));
  await settle();
});

test("cancellation publication can detach observation or invalidate its session without sending Cancel", async () => {
  for (const mode of ["dispose", "invalidate"] as const) {
    const controller = createLatestOperationController();
    const work = held(mode);
    let current = true;
    let cancels = 0;
    controller.subscribe(() => {
      if (!controller.current.cancelling) return;
      if (mode === "dispose") controller.dispose();
      else current = false;
    });
    const result = controller.run({ start: () => work.operation, isCurrent: () => current, cancel: () => { cancels++; } });
    await settle();
    controller.clear();
    assert.equal(cancels, 0);
    work.finish();
    assert.equal(await result, undefined);
  }
  const work = held("basic-dispose");
  const controller = createOperationController(() => work.operation);
  controller.subscribe(() => { if (controller.current.cancelling) controller.dispose(); });
  const result = controller.run();
  await settle();
  assert.equal(await controller.cancel(), undefined);
  assert.equal(await result, undefined);
  assert.equal(work.cancellations(), 0);
  work.finish();
});

test("dispose settles a Cancel observation even after its operation reached terminal", async () => {
  const work = held("terminal-before-cancel-response");
  const cancelResponse = deferred<void>();
  const controller = createOperationController(() => work.operation, { cancel: () => cancelResponse.promise });
  const result = controller.run();
  await settle();
  const cancellation = controller.cancel();
  work.finish();
  await result;
  assert.equal(controller.current.pending, false);
  controller.dispose();
  assert.equal(await cancellation, undefined);
  cancelResponse.reject(new Error("late Cancel observation"));
  await settle();
});

test("disposal during status publication skips application completion and outcome callbacks", async () => {
  const work = held("dispose-at-terminal");
  let barriers = 0;
  const controller = createOperationController(() => work.operation, { waitForCompletion: () => { barriers++; } });
  controller.subscribe(() => { if (controller.current.status) controller.dispose(); });
  const result = controller.run();
  work.finish();
  assert.equal(await result, undefined);
  assert.equal(barriers, 0);
  assert.equal(work.outcomes(), 0);

  const latest = createLatestOperationController();
  const read = held("replace-at-terminal");
  latest.subscribe(() => { if (latest.current.status) latest.clear("replace"); });
  const observation = latest.run({ start: () => read.operation });
  read.finish();
  assert.equal(await observation, undefined);
  assert.equal(read.outcomes(), 0);
});

test("admission publication rechecks session validity immediately before invoking Start", async () => {
  const controller = createLatestOperationController();
  let current = true;
  let starts = 0;
  controller.subscribe(() => { if (controller.current.admitting) current = false; });
  assert.equal(await controller.run({ start: () => { starts++; return held("stale").operation; }, isCurrent: () => current }), undefined);
  assert.equal(starts, 0);
  assert.equal(controller.current.pending, false);
  assert.equal(controller.current.blocked, false);
});

test("reentrant latest input during pending publication cannot be cancelled by the older run", async () => {
  const controller = createLatestOperationController();
  const oldWork = held("never-started");
  const freshWork = held("reentrant-latest");
  let entered = false;
  let starts = 0;
  let latest: Promise<BridgeOutcome<void, never> | undefined> | undefined;
  controller.subscribe(() => {
    if (entered || !controller.current.pending) return;
    entered = true;
    latest = controller.run({ start: () => freshWork.operation });
  });
  const older = controller.run({ start: () => { starts++; return oldWork.operation; } });
  assert.equal(await older, undefined);
  await settle();
  assert.equal(starts, 0);
  assert.equal(freshWork.cancellations(), 0);
  freshWork.finish();
  assert.equal((await latest)?.ok, true);
  assert.equal(controller.current.outcome?.ok, true);
});

test("a pending application-global Cancel cannot land after the next same-session selection starts", async () => {
  const controller = createLatestOperationController();
  const work = held("old-same-session");
  const next = held("next-same-session");
  const cancellation = deferred<void>();
  const events: string[] = [];
  const old = controller.run({ start: () => work.operation, cancel: async () => {
    await cancellation.promise;
    events.push("old global Cancel delivered");
  } });
  await settle();
  const latest = controller.run({ start: () => { events.push("next Start"); return next.operation; } });
  work.finish();
  await settle();
  assert.deepEqual(events, []);
  assert.equal(controller.current.pending, true);
  cancellation.resolve();
  await old;
  await settle();
  assert.deepEqual(events, ["old global Cancel delivered", "next Start"]);
  next.finish();
  assert.equal((await latest)?.ok, true);
});

for (const feedback of ["status", "outcome"] as const) {
  test(`supersession during ${feedback} publication waits for its newly requested global Cancel`, async () => {
    const controller = createLatestOperationController();
    const work = held(`terminal-${feedback}`);
    const next = held(`next-${feedback}`);
    const cancellation = deferred<void>();
    const events: string[] = [];
    let entered = false;
    let latest: Promise<BridgeOutcome<void, never> | undefined> | undefined;
    controller.subscribe(() => {
      if (entered || !controller.current[feedback]) return;
      entered = true;
      latest = controller.run({ start: () => { events.push("next Start"); return next.operation; } });
    });
    const old = controller.run({ start: () => work.operation, cancel: async () => {
      await cancellation.promise;
      events.push("old global Cancel delivered");
    } });
    work.finish();
    await settle();
    assert.deepEqual(events, []);
    cancellation.resolve();
    assert.equal(await old, undefined);
    await settle();
    assert.deepEqual(events, ["old global Cancel delivered", "next Start"]);
    next.finish();
    assert.equal((await latest)?.ok, true);
  });
}
