import type { EditorClient } from "./generated/editor.js";
import { EditorWrites } from "./editor-writes.js";
import { describeSaveFailure } from "./save-failure.js";

export function mountEditor(host: HTMLElement, view: EditorClient): () => void {
  host.innerHTML = `<h2>Full editor</h2><label>Title<input data-title></label><label>Body<textarea data-body></textarea></label><button data-save>Save</button><button data-discard>Discard changes</button><p data-message role="status"></p><p data-failure role="alert" hidden></p><p data-activation class="muted"></p>`;
  const title = host.querySelector<HTMLInputElement>("[data-title]")!;
  const body = host.querySelector<HTMLTextAreaElement>("[data-body]")!;
  const save = host.querySelector<HTMLButtonElement>("[data-save]")!;
  const discard = host.querySelector<HTMLButtonElement>("[data-discard]")!;
  const message = host.querySelector<HTMLElement>("[data-message]")!;
  const activation = host.querySelector<HTMLElement>("[data-activation]")!;
  const failure = host.querySelector<HTMLElement>("[data-failure]")!;
  const showFailure = (text: string | undefined) => { failure.textContent = text ?? ""; failure.hidden = text === undefined; };
  const report = (error: unknown) => { message.textContent = String(error); message.classList.add("error"); };
  const writes = new EditorWrites(cause => { if (cause !== undefined) report(cause); });
  const removeDiscardHandler = view.interactions.confirmDiscard.handle(async (request, { signal }) => {
    if (signal.aborted) throw signal.reason;
    return window.confirm(`Discard the ${request.bodyLength} unsaved characters in “${request.title}”?`);
  });
  const unsubscribe = view.subscribe(state => {
    if (document.activeElement !== title) title.value = state.title;
    if (document.activeElement !== body) body.value = state.body;
    save.disabled = !state.canSave;
    message.textContent = state.savedMessage;
    activation.textContent = `Activated ${state.activationCount} × · deactivated ${state.deactivationCount} ×`;
  });
  const titleChanged = () => { const value = title.value; writes.enqueue(() => view.setTitle(value)); };
  const bodyChanged = () => { const value = body.value; writes.enqueue(() => view.setBody(value)); };
  // Save declares SaveFailure, so a missing title resolves the outcome instead
  // of rejecting; only unexpected failures reach the catch.
  // The latest save wins: an earlier one that settles later shows nothing.
  let saves = 0;
  const saveClicked = () => {
    const save = ++saves;
    showFailure(undefined);
    void writes.run(() => view.save()).then(outcome => {
      if (save === saves) showFailure(outcome.ok ? undefined : describeSaveFailure(outcome.failure));
    }, error => { if (save === saves) report(error); });
  };
  const discardClicked = () => { void writes.run(() => view.discard()).catch(report); };
  title.addEventListener("change", titleChanged);
  body.addEventListener("change", bodyChanged);
  save.addEventListener("click", saveClicked);
  discard.addEventListener("click", discardClicked);
  return () => {
    title.removeEventListener("change", titleChanged); body.removeEventListener("change", bodyChanged);
    save.removeEventListener("click", saveClicked); discard.removeEventListener("click", discardClicked);
    removeDiscardHandler(); unsubscribe(); view.dispose(); host.replaceChildren();
  };
}
