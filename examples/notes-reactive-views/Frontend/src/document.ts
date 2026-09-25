import { mountContent, type ViewTemplates } from "./content.js";
import type { DocumentState, DocumentView } from "./generated/document.js";
import { mountEditor } from "./editor.js";
import { mountCompact } from "./compact.js";
import { mountPreview } from "./preview.js";

const paneViews = { editor: mountEditor, preview: mountPreview } satisfies ViewTemplates<DocumentState["currentPane"]>;
const compactViews = { editorCompact: mountCompact } satisfies ViewTemplates<DocumentState["compactNote"]>;

export function mountDocument(host: HTMLElement, view: DocumentView): () => void {
  host.innerHTML = `<h1>Document</h1><p class="muted">The nested router swaps Editor and Preview. The compact View stays mounted beside it.</p><div class="document"><div><div class="tabs"><button data-pane="editor">Editor</button><button data-pane="preview">Preview</button></div><section id="document-pane" class="card"></section></div><aside id="compact-pane" class="card"></aside></div>`;
  const editorButton = host.querySelector<HTMLButtonElement>("[data-pane=editor]")!;
  const previewButton = host.querySelector<HTMLButtonElement>("[data-pane=preview]")!;
  const pane = host.querySelector<HTMLElement>("#document-pane")!;
  const compact = host.querySelector<HTMLElement>("#compact-pane")!;
  const report = (error: unknown) => { const status = document.querySelector<HTMLElement>("#status")!; status.textContent = String(error); status.classList.add("error"); };
  const unmountPane = mountContent(pane, view, "currentPane", paneViews, report);
  const unmountCompact = mountContent(compact, view, "compactNote", compactViews, report);
  const unsubscribe = view.subscribe(state => {
    editorButton.setAttribute("aria-current", state.activePane === "Editor" ? "page" : "false");
    previewButton.setAttribute("aria-current", state.activePane === "Preview" ? "page" : "false");
  });
  const showEditor = () => { void view.showEditor().catch(report); };
  const showPreview = () => { void view.showPreview().catch(report); };
  editorButton.addEventListener("click", showEditor);
  previewButton.addEventListener("click", showPreview);
  return () => {
    editorButton.removeEventListener("click", showEditor);
    previewButton.removeEventListener("click", showPreview);
    unsubscribe(); unmountPane(); unmountCompact(); view.dispose(); host.replaceChildren();
  };
}
