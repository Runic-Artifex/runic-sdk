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
  const checkbox = element.tagName === "INPUT" && (element as HTMLInputElement).type === "checkbox";
  let composing = false;
  const show = () => {
    const { value, text, errors, error } = field.current;
    element.setAttribute("aria-invalid", String(errors.length > 0 || error !== undefined));
    if (checkbox) {
      const checked = value === true;
      if ((element as HTMLInputElement).checked !== checked) (element as HTMLInputElement).checked = checked;
    } else if (!composing && element.value !== text) element.value = text;
  };
  const changed = () => {
    if (checkbox) field.input((element as HTMLInputElement).checked as TValue);
    else field.inputText(element.value);
  };
  const compositionStart = () => { composing = true; field.compositionStart(); };
  const compositionEnd = () => { composing = false; field.compositionEnd(element.value); };
  const blur = () => { void field.commit(); };
  // A `<select>` and a checkbox report a choice with `change`; text inputs report each edit with `input`.
  const event = checkbox || element.tagName === "SELECT" ? "change" : "input";
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
 * writes the text typed (`input` events; `change` of a `<select>` or a checkbox, whose `checked` is the value),
 * writes nothing during IME composition, commits on blur, keeps the element's text (a checkbox's `checked`) equal to
 * the field's without moving the caret while it already shows it, and sets `aria-invalid` while the field has
 * `errors` or an `error`. The caller feeds the controller its View with `setSource`; {@link bindField} does both.
 */
export function attachField<TValue>(element: FieldElement, field: FieldController<never, never, TValue>): () => void {
  return wire(element, field as unknown as FieldController<unknown, unknown, TValue>).detach;
}

/** A field that {@link bindField} wired to an element and a client. `dispose()` unwires it. */
export interface BoundField<TState, TClient, TValue> extends FieldController<TState, TClient, TValue>, Disposable {}

/**
 * Binds an element to one member of a connected client: creates a `createFieldController` for a generated
 * descriptor such as `EditorFields.title` (or any definition with `read` and `write`), follows the client's state and
 * wires the element as {@link attachField} does.
 *
 * ```ts
 * const editor = await connectEditor();
 * const title = bindField(document.querySelector("#title")!, editor, EditorFields.title, { commit: "pause" });
 * save.onclick = async () => { if ((await title.commit()).error === undefined) await editor.save(); };
 * ```
 *
 * `dispose()` unwires the element and stops following the client; a value that waits to be written is still written.
 * The client stays connected: its owner disposes it.
 */
export function bindField<TClient extends ViewClient, TValue>(
  element: FieldElement,
  client: TClient,
  field: BridgeField<NoInfer<TClient>, NoInfer<TClient>["snapshot"], TValue> | FieldDefinition<NoInfer<TClient>, NoInfer<TClient>["snapshot"], TValue>,
  options: FieldOptions = {},
): BoundField<TClient["snapshot"], TClient, TValue> {
  const controller = createFieldController<TClient["snapshot"], TClient, TValue>(field, options);
  const wired = wire(element, controller as unknown as FieldController<unknown, unknown, TValue>);
  // setSource does not notify, so the element follows each state here.
  const unsubscribe = client.subscribe(state => {
    controller.setSource({ state, client });
    wired.show();
  });
  let disposed = false;
  return disposable<BoundField<TClient["snapshot"], TClient, TValue>>(Object.assign(Object.create(controller) as typeof controller, {
    dispose() {
      if (disposed) return;
      disposed = true;
      unsubscribe();
      wired.detach();
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
