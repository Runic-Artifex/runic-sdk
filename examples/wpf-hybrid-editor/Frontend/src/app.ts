import { connectEditor } from "./generated/editor.js";

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
  let writes: Promise<unknown> = Promise.resolve();
  const report = (cause: unknown) => { if (active) error.textContent = String(cause); };
  const enqueue = (write: () => Promise<unknown>) => {
    writes = writes.catch(() => undefined).then(write);
    void writes.catch(report);
  };
  const unsubscribe = editor.subscribe(state => {
    if (document.activeElement !== title) title.value = state.title;
    if (document.activeElement !== body) body.value = state.body;
    save.disabled = !state.canSave;
    cancel.disabled = !state.canSaveCancel;
    validation.textContent = state.validationMessage;
    error.textContent = state.error;
    status.textContent = `${state.isDirty ? "Unsaved changes. " : ""}${state.status}`;
  });
  title.addEventListener("input", () => { const value = title.value; enqueue(() => editor.setTitle(value)); });
  body.addEventListener("input", () => { const value = body.value; enqueue(() => editor.setBody(value)); });
  save.addEventListener("click", () => { void writes.then(() => editor.save()).catch(report); });
  // Do not queue cancellation behind Save: Save waits for this command.
  cancel.addEventListener("click", () => { void editor.saveCancel().catch(report); });
  window.addEventListener("beforeunload", () => { active = false; unsubscribe(); editor.dispose(); });
} catch (cause) {
  error.textContent = String(cause);
}
