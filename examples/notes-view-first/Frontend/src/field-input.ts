import type { FieldController, ViewClient } from "@runic-artifex/views";

/**
 * Wires a text input to a field controller while the editor is mounted: the View's state, what the user types, IME
 * composition, commit on blur, and the text the field shows. The framework variants get the same from `useField`.
 */
export function bindTextInput<TState, TClient extends ViewClient<TState>>(
  element: HTMLInputElement | HTMLTextAreaElement,
  field: FieldController<TState, TClient, string>,
  client: TClient,
): () => void {
  let composing = false;
  const show = () => {
    const { text, errors, error } = field.current;
    element.setAttribute("aria-invalid", String(errors.length > 0 || error !== undefined));
    if (!composing && element.value !== text) element.value = text;
  };
  const input = () => field.inputText(element.value);
  const compositionStart = () => { composing = true; field.compositionStart(); };
  const compositionEnd = () => { composing = false; field.compositionEnd(element.value); };
  const blur = () => { void field.commit(); };
  const unsubscribeField = field.subscribe(show);
  // setSource does not notify, so the element follows each state here.
  const unsubscribeView = client.subscribe(state => { field.setSource({ state, client }); show(); });
  element.addEventListener("input", input);
  element.addEventListener("compositionstart", compositionStart);
  element.addEventListener("compositionend", compositionEnd);
  element.addEventListener("blur", blur);
  return () => {
    element.removeEventListener("input", input);
    element.removeEventListener("compositionstart", compositionStart);
    element.removeEventListener("compositionend", compositionEnd);
    element.removeEventListener("blur", blur);
    unsubscribeView();
    unsubscribeField();
  };
}

/**
 * Commits the fields and waits for their writes, so a command that depends on them, such as Save, can follow:
 * rejects with the error of a field whose write failed.
 */
export async function commitFields(...fields: readonly FieldController<unknown, unknown, unknown>[]): Promise<void> {
  await Promise.all(fields.map(field => field.commit()));
  const failed = fields.find(field => field.current.error !== undefined);
  if (failed !== undefined) throw failed.current.error;
}
