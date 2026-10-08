// Navigation commands are unavailable while a navigation is in flight, and an
// asynchronous command is unavailable while it runs, so the button that started
// one is disabled under the user's focus. The browser then moves focus to the
// body. These helpers give focus back once the control is enabled again, unless
// focus moved somewhere else in the meantime.

// The element that last received focus.
let lastFocused: HTMLElement | null = null;
// A control that lost focus because it was disabled, waiting to be enabled again.
let returnTo: HTMLElement | null = null;
let installed = false;

const focusIsLost = () => document.activeElement === null || document.activeElement === document.body;
const isDisabled = (element: Element) => element.hasAttribute("disabled");

/**
 * Returns focus to any control that loses it by being disabled, once that
 * control is enabled again. Call it once when the app starts.
 */
export function keepFocusAcrossDisabling(): void {
  if (installed || typeof document === "undefined") return;
  installed = true;
  document.addEventListener("focusin", event => {
    if (!(event.target instanceof HTMLElement)) return;
    lastFocused = event.target;
    returnTo = null; // focus moved on; nothing to return to
  }, true);
  // Focus that leaves a control still enabled, such as a click on a blank area, is not
  // lost to disabling, so a later disable and enable must not take it back. A focusout
  // also fires when the whole window loses focus, for example to another application or a
  // native dialog. The control then still holds focus and nothing took it, so it stays
  // remembered: a navigation may disable it while the window is in the background.
  document.addEventListener("focusout", event => {
    if (!(event.target instanceof HTMLElement) || isDisabled(event.target)) return;
    if (!document.hasFocus() || (event.relatedTarget === null && document.activeElement === event.target)) return;
    if (event.target === lastFocused) lastFocused = null;
    if (event.target === returnTo) returnTo = null;
  }, true);
  // One observer for the whole document, so nothing outlives the elements it watched.
  new MutationObserver(records => {
    for (const { target } of records) {
      if (!(target instanceof HTMLElement)) continue;
      if (isDisabled(target)) {
        // The browser may move focus to the body only after this callback runs.
        if (target === lastFocused && (document.activeElement === target || focusIsLost())) returnTo = target;
      } else if (target === returnTo) {
        returnTo = null;
        if (target.isConnected && focusIsLost()) target.focus();
      }
    }
    if (returnTo && !returnTo.isConnected) returnTo = null;
  }).observe(document.documentElement, { subtree: true, attributes: true, attributeFilter: ["disabled"] });
}

/** Returns the element a dialog should give focus back to when it closes. */
export function focusOrigin(): HTMLElement | null {
  const active = document.activeElement;
  if (active instanceof HTMLElement && active !== document.body) return active;
  return lastFocused?.isConnected ? lastFocused : null;
}

/**
 * Returns focus to the element that opened a dialog. If that control is still
 * disabled, for example because the navigation it started is still in flight,
 * focus returns once it is enabled again.
 */
export function restoreFocus(element: HTMLElement | null): void {
  keepFocusAcrossDisabling();
  if (!element?.isConnected) return;
  element.focus();
  if (document.activeElement !== element && isDisabled(element)) returnTo = element;
}
