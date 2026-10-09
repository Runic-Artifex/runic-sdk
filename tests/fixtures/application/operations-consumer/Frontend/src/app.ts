import { mount, unmount } from "svelte";
import { createLatestOperationController, createOperationController, BridgeError, BridgeOperationUncertainError,
  type BridgeOperation } from "@runic-artifex/views";
import type { OperationHandle } from "@runic-artifex/svelte/views";
import OperationFeedback from "./OperationFeedback.svelte";
import { connectOperations } from "./generated/operations";

const client = await connectOperations();
const feedback = deferred<OperationHandle<[], string>>();
const component = mount(OperationFeedback, { target: document.querySelector("#operation")!,
  props: { client, ready: feedback.resolve } });
const svelteOperation = await feedback.promise;
const output = document.querySelector<HTMLOutputElement>("#journey")!;
const passed: string[] = [];
let running: Promise<string[]> | undefined;

declare global {
  interface Window {
    runJourneys: () => Promise<string[]>;
    operationsReady: boolean;
    operationsPassed: string[];
    operationsError?: string;
  }
}
window.operationsReady = true;
window.operationsPassed = passed;
window.runJourneys = () => running ??= journeys().catch(error => {
  window.operationsError = String(error?.stack ?? error);
  output.value = window.operationsError;
  throw error;
});
output.value = "Ready";

