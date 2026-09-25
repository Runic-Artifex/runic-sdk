import type { EditorView } from "./generated/editor.js";

export function mountEditor(host: HTMLElement, view: EditorView): () => void {
  host.innerHTML = `<h2>Full editor</h2><label>Title<input data-title></label><label>Body<textarea data-body></textarea></label><button data-save>Save</button><p data-message role="status"></p><p data-activation class="muted"></p>`;
  const title = host.querySelector<HTMLInputElement>("[data-title]")!;
  const body = host.querySelector<HTMLTextAreaElement>("[data-body]")!;
  const save = host.querySelector<HTMLButtonElement>("[data-save]")!;
  const message = host.querySelector<HTMLElement>("[data-message]")!;
  const activation = host.querySelector<HTMLElement>("[data-activation]")!;
  const report = (error: unknown) => { message.textContent = String(error); message.classList.add("error"); };
  const unsubscribe = view.subscribe(state => {
    if (document.activeElement !== title) title.value = state.title;
    if (document.activeElement !== body) body.value = state.body;
    save.disabled = !state.canSave;
    message.textContent = state.savedMessage;
    activation.textContent = `Activated ${state.activationCount} × · deactivated ${state.deactivationCount} ×`;
  });
  const titleChanged = () => { void view.setTitle(title.value).catch(report); };
  const bodyChanged = () => { void view.setBody(body.value).catch(report); };
  const saveClicked = () => { void view.save().catch(report); };
  title.addEventListener("change", titleChanged);
  body.addEventListener("change", bodyChanged);
  save.addEventListener("click", saveClicked);
  return () => {
    title.removeEventListener("change", titleChanged); body.removeEventListener("change", bodyChanged);
    save.removeEventListener("click", saveClicked); unsubscribe(); view.dispose(); host.replaceChildren();
  };
}
