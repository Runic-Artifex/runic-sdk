/// <reference lib="esnext.disposable" preserve="true" />
import {
  createFieldController,
  type FieldController,
  type FieldOptions,
  type FieldSource,
  type FieldState,
} from "./field-controller.js";

/** What a text field shows: a {@link FieldState} of a string. */
export type TextFieldState = FieldState<string>;

/** The View a text field reads and writes: its latest state and the client to write with. */
export type TextFieldSource<TState, TClient> = FieldSource<TState, TClient>;

export interface TextFieldControllerOptions<TState, TClient> extends Omit<FieldOptions, "checked"> {
  /** Reads the field's value from the View's state. */
  readonly read: (state: TState) => string;
  /**
   * Writes a value: a generated setter (`(client, value) => client.setName(value)`), a command or a checked write.
   * The field shows its draft until the returned promise settles and the state caught up. A checked write's
   * `rejected` and `conflict` receipts keep the draft and set `error` (a `FieldError`); `committed-with-error` sets it
   * too, and `applied.validation` is `errors`.
   */
  readonly write: (client: TClient, value: string) => Promise<unknown>;
}

/**
 * A framework-neutral text input binding for one View member: the string case of {@link FieldController}, which
 * `@runic-artifex/angular`, `react`, `vue` and `svelte` wrap as `injectTextField` / `useTextField`. Typing key by
 * key never loses characters: the field holds a draft while writes are in flight, sends nothing during IME
 * composition, and shows the View's value, including any normalization, once the last write settled.
 */
export type TextFieldController<TState, TClient> = FieldController<TState, TClient, string>;

/**
 * Creates a {@link TextFieldController}: `createFieldController` for a string member read and written by the given
 * functions. A `using` declaration disposes it where the engine has `Symbol.dispose`.
 */
export function createTextFieldController<TState, TClient>(
  options: TextFieldControllerOptions<TState, TClient>): TextFieldController<TState, TClient> & Disposable {
  const { read, write, ...field } = options;
  return createFieldController<TState, TClient, string>({ kind: "text", read, write }, field);
}