async function journeys(): Promise<string[]> {
  require(client.snapshot.query.message === "history" && client.snapshot.query.hint === null,
    "The external DTO did not cross the generated contract.");
  require(!("isFiltered" in client.snapshot.query) && client.snapshot.rootVisible === "root-visible",
    "JsonIgnore opt-in changed DTO/root contract boundaries.");
  pass("shared-dto");

  // A legacy awaited callback must not hold the transport while another callback
  // writes a draft or requests cancellation.
  const direct = client.long().then(() => undefined, error => error);
  await until(() => client.snapshot.longActive, "awaited long command entered");
  await bounded(client.setDraft("during-direct"), "draft during awaited callback");
  await bounded(client.cancelLong(), "Cancel during awaited callback");
  const directError = await bounded(direct, "awaited command cancellation");
  require(directError instanceof BridgeError && directError.kind === "cancelled",
    "The cancelled direct command did not report cancellation.");
  await until(() => !client.snapshot.isLongExecuting, "long command available again");
  pass("responsive-awaited-command");

  // The shipped Svelte wrapper observes a real public wait callback. The same
  // transport must admit draft and Cancel while wait remains in flight.
  const observed = svelteOperation.run();
  await until(() => client.snapshot.longActive && document.querySelector("#pending")?.textContent === "pending",
    "Svelte pending feedback");
  await bounded(client.setDraft("during-wait"), "draft during operation.wait");
  require(client.snapshot.draft === "during-wait", "The responsive draft callback was not applied.");
  await bounded(svelteOperation.cancel(), "Cancel during operation.wait");
  await bounded(observed, "Svelte cancelled observation");
  await until(() => document.querySelector("#pending")?.textContent === "idle", "Svelte terminal feedback");
  require(svelteOperation.status?.kind === "cancelled" && svelteOperation.error instanceof BridgeError,
    "Svelte feedback lost the terminal cancellation.");
  require(document.querySelector("#cancellation")?.textContent === "requested", "Svelte Cancel feedback did not render.");
  pass("responsive-wait-and-binding");

  const latest = createLatestOperationController<string>();
  let session = 0;
  const completionBarrier = async () => {
    await client.waitReadDrained();
    await until(() => !client.snapshot.isReadExecuting && client.snapshot.canRead, "actual read completion and command availability");
  };
  const intent = (label: string, receipt?: ReturnType<typeof deferred<void>>,
    accepted?: ReturnType<typeof deferred<BridgeOperation<string>>>) => {
    const captured = session;
    return {
      start: async () => {
        const operation = await client.startRead(label);
        accepted?.resolve(operation);
        if (receipt) await receipt.promise;
        return operation;
      },
      cancel: async (operation: BridgeOperation<string>) => {
        await client.cancelRead();
        return operation.cancel();
      },
      waitForCompletion: completionBarrier,
      isCurrent: () => captured === session,
    };
  };
  const receipt = deferred<void>();
  const firstHandle = deferred<BridgeOperation<string>>();
  const first = latest.run(intent("held:first", receipt, firstHandle));
  await until(() => client.snapshot.readActive, "read accepted before Start receipt");
  const middle = latest.run(intent("discarded-middle"));
  const newest = latest.run(intent("newest"));
  require(client.snapshot.readAccepted.join("|") === "held:first", "A selection overtook its delayed receipt.");
  receipt.resolve();
  await until(() => client.snapshot.cancelReadCalls === 1, "deferred read cancellation");
  const firstTerminal = await bounded((await firstHandle.promise).wait(), "cancelled read invocation wrapper");
  require(firstTerminal.kind === "cancelled", "The superseded invocation wrapper did not become terminal.");
  // Wrapper cancellation is terminal, but actual work/command availability still
  // owns admission. A control callback can verify this while the barrier waits.
  await client.setDraft("while-read-drains");
  require(client.snapshot.readAccepted.join("|") === "held:first" && client.snapshot.readActive,
    "The helper admitted another selection before accepted work finished.");
  await client.releaseRead();
  const result = await bounded(newest, "latest selection after accepted read completion");
  await bounded(Promise.all([first, middle]), "superseded observations");
  require(result?.ok && result.value === "newest" && client.snapshot.selected === "newest",
    "The final selection was not published.");
  require(client.snapshot.readAccepted.join("|") === "held:first|newest", "Intermediate selections reached the model.");
  const cancelActive = latest.run(intent("held:explicit-cancel"));
  await until(() => client.snapshot.readActive, "read before explicit Cancel");
  const cancelQueued = latest.run(intent("discarded-by-cancel"));
  latest.clear("cancel");
  await until(() => client.snapshot.cancelReadCalls === 2, "explicit queued selection cancellation");
  await client.releaseRead();
  await bounded(Promise.all([cancelActive, cancelQueued]), "cleared selection observations");
  await until(() => !latest.current.pending, "cleared selection drain");
  require(!client.snapshot.readAccepted.includes("discarded-by-cancel"), "Explicit Cancel retained a queued choice.");
  pass("latest-delayed-receipt-and-drain");

  // Repository replacement establishes fresh model admission before detaching
  // old observation. A late old receipt must never invoke global CancelRead on
  // the replacement operation.
  const oldReceipt = deferred<void>();
  const old = latest.run(intent("held:old-session", oldReceipt));
  await until(() => client.snapshot.readActive, "old session read");
  const dropped = latest.run(intent("discarded-old-session"));
  latest.clear("replace");
  session++;
  const cancelsBeforeReplacement = client.snapshot.cancelReadCalls;
  await client.replaceSession();
  await completionBarrier();
  const replacement = latest.run(intent("held:replacement"));
  await until(() => client.snapshot.readAccepted.includes("held:replacement"), "replacement session read");
  oldReceipt.resolve();
  await bounded(Promise.all([old, dropped]), "detached old session observations");
  await client.setDraft("new-session");
  require(client.snapshot.cancelReadCalls === cancelsBeforeReplacement && client.snapshot.readActive,
    "An old receipt cancelled the fresh repository session.");
  await client.releaseRead();
  const replaced = await bounded(replacement, "replacement selection");
  require(replaced?.ok && replaced.value === "held:replacement" && client.snapshot.selected === "held:replacement",
    "Replacement session did not own selection publication.");
  require(!client.snapshot.readAccepted.includes("discarded-old-session"), "A replaced session retained its old queue.");
  pass("latest-session-replacement");

  // The real generated recovery API cannot establish admission for an unknown
  // request. The queue must stop until the application explicitly replaces it.
  await latest.run({ start: () => client.recoverReadWithRequestId("never-accepted-package-consumer") });
  require(latest.current.blocked && latest.current.error instanceof BridgeOperationUncertainError,
    "Unknown admission did not block the latest-selection policy.");
  const acceptedBeforeBlocked = client.snapshot.readAccepted.length;
  await latest.run(intent("blocked"));
  await client.setDraft("uncertain-boundary");
  require(client.snapshot.readAccepted.length === acceptedBeforeBlocked, "Uncertain admission allowed another read.");
  latest.clear("replace");
  const afterReplacement = await latest.run(intent("after-explicit-replacement"));
  require(afterReplacement?.ok && afterReplacement.value === "after-explicit-replacement", "Explicit replacement did not reset uncertainty.");
  latest.dispose();
  const successfulOperation = createOperationController((label: string) => client.startRead(label), {
    waitForCompletion: completionBarrier,
  });
  const successful = await successfulOperation.run("generic-success");
  require(successful?.ok && successful.value === "generic-success" && successfulOperation.current.outcome === successful
    && !successfulOperation.current.pending, "The packed operation controller lost its successful outcome.");
  successfulOperation.dispose();
  pass("uncertain-admission");

  // Destroying the Svelte owner releases observation only. Accepted work must
  // remain running until the application separately asks for cancellation.
  const detached = svelteOperation.run();
  await until(() => client.snapshot.longActive, "operation before component destruction");
  await unmount(component);
  await bounded(detached, "disposed Svelte observation");
  require(client.snapshot.longActive, "Svelte owner destruction cancelled accepted work.");
  await client.cancelLong();
  await until(() => !client.snapshot.isLongExecuting, "detached operation explicitly cancelled");
  pass("binding-disposal");

  // Cancel the generated wrapper while application recovery is deliberately
  // retained. Program.cs closes the content session then asynchronously disposes
  // the scoped model, checking the accepted-work drain before releasing it.
  const mutation = createOperationController(() => client.startMutation());
  const mutationObservation = mutation.run();
  await until(() => client.snapshot.mutationActive, "accepted mutation");
  await mutation.cancel();
  await bounded(mutationObservation, "cancelled invocation wrapper");
  await until(() => client.snapshot.recoveryStarted, "accepted recovery started");
  require(mutation.current.status?.kind === "cancelled" && client.snapshot.mutationActive && !client.snapshot.recoveryPublished,
    "The consumer did not retain accepted recovery after wrapper cancellation.");
  mutation.dispose();
  pass("cancelled-wrapper-ready-for-close");
  output.value = "OPERATIONS_BROWSER_OK";
  return passed;
}

function pass(name: string) { passed.push(name); output.value = passed.join(" | "); }
function require(condition: unknown, message: string): asserts condition { if (!condition) throw new Error(message); }
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}
async function bounded<T>(promise: PromiseLike<T>, label: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout>;
  const timeout = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error(`Timed out: ${label}`)), 15_000); });
  try { return await Promise.race([promise, timeout]); }
  finally { clearTimeout(timer!); }
}
async function until(condition: () => boolean, label: string): Promise<void> {
  const deadline = Date.now() + 15_000;
  while (!condition()) {
    if (Date.now() >= deadline) throw new Error(`Timed out: ${label}`);
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}
