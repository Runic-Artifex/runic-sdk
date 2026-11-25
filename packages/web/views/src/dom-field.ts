/// <reference lib="esnext.disposable" preserve="true" />
// DOM wiring of a field controller for applications without a framework binding: the plain TypeScript counterpart
// of React's `inputProps`, Vue's `v-model`, Svelte's `{@attach}` and Angular's `[runicField]`.
import type { ViewClient } from "./connection.js";
import { createFieldController, type FieldController, type FieldDefinition, type FieldOptions } from "./field-controller.js";
import type { BridgeField } from "./field.js";
import { disposable } from "./lifetime.js";

/** The elements {@link attachField} and {@link bindField} wire. */
export type FieldElement = HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement;

function wire<TValue>(element: FieldElement, field: FieldController<unknown, unknown, TValue>): { show(): void; detach(): void } {
  const type = element.tagName === "INPUT" ? (element as HTMLInputElement).type : undefined;
  const checkbox = type === "checkbox";
  // A radio of a group bound to one field: its own `value` is its option, and it is checked while the field's text is.
  const radio = type === "radio";
  const choice = checkbox || radio || element.tagName === "SELECT";
  // With `commit: "change"` a choice is written at once; typed text waits for the blur.
  const commitsChoice = choice && field.field.commitMode === "change";
  let composing = false;
  const show = () => {
    const { value, text, errors, error } = field.current;
    element.setAttribute("aria-invalid", String(errors.length > 0 || error !== undefined));
    if (checkbox || radio) {
      const checked = checkbox ? value === true : element.value === text;
      if ((element as HTMLInputElement).checked !== checked) (element as HTMLInputElement).checked = checked;
    } else if (!composing && element.value !== text) element.value = text;
  };
  const changed = () => {
    if (checkbox) field.input((element as HTMLInputElement).checked as TValue);
    // Only the radio that became checked reports the choice.
    else if (radio && !(element as HTMLInputElement).checked) return;
    else field.inputText(element.value);
    if (commitsChoice) void field.commit();
  };
  const compositionStart = () => { composing = true; field.compositionStart(); };
  const compositionEnd = () => { composing = false; field.compositionEnd(element.value); };
  const blur = () => { void field.commit(); };
  // A `<select>`, a checkbox and a radio report a choice with `change`; text inputs report each edit with `input`.
  const event = choice ? "change" : "input";
  element.addEventListener(event, changed);
  element.addEventListener("compositionstart", compositionStart);
  element.addEventListener("compositionend", compositionEnd);
  element.addEventListener("blur", blur);
  const unsubscribe = field.subscribe(show);
  show();
  return {
    show,
    detach() {
      element.removeEventListener(event, changed);
      element.removeEventListener("compositionstart", compositionStart);
      element.removeEventListener("compositionend", compositionEnd);
      element.removeEventListener("blur", blur);
      unsubscribe();
    },
  };
}

/**
 * Wires an `<input>`, `<textarea>` or `<select>` to a field controller until the returned function is called: it
 * writes the text typed (`input` events; `change` of a `<select>` or a checkbox, whose `checked` is the value, and of
 * a radio that became checked, whose `value` is the text), writes nothing during IME composition, commits on blur
 * (and, with `commit: "change"`, after each choice of a checkbox, radio or `<select>`), keeps the element's text (a
 * checkbox's `checked`; a radio is checked while its `value` is the field's text) equal to the field's without moving
 * the caret while it already shows it, and sets `aria-invalid` while the field has `errors` or an `error`. Wire each
 * radio of a group to the same field. The caller feeds the controller its View with `setSource`; {@link bindField} does both.
 */
export function attachField<TValue>(element: FieldElement, field: FieldController<never, never, TValue>): () => void {
  return wire(element, field as unknown as FieldController<unknown, unknown, TValue>).detach;
}

const nativeTypes = new Set(["number", "date", "time", "datetime-local"]);

/** A field that {@link bindField} wired to an element and a client. `dispose()` unwires it. */
export interface BoundField<TState, TClient, TValue> extends FieldController<TState, TClient, TValue>, Disposable {}

/**
 * Binds an element, or the radios of a group, to one member of a connected client: creates a `createFieldController`
 * for a generated descriptor such as `EditorFields.title` (or any definition with `read` and `write`), follows the
 * client's state and wires each element as {@link attachField} does.
 *
 * ```ts
 * const editor = await connectEditor();
 * const title = bindField(document.querySelector("#title")!, editor, EditorFields.title, { commit: "pause" });
 * save.onclick = async () => { if ((await title.commit()).error === undefined) await editor.save(); };
 * // A radio group: each radio's `value` is an option; the one whose value is the field's text is checked.
 * const size = bindField(document.querySelectorAll<HTMLInputElement>("input[name=size]"), editor, EditorFields.size);
 * ```
 *
 * A field bound to `<input type="number">`, `"date"`, `"time"` or `"datetime-local"` without a `locale` option uses
 * the invariant and ISO forms those inputs take (`locale: null`) instead of the window's locale.
 *
 * `dispose()` unwires the elements and stops following the client; a value that waits to be written is still written.
 * The client stays connected: its owner disposes it.
 */
export function bindField<TClient extends ViewClient, TValue>(
  element: FieldElement | Iterable<FieldElement>,
  client: TClient,
  field: BridgeField<NoInfer<TClient>, NoInfer<TClient>["snapshot"], TValue> | FieldDefinition<NoInfer<TClient>, NoInfer<TClient>["snapshot"], TValue>,
  options: FieldOptions<TValue> = {},
): BoundField<TClient["snapshot"], TClient, TValue> {
  // A `<select>` is iterable too (over its options), so an element is told apart by its tag.
  const elements: readonly FieldElement[] = "tagName" in element ? [element] : [...element];
  // `<input type="number">` and the browser's date and time inputs take only the invariant and ISO forms, which they
  // show in the browser's language: such a field does not follow the window's locale.
  const native = elements.length > 0 && elements.every(item => item.tagName === "INPUT" && nativeTypes.has((item as HTMLInputElement).type));
  const controller = createFieldController<TClient["snapshot"], TClient, TValue>(
    field, native && options.locale === undefined ? { ...options, locale: null } : options);
  // The radios of a group share the controller, so they show one draft.
  const wired = elements.map(item => wire(item, controller as unknown as FieldController<unknown, unknown, TValue>));
  // setSource does not notify, so the elements follow each state here.
  const unsubscribe = client.subscribe(state => {
    controller.setSource({ state, client });
    for (const item of wired) item.show();
  });
  let disposed = false;
  return disposable<BoundField<TClient["snapshot"], TClient, TValue>>(Object.assign(Object.create(controller) as typeof controller, {
    dispose() {
      if (disposed) return;
      disposed = true;
      unsubscribe();
      for (const item of wired) item.detach();
      controller.dispose();
    },
  }));
}

/**
 * Commits fields and waits for their writes, so a command that depends on them, such as Save, can follow. Resolves
 * once every field settled; rejects with the `error` of the first field whose write or text failed.
 *
 * ```ts
 * await commitFields(title, body);
 * await editor.save();
 * ```
 */
export async function commitFields(...fields: readonly Pick<FieldController<never, never, unknown>, "commit">[]): Promise<void> {
  const states = await Promise.all(fields.map(field => field.commit()));
  const failed = states.find(state => state.error !== undefined);
  if (failed !== undefined) throw failed.error;
}
