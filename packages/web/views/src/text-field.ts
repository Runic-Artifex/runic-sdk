/// <reference lib="esnext.disposable" preserve="true" />
import { disposable } from "./lifetime.js";

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

/** Creates a {@link TextFieldController}. A `using` declaration disposes it where the engine has `Symbol.dispose`. */
export function createTextFieldController<TState, TClient>(
  options: TextFieldControllerOptions<TState, TClient>): TextFieldController<TState, TClient> & Disposable {
  void options;
  throw new Error("Not implemented yet.");
  return disposable({} as TextFieldController<TState, TClient>);
}
