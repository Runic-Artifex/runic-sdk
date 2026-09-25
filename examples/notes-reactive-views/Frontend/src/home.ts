import type { HomeView } from "./generated/home.js";

export function mountHome(host: HTMLElement, view: HomeView): () => void {
  host.innerHTML = `<h1>Reactive Notes</h1><p>Open the document to compare its full editor and compact view. They share one ViewModel.</p>`;
  const unsubscribe = view.subscribe(state => { host.querySelector("h1")!.textContent = state.greeting; });
  return () => { unsubscribe(); view.dispose(); host.replaceChildren(); };
}
