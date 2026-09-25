import type { SidebarView } from "./generated/sidebar.js";

export function mountSidebar(host: HTMLElement, sidebar: SidebarView): () => void {
  host.innerHTML = `<h2>Workspace</h2><nav aria-label="Notes navigation"><button data-go="home">Home</button><button data-go="notes">Notes</button></nav><p class="muted">This sidebar owns a separate .NET ViewModel.</p>`;
  const home = host.querySelector<HTMLButtonElement>("[data-go=home]")!;
  const notes = host.querySelector<HTMLButtonElement>("[data-go=notes]")!;
  let active = true;
  const showError = (error: unknown) => {
    if (active) { const status = document.querySelector("#status")!; status.textContent = String(error); status.classList.add("error"); }
  };
  const unsubscribe = sidebar.subscribe(state => {
    home.setAttribute("aria-current", state.selected === "Home" ? "page" : "false");
    notes.setAttribute("aria-current", state.selected === "Notes" ? "page" : "false");
    home.disabled = !state.canOpenHome;
    notes.disabled = !state.canOpenNotes;
  });
  const goHome = () => { void sidebar.openHome().catch(showError); };
  const goNotes = () => { void sidebar.openNotes().catch(showError); };
  home.addEventListener("click", goHome);
  notes.addEventListener("click", goNotes);
  return () => {
    active = false;
    home.removeEventListener("click", goHome);
    notes.removeEventListener("click", goNotes);
    unsubscribe();
    sidebar.dispose();
    host.replaceChildren();
  };
}
