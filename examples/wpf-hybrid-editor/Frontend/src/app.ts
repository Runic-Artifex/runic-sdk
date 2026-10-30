import { bindField, commitFields, describeBridgeError } from "@runic-artifex/views";
import { connectEditor, EditorFields } from "./generated/editor.js";

const title = document.querySelector<HTMLInputElement>("#title")!;
const body = document.querySelector<HTMLTextAreaElement>("#body")!;
const save = document.querySelector<HTMLButtonElement>("#save")!;
const cancel = document.querySelector<HTMLButtonElement>("#cancel")!;
const validation = document.querySelector<HTMLElement>("#validation")!;
const error = document.querySelector<HTMLElement>("#error")!;
const status = document.querySelector<HTMLElement>("#status")!;

// The frontend owns connection, focus and event ordering. Validation, dirty
// tracking, saving and cancellation live in the existing Toolkit model.
try {
  const editor = await connectEditor();
  let active = true;
  const report = (cause: unknown) => { if (active) error.textContent = describeBridgeError(cause); };
  // Each field keeps what the user typed while its writes are on their way, writes them in order and shows the
  // model's value once they settled, so no write queue of its own is needed.
  const fields = [bindField(title, editor, EditorFields.title), bindField(body, editor, EditorFields.body)];
  for (const field of fields) field.subscribe(() => { if (field.current.error !== undefined) report(field.current.error); });
  const unsubscribe = editor.subscribe(state => {
    save.disabled = !state.canSave;
    cancel.disabled = !state.canSaveCancel;
    validation.textContent = state.validationMessage;
    error.textContent = state.error;
    status.textContent = `${state.isDirty ? "Unsaved changes. " : ""}${state.status}`;
  });
  // Save runs after the fields' writes settled, and not after a failed one.
  save.addEventListener("click", () => { void commitFields(...fields).then(() => editor.save()).catch(report); });
  // Do not wait for the fields before cancellation: Save waits for this command.
  cancel.addEventListener("click", () => { void editor.saveCancel().catch(report); });
  window.addEventListener("beforeunload", () => {
    active = false;
    unsubscribe();
    for (const field of fields) field.dispose();
    editor.dispose();
  });
} catch (cause) {
  error.textContent = describeBridgeError(cause);
}
