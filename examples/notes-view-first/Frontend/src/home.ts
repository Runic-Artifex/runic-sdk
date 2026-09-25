import type { HomeView } from "./generated/home.js";

export function mountHome(host: HTMLElement, home: HomeView): () => void {
  host.innerHTML = `<div class="card"><h1></h1><p>Choose Notes in the independent sidebar to open your document.</p></div>`;
  const title = host.querySelector("h1")!;
  const unsubscribe = home.subscribe(state => { title.textContent = state.greeting; });
  return () => { unsubscribe(); home.dispose(); host.replaceChildren(); };
}
