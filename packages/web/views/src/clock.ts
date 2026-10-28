// The timers of the protocol's waits (revision timeout, recovery backoff, reopen retries, interaction expiry).
// Tests replace `clock` to run them on virtual time; it is not part of the package's public API.

export interface Clock {
  set(callback: () => void, milliseconds: number): unknown;
  clear(handle: unknown): void;
  now(): number;
}

/** The clock of the runtime; not exported from the package entries. */
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

/**
 * A random UUID for sessions and presentation ids. `crypto.randomUUID` exists only in secure contexts, so a page
 * served over plain HTTP gets one from `crypto.getRandomValues`.
 */
export function randomId(): string {
  const crypto = globalThis.crypto;
  if (typeof crypto?.randomUUID === "function") return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = bytes[6]! & 0x0f | 0x40;
  bytes[8] = bytes[8]! & 0x3f | 0x80;
  const hex = [...bytes].map(byte => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
