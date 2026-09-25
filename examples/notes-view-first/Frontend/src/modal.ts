import type { ConfirmNavigationView } from "./generated/confirmNavigation.js";

export function mountConfirmNavigation(host: HTMLElement, dialog: ConfirmNavigationView): () => void {
  const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  host.innerHTML = `<section class="dialog" role="dialog" aria-modal="true" aria-labelledby="dialog-title"><h2 id="dialog-title">Unsaved changes</h2><p data-message></p><div class="dialog-actions"><button data-cancel>Keep editing</button><button data-confirm>Discard changes</button></div></section>`;
  const message = host.querySelector<HTMLElement>("[data-message]")!;
  const cancel = host.querySelector<HTMLButtonElement>("[data-cancel]")!;
  const confirm = host.querySelector<HTMLButtonElement>("[data-confirm]")!;
  let active = true;
  const unsubscribe = dialog.subscribe(state => {
    message.textContent = state.message;
    cancel.disabled = !state.canCancel;
    confirm.disabled = !state.canConfirm;
  });
  cancel.focus();
  const showError = (cause: unknown) => {
    if (active) { const status = document.querySelector("#status")!; status.textContent = String(cause); status.classList.add("error"); }
  };
  const cancelClicked = () => { void dialog.cancel().catch(showError); };
  const confirmClicked = () => { void dialog.confirm().catch(showError); };
  const keydown = (event: KeyboardEvent) => {
    if (event.key === "Escape") { event.preventDefault(); cancelClicked(); }
    if (event.key === "Tab") {
      const target = document.activeElement;
      if (event.shiftKey && target === cancel) { event.preventDefault(); confirm.focus(); }
      else if (!event.shiftKey && target === confirm) { event.preventDefault(); cancel.focus(); }
    }
  };
  cancel.addEventListener("click", cancelClicked);
  confirm.addEventListener("click", confirmClicked);
  host.addEventListener("keydown", keydown);
  return () => {
    active = false;
    cancel.removeEventListener("click", cancelClicked);
    confirm.removeEventListener("click", confirmClicked);
    host.removeEventListener("keydown", keydown);
    unsubscribe();
    dialog.dispose();
    host.replaceChildren();
    previousFocus?.focus();
  };
}
