import { mountContent, type ViewTemplates } from "./content.js";
import type { DocumentClient, DocumentPane, DocumentState } from "./generated/document.js";
import { mountEditor } from "./editor.js";
import { mountCompact } from "./compact.js";
import { mountPreview } from "./preview.js";
import { describeSaveFailure } from "./save-failure.js";

const paneViews = { editor: mountEditor, preview: mountPreview } satisfies ViewTemplates<NonNullable<DocumentState["currentPane"]>>;
const compactViews = { editorCompact: mountCompact } satisfies ViewTemplates<DocumentState["compactNote"]>;

export function mountDocument(host: HTMLElement, view: DocumentClient): () => void {
  host.innerHTML = `<h1>Document</h1><p class="muted">A child region swaps Editor and Preview. The compact View stays mounted beside it.</p><div class="document"><div><div class="tabs"><button data-pane="editor">Editor</button><button data-pane="preview">Preview</button></div><section id="document-pane" class="card"></section><div class="tabs"><button data-save-close>Save and close</button><button data-close>Close</button></div><p data-document-status class="muted" role="status"></p><p data-close-failure role="alert" hidden></p></div><aside id="compact-pane" class="card"></aside></div>`;
  const editorButton = host.querySelector<HTMLButtonElement>("[data-pane=editor]")!;
  const previewButton = host.querySelector<HTMLButtonElement>("[data-pane=preview]")!;
  const saveCloseButton = host.querySelector<HTMLButtonElement>("[data-save-close]")!;
  const closeButton = host.querySelector<HTMLButtonElement>("[data-close]")!;
  const status = host.querySelector<HTMLElement>("[data-document-status]")!;
  const failure = host.querySelector<HTMLElement>("[data-close-failure]")!;
  const pane = host.querySelector<HTMLElement>("#document-pane")!;
  const compact = host.querySelector<HTMLElement>("#compact-pane")!;
  // DocumentPane is a literal union of the C# enum cases, so this switch is
  // exhaustive: a new case is a compile error here.
  const paneButton = (active: DocumentPane): HTMLButtonElement => {
    switch (active) {
      case "Editor": return editorButton;
      case "Preview": return previewButton;
      default: { const unknown: never = active; throw new Error(`Unknown document pane ${String(unknown)}.`); }
    }
  };
  const report = (error: unknown) => { const status = document.querySelector<HTMLElement>("#status")!; status.textContent = String(error); status.classList.add("error"); };
  // The leave guard asks here when this window leaves the document with unsaved
  // edits: its Close or the shell's Home (Back).
  // Answering false keeps the document and its edits.
  const removeLeaveHandler = view.interactions.confirmLeave.handle(async (request, { signal }) => {
    if (signal.aborted) throw signal.reason;
    return window.confirm(`Close “${request.title}” and lose its unsaved changes?`);
  });
  const unmountPane = mountContent(pane, view, "currentPane", paneViews, report);
  const unmountCompact = mountContent(compact, view, "compactNote", compactViews, report);
  const unsubscribe = view.subscribe(state => {
    const current = paneButton(state.activePane);
    editorButton.setAttribute("aria-current", current === editorButton ? "page" : "false");
    previewButton.setAttribute("aria-current", current === previewButton ? "page" : "false");
    status.textContent = state.status;
  });
  const showFailure = (text: string | undefined) => { failure.textContent = text ?? ""; failure.hidden = text === undefined; };
  const showEditor = () => { void view.showEditor().catch(report); };
  const showPreview = () => { void view.showPreview().catch(report); };
  // Save and close declares Save's failure; a committed close unmounts this View.
  const saveAndClose = () => {
    showFailure(undefined);
    void view.saveAndClose().then(outcome => { if (!outcome.ok) showFailure(describeSaveFailure(outcome.failure)); }, report);
  };
  const close = () => { void view.close().catch(report); };
  editorButton.addEventListener("click", showEditor);
  previewButton.addEventListener("click", showPreview);
  saveCloseButton.addEventListener("click", saveAndClose);
  closeButton.addEventListener("click", close);
  return () => {
    editorButton.removeEventListener("click", showEditor);
    previewButton.removeEventListener("click", showPreview);
    saveCloseButton.removeEventListener("click", saveAndClose);
    closeButton.removeEventListener("click", close);
    removeLeaveHandler(); unsubscribe(); unmountPane(); unmountCompact(); view.dispose(); host.replaceChildren();
  };
}
