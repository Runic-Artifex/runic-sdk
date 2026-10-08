// The element that last received focus. A focused button that becomes disabled
// loses focus to the body, which happens to the sidebar's Home button when its
// navigation opens the confirm dialog, so the dialog may mount after focus has
// already left its trigger.
let lastFocused: HTMLElement | null = null;
if (typeof document !== "undefined") {
  document.addEventListener("focusin", event => {
    if (event.target instanceof HTMLElement) lastFocused = event.target;
  }, true);
}

/** Returns the element a dialog should give focus back to when it closes. */
export function focusOrigin(): HTMLElement | null {
  const active = document.activeElement;
  if (active instanceof HTMLElement && active !== document.body) return active;
  return lastFocused?.isConnected ? lastFocused : null;
}

/**
 * Returns focus to the element that opened a dialog. The sidebar's buttons are
 * unavailable while the navigation they started is still in flight, so the
 * trigger can still be disabled when the dialog closes. Focus then returns once
 * it is enabled again, unless focus moved elsewhere in the meantime.
 */
export function restoreFocus(element: HTMLElement | null): void {
  if (!element?.isConnected) return;
  element.focus();
  if (document.activeElement === element || !(element instanceof HTMLButtonElement) || !element.disabled) return;
  const observer = new MutationObserver(() => {
    if (!element.isConnected) { observer.disconnect(); return; }
    if (element.disabled) return;
    observer.disconnect();
    if (document.activeElement === null || document.activeElement === document.body) element.focus();
  });
  observer.observe(element, { attributes: true, attributeFilter: ["disabled"] });
}
