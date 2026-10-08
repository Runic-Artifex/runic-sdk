import { mountContent, type ViewTemplates } from "./content.js";
import type { DocumentClient, DocumentPane, DocumentState } from "./generated/document.js";
import { mountEditor } from "./editor.js";
import { mountPreview } from "./preview.js";

const paneViews = {
  editor: mountEditor,
  preview: mountPreview,
} satisfies ViewTemplates<NonNullable<DocumentState["currentPane"]>>;

export function mountDocument(host: HTMLElement, documentView: DocumentClient): () => void {
  host.innerHTML = `<h1>Document</h1><p class="muted">This area has its own ViewModel and a nested Editor/Preview outlet.</p><div class="tabs"><button data-pane="editor">Editor</button><button data-pane="preview">Preview</button></div><section id="document-pane" class="card"></section>`;
  const editor = host.querySelector<HTMLButtonElement>("[data-pane=editor]")!;
  const preview = host.querySelector<HTMLButtonElement>("[data-pane=preview]")!;
  const pane = host.querySelector<HTMLElement>("#document-pane")!;
  // DocumentPane is a literal union of the C# enum cases, so this switch is
  // exhaustive: a new case is a compile error here.
  const paneButton = (active: DocumentPane): HTMLButtonElement => {
    switch (active) {
      case "Editor": return editor;
      case "Preview": return preview;
      default: { const unknown: never = active; throw new Error(`Unknown document pane ${String(unknown)}.`); }
    }
  };
  const unmountPane = mountContent(pane, documentView, "currentPane", paneViews, error => {
    const status = document.querySelector("#status")!;
    status.textContent = String(error);
    status.classList.add("error");
  });
  const unsubscribe = documentView.subscribe(state => {
    const current = paneButton(state.activePane);
    editor.setAttribute("aria-current", current === editor ? "page" : "false");
    preview.setAttribute("aria-current", current === preview ? "page" : "false");
    editor.disabled = !state.canShowEditor;
    preview.disabled = !state.canShowPreview;
  });
  const showEditor = () => { void documentView.showEditor(); };
  const showPreview = () => { void documentView.showPreview(); };
  editor.addEventListener("click", showEditor);
  preview.addEventListener("click", showPreview);
  return () => {
    editor.removeEventListener("click", showEditor);
    preview.removeEventListener("click", showPreview);
    unsubscribe();
    unmountPane();
    documentView.dispose();
    host.replaceChildren();
  };
}
