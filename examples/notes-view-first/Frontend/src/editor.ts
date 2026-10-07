import type { EditorClient } from "./generated/editor.js";
import { EditorWrites } from "./editor-writes.js";
import { describeSaveFailure } from "./save-failure.js";

export function mountEditor(host: HTMLElement, editor: EditorClient): () => void {
  host.innerHTML = `<h2>Editor</h2><label>Title <input></label><label>Body <textarea></textarea></label><button data-save>Save</button><p data-message role="status"></p><p data-error role="alert" hidden></p>`;
  const title = host.querySelector("input")!;
  const body = host.querySelector("textarea")!;
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
      if (active) { error.textContent = String(cause); error.hidden = false; }
    }
  };
  const writes = new EditorWrites(cause => {
    if (!active) return;
    error.textContent = cause === undefined ? "" : String(cause);
    error.hidden = cause === undefined;
  });
  const unsubscribe = editor.subscribe(state => {
    if (document.activeElement !== title) title.value = state.title;
    if (document.activeElement !== body) body.value = state.body;
    save.disabled = !state.canSave;
    message.textContent = `${state.isDirty ? "Unsaved changes. " : ""}${state.savedMessage}`;
  });
  const titleChanged = () => { const value = title.value; writes.enqueue(() => editor.setTitle(value)); };
  const bodyChanged = () => { const value = body.value; writes.enqueue(() => editor.setBody(value)); };
  const saveClicked = () => {
    void run(async () => {
      const outcome = await writes.run(() => editor.save());
      return outcome.ok ? undefined : describeSaveFailure(outcome.failure);
    });
  };
  title.addEventListener("change", titleChanged);
  body.addEventListener("change", bodyChanged);
  save.addEventListener("click", saveClicked);
  return () => {
    active = false;
    title.removeEventListener("change", titleChanged);
    body.removeEventListener("change", bodyChanged);
    save.removeEventListener("click", saveClicked);
    unsubscribe();
    editor.dispose();
    host.replaceChildren();
  };
}
