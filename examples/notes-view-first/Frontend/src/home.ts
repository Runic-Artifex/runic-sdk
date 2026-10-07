import type { HomeClient, SavedNote } from "./generated/home.js";

export function mountHome(host: HTMLElement, home: HomeClient): () => void {
  host.innerHTML = `<div class="card"><h1></h1><p>Choose Notes in the independent sidebar to open your document.</p><ul data-recent></ul></div>`;
  const title = host.querySelector("h1")!;
  const recent = host.querySelector<HTMLUListElement>("[data-recent]")!;
  // Saved notes arrive as keyed collection changes, which keep unchanged rows
  // as the same objects, so their list items are reused.
  const rows = new Map<SavedNote, HTMLLIElement>();
  const unsubscribe = home.subscribe(state => {
    title.textContent = state.greeting;
    const items = state.recentNotes.map(note => {
      let item = rows.get(note);
      if (!item) {
        item = document.createElement("li");
        item.textContent = `${note.title}: ${note.excerpt}`;
      }
      return [note, item] as const;
    });
    rows.clear();
    for (const [note, item] of items) rows.set(note, item);
    recent.replaceChildren(...items.map(([, item]) => item));
  });
  return () => { unsubscribe(); home.dispose(); host.replaceChildren(); };
}
