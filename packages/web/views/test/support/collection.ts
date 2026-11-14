// Waiting for garbage collection in tests that check that dropped objects are released.

type Collect = (force: boolean) => void;

/** The runtime's forced collection: `Bun.gc`, or Node's `gc` under `--expose-gc`; `undefined` when there is none. */
export function forcedCollection(): (() => void) | undefined {
  const host = globalThis as { readonly Bun?: { readonly gc: Collect }; readonly gc?: Collect };
  const collect = host.Bun?.gc ?? host.gc;
  return collect === undefined ? undefined : () => collect(true);
}

export interface CollectionWait {
  /** Whether `released()` held before the deadline. */
  readonly released: boolean;
  /** The forced collections it took. */
  readonly rounds: number;
  readonly elapsedMs: number;
}

/**
 * Forces collections until `released()` holds or `timeoutMs` passes. Each round ends the current job with a macrotask
 * turn, collects from that fresh, shallow stack (a conservative stack scan then finds no stale frame of the test), and
 * drains the microtasks, where finalization callbacks and promise reactions run, before it checks again. Ending the job
 * first matters: `WeakRef.prototype.deref` keeps its target alive until the job ends, so a check and a collection in the
 * same job could never release what the check looked at.
 *
 * Returns `undefined` when the runtime cannot force a collection; the caller then skips its check.
 */
export async function collectUntil(released: () => boolean, timeoutMs = 5_000): Promise<CollectionWait | undefined> {
  const collect = forcedCollection();
  if (collect === undefined) return undefined;
  const started = performance.now();
  let rounds = 0;
  while (!released()) {
    if (performance.now() - started >= timeoutMs) return { released: false, rounds, elapsedMs: performance.now() - started };
    await new Promise<void>(resolve => setTimeout(() => { collect(); resolve(); }, rounds < 10 ? 0 : 10));
    rounds++;
    for (let turn = 0; turn < 10; turn++) await Promise.resolve();
  }
  return { released: true, rounds, elapsedMs: performance.now() - started };
}
