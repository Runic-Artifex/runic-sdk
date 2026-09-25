import type { PreviewView } from "./generated/preview.js";

export function mountPreview(host: HTMLElement, preview: PreviewView): () => void {
  host.innerHTML = `<h2></h2><p></p>`;
  const heading = host.querySelector("h2")!;
  const excerpt = host.querySelector("p")!;
  const unsubscribe = preview.subscribe(state => {
    heading.textContent = state.heading;
    excerpt.textContent = state.excerpt;
  });
  return () => { unsubscribe(); preview.dispose(); host.replaceChildren(); };
}
