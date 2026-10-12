import type { HomeClient } from "./generated/home.js";

export function mountHome(host: HTMLElement, view: HomeClient): () => void {
  host.innerHTML = `<h1>Reactive Notes</h1><p>Open the document to compare its full editor and compact view. They share one ViewModel.</p><p data-last-result class="muted"></p>`;
  const lastResult = host.querySelector<HTMLElement>("[data-last-result]")!;
  const unsubscribe = view.subscribe(state => {
    host.querySelector("h1")!.textContent = state.greeting;
    lastResult.textContent = state.lastResult;
  });
  return () => { unsubscribe(); view.dispose(); host.replaceChildren(); };
}
