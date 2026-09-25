import type { EditorView } from "./generated/editor.js";

export function mountCompact(host: HTMLElement, view: EditorView): () => void {
  host.innerHTML = `<h2>Compact View</h2><p class="muted">Contract: compact</p><strong data-title></strong><p data-body></p><p data-activation class="muted"></p>`;
  const title = host.querySelector<HTMLElement>("[data-title]")!;
  const body = host.querySelector<HTMLElement>("[data-body]")!;
  const activation = host.querySelector<HTMLElement>("[data-activation]")!;
  const unsubscribe = view.subscribe(state => {
    title.textContent = state.title;
    body.textContent = state.body || "Nothing written yet.";
    activation.textContent = `Activated ${state.activationCount} × · deactivated ${state.deactivationCount} ×`;
  });
  return () => { unsubscribe(); view.dispose(); host.replaceChildren(); };
}
