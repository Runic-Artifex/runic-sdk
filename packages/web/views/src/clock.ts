// The timers of the protocol's waits (revision timeout, recovery backoff, reopen retries, interaction expiry).
// Tests replace `clock` to run them on virtual time; it is not part of the package's public API.

export interface Clock {
  set(callback: () => void, milliseconds: number): unknown;
  clear(handle: unknown): void;
  now(): number;
}

/** @internal */
export const clock: Clock = {
  set: (callback, milliseconds) => setTimeout(callback, milliseconds),
  clear: handle => clearTimeout(handle as ReturnType<typeof setTimeout>),
  now: () => Date.now(),
};

/** Resolves after `milliseconds` on the clock; `cancel` resolves it at once. */
export function delay(milliseconds: number): { readonly promise: Promise<void>; cancel(): void } {
  let handle: unknown;
  let done!: () => void;
  const promise = new Promise<void>(resolve => { done = resolve; handle = clock.set(resolve, milliseconds); });
  return { promise, cancel() { clock.clear(handle); done(); } };
}
