/// <reference lib="esnext.disposable" preserve="true" />
import { disposable } from "./lifetime.js";
import { reportBridgeError } from "./transport.js";

/** What a text field shows. */
export interface TextFieldState {
  /** The draft while one exists, otherwise the field's value in the View's state. */
  readonly value: string;
  /**
   * A draft exists: from the first input until the last write settled and the View's state caught up, or after a
   * failed write until `reset()` or the next successful write. Prefer it to disabling the field on the View's busy
   * state, which the field's own write causes.
   */
  readonly editing: boolean;
  /** Why the latest write failed, until the next input or `reset()`; `undefined` otherwise. */
  readonly error: unknown;
}

/** The View a text field reads and writes: its latest state and the client to write with. */
export interface TextFieldSource<TState, TClient> {
  readonly state: TState;
  readonly client: TClient;
}

export interface TextFieldControllerOptions<TState, TClient> {
  /** Reads the field's value from the View's state. */
  readonly read: (state: TState) => string;
  /**
   * Writes a value: a generated setter (`(client, value) => client.setName(value)`), a command or a checked write.
   * The field shows its draft until the returned promise settles and the state caught up.
   */
  readonly write: (client: TClient, value: string) => Promise<unknown>;
  /**
   * With `true` (the default) at most one write is in flight, and values typed meanwhile coalesce into the latest
   * one, which is written when the write settles. With `false` every value is written at once; setters keep them in
   * order with their edit numbers.
   */
  readonly coalesce?: boolean;
  /**
   * With `true` (the default) a failed write keeps the draft and sets `error`, so the user can correct it. With
   * `false` the field shows the View's value again and sets `error`.
   */
  readonly keepDraftOnError?: boolean;
}

/**
 * A framework-neutral text input binding for one View member (`@runic-artifex/angular`, `react`, `vue` and `svelte`
 * wrap it). Typing key by key never loses characters: the field holds a draft while writes are in flight, sends
 * nothing during IME composition, and shows the View's value, including any normalization, once the last write
 * settled.
 */
export interface TextFieldController<TState, TClient> {
  readonly current: TextFieldState;
  /** Calls `listener` after each change of `current`, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /**
   * Sets the View the field reads and writes, or `undefined` while none is connected. Call it with each new state.
   * `setSource` does not notify subscribers; read `current` after calling it.
   */
  setSource(source: TextFieldSource<TState, TClient> | undefined): void;
  /** The user changed the field's text, such as on an `input` event. */
  input(value: string): void;
  /** An IME composition started: values are kept as the draft but not written until it ends. */
  compositionStart(): void;
  /** The IME composition ended with `value`, which is then written like an input. */
  compositionEnd(value: string): void;
  /** Drops the draft and the error and shows the View's value. Writes in flight still complete. */
  reset(): void;
  /** Stops notifying. A value that waits to be written because of coalescing is still written. */
  dispose(): void;
}

// A generated client exposes its latest state as `snapshot`, which is current as soon as a write settled.
function latestState<TState>(client: unknown): { readonly state: TState } | undefined {
  if (client === null || typeof client !== "object" || typeof (client as { subscribe?: unknown }).subscribe !== "function"
    || !("snapshot" in client)) return undefined;
  return { state: (client as { readonly snapshot: TState }).snapshot };
}

/** Creates a {@link TextFieldController}. A `using` declaration disposes it where the engine has `Symbol.dispose`. */
export function createTextFieldController<TState, TClient>(
  options: TextFieldControllerOptions<TState, TClient>): TextFieldController<TState, TClient> & Disposable {
  const coalesce = options.coalesce !== false;
  const keepDraftOnError = options.keepDraftOnError !== false;
  const listeners = new Set<() => void>();
  let source: TextFieldSource<TState, TClient> | undefined;
  let draft: string | undefined;
  let error: unknown;
  let composing = false;
  let inFlight = 0;
  // The latest value typed while a write was in flight (coalescing) or while no client was connected.
  let queued: string | undefined;
  let disposed = false;
  let current: TextFieldState = Object.freeze({ value: "", editing: false, error: undefined });

  const viewValue = () => source === undefined ? "" : options.read(source.state);
  function update(notify = true): void {
    const next = { value: draft ?? viewValue(), editing: draft !== undefined, error };
    if (next.value === current.value && next.editing === current.editing && Object.is(next.error, current.error)) return;
    current = Object.freeze(next);
    if (!notify || disposed) return;
    for (const listener of [...listeners]) {
      try { listener(); } catch (failure) { reportBridgeError(failure); }
    }
  }

  function write(value: string): void {
    const client = source!.client;
    inFlight++;
    let pending: Promise<unknown>;
    try { pending = Promise.resolve(options.write(client, value)); }
    catch (failure) { pending = Promise.reject(failure); }
    pending.then(() => settled(undefined, false), failure => settled(failure, true));
  }

  function send(value: string): void {
    if (source === undefined || coalesce && inFlight !== 0) { queued = value; return; }
    queued = undefined;
    write(value);
  }

  function settled(failure: unknown, failed: boolean): void {
    inFlight--;
    if (disposed) return;
    if (failed) error = failure;
    else error = undefined;
    // Values typed meanwhile are written next, also after a failure: they are newer than the failed one.
    if (queued !== undefined && source !== undefined && !composing) {
      if (!coalesce || inFlight === 0) send(queued);
      update();
      return;
    }
    if (inFlight === 0 && !composing && queued === undefined && draft !== undefined && (!failed || !keepDraftOnError)) {
      // The last write settled: the View's state now includes it, with any normalization the producer applied.
      draft = undefined;
      const latest = source === undefined ? undefined : latestState<TState>(source.client);
      if (latest !== undefined && source !== undefined) source = { state: latest.state, client: source.client };
    }
    update();
  }

  function input(value: string): void {
    if (disposed) return;
    error = undefined;
    draft = value;
    if (!composing) send(value);
    update();
  }

  return disposable({
    get current() { return current; },
    subscribe(listener: () => void) {
      if (disposed) return () => {};
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    setSource(next: TextFieldSource<TState, TClient> | undefined) {
      source = next;
      if (disposed) return;
      // A value typed before a client was connected is written once one is.
      if (source !== undefined && queued !== undefined && inFlight === 0 && !composing) send(queued);
      update(false);
    },
    input,
    compositionStart() { composing = true; },
    compositionEnd(value: string) {
      composing = false;
      input(value);
    },
    reset() {
      if (disposed) return;
      draft = undefined;
      error = undefined;
      queued = undefined;
      update();
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      listeners.clear();
      // The last value the user typed is not lost with the field.
      if (queued !== undefined && source !== undefined) {
        const value = queued;
        queued = undefined;
        inFlight++;
        let pending: Promise<unknown>;
        try { pending = Promise.resolve(options.write(source.client, value)); }
        catch (failure) { pending = Promise.reject(failure); }
        pending.catch(failure => reportBridgeError(failure));
      }
    },
  });
}
