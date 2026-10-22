/// <reference lib="esnext.disposable" preserve="true" />
// `Symbol.dispose` and `AbortSignal` support shared by clients, controllers and waits.
//
// Engines without `Symbol.dispose`, such as older WebKitGTK and WKWebView, still
// load this module: the symbol is looked up when a client or controller is
// created, so a polyfill installed before that is used, and without one the
// object has only `dispose()`.

/** Defines `[Symbol.dispose]()` as `value.dispose()` where the engine has `Symbol.dispose`. */
export function disposable<T extends { dispose(): void }>(value: T): T & Disposable {
  const key = (Symbol as { readonly dispose?: unknown }).dispose;
  if (typeof key === "symbol" && !Object.hasOwn(value, key))
    Object.defineProperty(value, key, { value: function dispose() { value.dispose(); }, configurable: true, writable: true });
  return value as T & Disposable;
}

/** The reason an aborted signal rejects with: `signal.reason`, or an `AbortError` where the engine sets none. */
export function abortReason(signal: AbortSignal): unknown {
  return signal.reason ?? new DOMException("This operation was aborted.", "AbortError");
}

/** Throws the abort reason of an aborted signal. */
export function throwIfAborted(signal: AbortSignal | undefined): void {
  if (signal?.aborted) throw abortReason(signal);
}

/** True when `error` is the reason `signal` aborted with. */
export function isAbort(error: unknown, signal: AbortSignal | undefined): boolean {
  if (signal?.aborted !== true) return false;
  return signal.reason === undefined ? error instanceof DOMException && error.name === "AbortError" : error === signal.reason;
}

/**
 * Settles like `promise`, or rejects with the abort reason when `signal` aborts
 * first. The abort listener is removed when either happens. The work behind
 * `promise` is not cancelled; its later rejection is handled here.
 */
export function abortable<T>(promise: PromiseLike<T>, signal: AbortSignal | undefined): Promise<T> {
  if (signal === undefined) return Promise.resolve(promise);
  if (signal.aborted) {
    Promise.resolve(promise).catch(() => {});
    return Promise.reject(abortReason(signal));
  }
  return new Promise<T>((resolve, reject) => {
    const abort = () => reject(abortReason(signal));
    signal.addEventListener("abort", abort, { once: true });
    Promise.resolve(promise).then(
      value => { signal.removeEventListener("abort", abort); resolve(value); },
      (error: unknown) => { signal.removeEventListener("abort", abort); reject(error); },
    );
  });
}
