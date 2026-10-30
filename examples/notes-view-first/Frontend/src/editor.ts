import { bindField, commitFields, describeBridgeError } from "@runic-artifex/views";
import { EditorFields, type EditorClient } from "./generated/editor.js";
import { describeSaveFailure } from "./save-failure.js";

export function mountEditor(host: HTMLElement, editor: EditorClient): () => void {
  host.innerHTML = `<h2>Editor</h2><label>Title <input></label><label>Body <textarea></textarea></label><button data-save>Save</button><p data-message role="status"></p><p data-error role="alert" hidden></p>`;
  const titleInput = host.querySelector("input")!;
  const bodyInput = host.querySelector("textarea")!;
  const save = host.querySelector<HTMLButtonElement>("[data-save]")!;
  const message = host.querySelector<HTMLElement>("[data-message]")!;
  const error = host.querySelector<HTMLElement>("[data-error]")!;
  let active = true;
  // An action resolves to the text of a declared failure, or undefined.
  const run = async (action: () => Promise<string | undefined>) => {
    try {
      const failure = await action();
      if (active) { error.textContent = failure ?? ""; error.hidden = failure === undefined; }
    } catch (cause) {
      if (active) { error.textContent = describeBridgeError(cause); error.hidden = false; }
    }
  };
  // A field is written after a pause in typing or when it loses focus, not on every keystroke. bindField wires the
  // input: the View's state, what the user types, IME composition and commit on blur.
  const title = bindField(titleInput, editor, EditorFields.title, { commit: "pause" });
  const body = bindField(bodyInput, editor, EditorFields.body, { commit: "pause" });
  // A field's failed write shows until the field's next successful write.
  let writeError: unknown;
  const showWriteError = () => {
    const cause = title.current.error ?? body.current.error;
    if (!active || Object.is(cause, writeError)) return;
    writeError = cause;
    error.textContent = describeBridgeError(cause);
    error.hidden = cause === undefined;
  };
  const stopTitle = title.subscribe(showWriteError);
  const stopBody = body.subscribe(showWriteError);
  const unsubscribe = editor.subscribe(state => {
    save.disabled = !state.canSave;
    message.textContent = `${state.isDirty ? "Unsaved changes. " : ""}${state.savedMessage}`;
  });
  const saveClicked = () => {
    void run(async () => {
      // Save follows the fields' writes; a write that failed stops it.
      await commitFields(title, body);
      const outcome = await editor.save();
      return outcome.ok ? undefined : describeSaveFailure(outcome.failure);
    });
  };
  save.addEventListener("click", saveClicked);
  return () => {
    active = false;
    save.removeEventListener("click", saveClicked);
    unsubscribe();
    stopTitle();
    stopBody();
    // Unwires the inputs; text that waits for its pause is written before the editor is released.
    title.dispose();
    body.dispose();
    editor.dispose();
    host.replaceChildren();
  };
}
