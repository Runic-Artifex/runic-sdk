import type { PreviewView } from "./generated/preview.js";

export function mountPreview(host: HTMLElement, view: PreviewView): () => void {
  host.innerHTML = `<h2 data-heading></h2><p data-body></p>`;
  const heading = host.querySelector<HTMLElement>("[data-heading]")!;
  const body = host.querySelector<HTMLElement>("[data-body]")!;
  const unsubscribe = view.subscribe(state => { heading.textContent = state.heading; body.textContent = state.body; });
  return () => { unsubscribe(); view.dispose(); host.replaceChildren(); };
}
