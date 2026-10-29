import { untrack } from "svelte";
import type { Attachment } from "svelte/attachments";
import {
  createFieldController,
  type BridgeField,
  type FieldBaseline,
  type FieldDefinition,
  type FieldOptions,
  type ViewClient,
  type FieldState,
} from "@runic-artifex/views";
import type { TextFieldView } from "./use-text-field.svelte.js";

/** The elements {@link FieldHandle.attach} wires. */
export type FieldElement = HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement;

/** A field bound by {@link useField}. */
export interface FieldHandle<TValue> {
  /**
   * The draft while one exists, otherwise the member's value in the View's state. Setting it is an input by the user,
   * as `bind:value` and `bind:checked` do.
   */
  value: TValue;
  /**
   * What a text input shows: the text typed while it does not parse or still means `value` (so `1.` stays while typing
   * `1.5`), otherwise `value` formatted. Setting it is an input of text, parsed for the field's kind.
   */
  text: string;
  /**
   * True while a draft exists, from the first input until the last write settled and the state caught up, and after a
   * failed write. Keep the input enabled while it is true, rather than disabling it on the View's busy state.
   */
  readonly editing: boolean;
  /** Why the latest write or text failed (a `BridgeError` or a `FieldError`), until the next input, `retry()` or `reset()`. */
  readonly error: unknown;
  /** The member's validation messages in the View's state, or of the last checked write's receipt; `[]` when valid. */
  readonly errors: readonly string[];
  /** The producer's field while a checked write's `conflict` keeps the draft; `undefined` otherwise. */
  readonly conflict: FieldBaseline<TValue> | undefined;
  /** The user changed the value. */
  input(value: TValue): void;
  /**
   * The user is done with the field (`attach` calls it on blur): with `commit: "pause"` the draft is written now.
   * Resolves with the field's state once its writes settled, so a command that depends on the field can run after
   * it: `await title.commit(); await editor.save()`. Never rejects; a failure is `error`.
   */
  commit(): Promise<FieldState<TValue>>;
  /** Writes the draft again after a failed write; after a `conflict`, over the producer's value. */
  retry(): void;
  /** Drops the draft, the error and a conflict and shows the View's value. Writes in flight still complete. */
  reset(): void;
  /**
   * Wires an `<input>`, `<textarea>` or `<select>` to the field: `<input {@attach title.attach}>`. It writes the text
   * typed (`input` events; `change` of a `<select>` or checkbox, whose `checked` is the value), writes nothing during
   * IME composition, commits on blur, keeps the element's text (a checkbox's `checked`) equal to the field's without
   * moving the caret while it already shows it, and sets `aria-invalid` while the field has `errors` or an `error`.
   */
  readonly attach: Attachment<FieldElement>;
}

/**
 * Binds an element to one View member while the calling component is mounted, through `createFieldController`: pass a
 * generated descriptor such as `EditorFields.title`, or any definition with `read` and `write`. Call it during
 * component initialization; `options` are read then. Wire the element with `{@attach title.attach}`, or bind
 * `bind:value={title.value}` and call `commit` on blur yourself.
 *
 * ```svelte
 * const editor = useView(() => page);
 * const title = useField(() => editor, EditorFields.title, { commit: "pause", checked: true });
 * <input {@attach title.attach}>
 * {#if title.conflict}<button onclick={title.retry}>Keep mine</button>{/if}
 * ```
 *
 * With `checked: true` a `rejected` or `conflict` receipt keeps the draft and sets `error` (a `FieldError`), and a
 * conflict holds further writes until `retry()` or `reset()`. Unmounting writes a value that waits to be written.
 */
export function useField<TClient extends ViewClient, TValue>(
  view: () => TextFieldView<TClient>,
  field: BridgeField<NoInfer<TClient>, NoInfer<TClient>["snapshot"], TValue> | FieldDefinition<NoInfer<TClient>, NoInfer<TClient>["snapshot"], TValue>,
  options: FieldOptions = {},
): FieldHandle<TValue> {
  const controller = createFieldController<TClient["snapshot"], TClient, TValue>(field, options);
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

  const attach: Attachment<FieldElement> = element => {
    const checkbox = element instanceof HTMLInputElement && element.type === "checkbox";
    let composing = false;
    const changed = () => {
      if (checkbox) controller.input((element as HTMLInputElement).checked as TValue);
      else controller.inputText(element.value);
    };
    const compositionStart = () => { composing = true; controller.compositionStart(); };
    const compositionEnd = () => { composing = false; controller.compositionEnd(element.value); };
    const blur = () => { void controller.commit(); };
    // A `<select>` and a checkbox report a choice with `change`; text inputs report each edit with `input`.
    const event = checkbox || element instanceof HTMLSelectElement ? "change" : "input";
    element.addEventListener(event, changed);
    element.addEventListener("compositionstart", compositionStart);
    element.addEventListener("compositionend", compositionEnd);
    element.addEventListener("blur", blur);
    // Only this effect reads the field, so a change of the field updates the element without wiring it again.
    $effect(() => {
      const { value, text, errors, error } = current;
      element.setAttribute("aria-invalid", String(errors.length > 0 || error !== undefined));
      if (checkbox) {
        const checked = value === true;
        if ((element as HTMLInputElement).checked !== checked) (element as HTMLInputElement).checked = checked;
      } else if (!composing && element.value !== text) element.value = text;
    });
    return () => {
      element.removeEventListener(event, changed);
      element.removeEventListener("compositionstart", compositionStart);
      element.removeEventListener("compositionend", compositionEnd);
      element.removeEventListener("blur", blur);
    };
  };

  return {
    get value() { return current.value; },
    set value(value: TValue) { controller.input(value); },
    get text() { return current.text; },
    set text(text: string) { controller.inputText(text); },
    get editing() { return current.editing; },
    get error() { return current.error; },
    get errors() { return current.errors; },
    get conflict() { return current.conflict; },
    input: (value: TValue) => controller.input(value),
    commit: () => controller.commit(),
    retry: () => controller.retry(),
    reset: () => controller.reset(),
    attach,
  };
}
