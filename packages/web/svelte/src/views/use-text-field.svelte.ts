import { untrack } from "svelte";
import { createTextFieldController, type ViewClient } from "@runic-artifex/views";
import { derivedFields } from "./derived-fields.svelte.js";

/** A text input bound to one View member. */
export interface TextFieldHandle {
  /**
   * What the input shows: the draft while one exists, otherwise the member's value in the View's state. Setting it
   * is an input by the user, as `bind:value` does.
   */
  value: string;
  /**
   * True while a draft exists, from the first input until the last write settled and the state caught up, and after a
   * failed write. Keep the input enabled while it is true, rather than disabling it on the View's busy state, which
   * the field's own write causes.
   */
  readonly editing: boolean;
  /** Why the latest write failed, until the next input or `reset()`. */
  readonly error: unknown;
  /** An IME composition started: the text is kept but not written until it ends. Use it as `oncompositionstart`. */
  compositionStart(): void;
  /** The IME composition ended: pass the `compositionend` event, or the text, which is then written. */
  compositionEnd(event: Event | string): void;
  /** Drops the draft and the error and shows the View's value. Writes in flight still complete. */
  reset(): void;
}

export interface UseTextFieldOptions {
  /**
   * With `true` (the default) at most one write is in flight, and text typed meanwhile is written next, as one
   * value. With `false` every input is written.
   */
  readonly coalesce?: boolean;
  /** With `true` (the default) a failed write keeps the draft and sets `error`; with `false` the field shows the View's value again. */
  readonly keepDraftOnError?: boolean;
}

/** The View a text field binds to: a `useView` handle, or any state and client. */
export interface TextFieldView<TClient extends ViewClient> {
  readonly state: TClient["snapshot"] | undefined;
  readonly client: TClient | undefined;
}

function textOf(event: Event | string): string {
  if (typeof event === "string") return event;
  const target = event.target as { value?: unknown } | null;
  return typeof target?.value === "string" ? target.value : "";
}

/**
 * Binds a text input to one member of a View while the calling component is mounted, through
 * `createTextFieldController`. Call it during component initialization. Typing key by key never loses characters:
 * while its writes are in flight the field shows what the user typed, at most one write is in flight (newer text is
 * written next), nothing is written during IME composition, and once the last write settled it shows the View's
 * value, including any normalization.
 *
 * ```svelte
 * const person = useView(() => page);
 * const name = useTextField(() => person, state => state.name, (client, value) => client.setName(value));
 * <input bind:value={name.value} oncompositionstart={name.compositionStart} oncompositionend={name.compositionEnd}>
 * ```
 *
 * `write` may be a generated setter, a command or a checked write. Its failure, and a checked write's `rejected` or
 * `conflict` receipt, keep the draft and set `error` (a receipt's is a `FieldError`). For a generated member, prefer
 * `useField` with its `{View}Fields` descriptor.
 */
export function useTextField<TClient extends ViewClient>(
  view: () => TextFieldView<TClient>,
  read: (state: TClient["snapshot"]) => string,
  write: (client: TClient, value: string) => Promise<unknown>,
  options: UseTextFieldOptions = {},
): TextFieldHandle {
  const controller = createTextFieldController<TClient["snapshot"], TClient>({ read, write, ...options });
  let current = $state.raw(controller.current);
  const unsubscribe = controller.subscribe(() => { current = controller.current; });
  // A pre effect runs when it is created, so the teardown exists even if the owner is destroyed before its first
  // flush (see useOperation). It reads no state, so it never re-runs.
  $effect.pre(() => () => { unsubscribe(); controller.dispose(); });
  $effect.pre(() => {
    const { state, client } = view();
    untrack(() => {
      controller.setSource(state === undefined || client === undefined ? undefined : { state, client });
      current = controller.current;
    });
  });
  // One derived per member, so a reader of one member runs again only when that member changes.
  const fields = derivedFields(() => current, ["value", "editing", "error"] as const);
  return {
    get value() { return fields.value(); },
    set value(text: string) { controller.input(text); },
    get editing() { return fields.editing(); },
    get error() { return fields.error(); },
    compositionStart: () => controller.compositionStart(),
    compositionEnd: (event: Event | string) => controller.compositionEnd(textOf(event)),
    reset: () => controller.reset(),
  };
}
