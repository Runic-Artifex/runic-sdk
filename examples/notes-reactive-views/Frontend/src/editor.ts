import { bindField, commitFields, describeBridgeError } from "@runic-artifex/views";
import { EditorFields, type EditorClient } from "./generated/editor.js";
import { describeSaveFailure } from "./save-failure.js";

export function mountEditor(host: HTMLElement, view: EditorClient): () => void {
  host.innerHTML = `<h2>Full editor</h2><label>Title<input data-title></label><label>Body<textarea data-body></textarea></label><button data-save>Save</button><button data-discard>Discard changes</button><p data-message role="status"></p><p data-failure role="alert" hidden></p><p data-activation class="muted"></p>`;
  const titleInput = host.querySelector<HTMLInputElement>("[data-title]")!;
  const bodyInput = host.querySelector<HTMLTextAreaElement>("[data-body]")!;
  const save = host.querySelector<HTMLButtonElement>("[data-save]")!;
  const discard = host.querySelector<HTMLButtonElement>("[data-discard]")!;
  const message = host.querySelector<HTMLElement>("[data-message]")!;
  const activation = host.querySelector<HTMLElement>("[data-activation]")!;
  const failure = host.querySelector<HTMLElement>("[data-failure]")!;
  const showFailure = (text: string | undefined) => { failure.textContent = text ?? ""; failure.hidden = text === undefined; };
  const report = (error: unknown) => { message.textContent = describeBridgeError(error); message.classList.add("error"); };
  // A field is written after a pause in typing or when it loses focus, not on every keystroke. bindField wires the
  // input: the View's state, what the user types, IME composition and commit on blur.
  const title = bindField(titleInput, view, EditorFields.title, { commit: "pause" });
  const body = bindField(bodyInput, view, EditorFields.body, { commit: "pause" });
  const reportWriteError = (field: typeof title) => {
    let reported: unknown;
    return field.subscribe(() => {
      const cause = field.current.error;
      if (cause !== undefined && !Object.is(cause, reported)) report(cause);
      reported = cause;
    });
  };
  const stopTitle = reportWriteError(title);
  const stopBody = reportWriteError(body);
  const removeDiscardHandler = view.interactions.confirmDiscard.handle(async (request, { signal }) => {
    if (signal.aborted) throw signal.reason;
    return window.confirm(`Discard the ${request.bodyLength} unsaved characters in “${request.title}”?`);
  });
  const unsubscribe = view.subscribe(state => {
    save.disabled = !state.canSave;
    message.textContent = state.savedMessage;
    activation.textContent = `Activated ${state.activationCount} × · deactivated ${state.deactivationCount} ×`;
  });
  // Save and Discard follow the fields' writes; a write that failed stops them.
  // Save declares SaveFailure, so a missing title resolves the outcome instead
  // of rejecting; only unexpected failures reach the catch.
  // The latest save wins: an earlier one that settles later shows nothing.
  let saves = 0;
  const saveClicked = () => {
    const save = ++saves;
    showFailure(undefined);
    void commitFields(title, body).then(() => view.save()).then(outcome => {
      if (save === saves) showFailure(outcome.ok ? undefined : describeSaveFailure(outcome.failure));
    }, error => { if (save === saves) report(error); });
  };
  const discardClicked = () => { void commitFields(title, body).then(() => view.discard()).catch(report); };
  save.addEventListener("click", saveClicked);
  discard.addEventListener("click", discardClicked);
  return () => {
    stopTitle(); stopBody();
    // Unwires the inputs; text that waits for its pause is written before the editor is released.
    title.dispose(); body.dispose();
    save.removeEventListener("click", saveClicked); discard.removeEventListener("click", discardClicked);
    removeDiscardHandler(); unsubscribe(); view.dispose(); host.replaceChildren();
  };
}
