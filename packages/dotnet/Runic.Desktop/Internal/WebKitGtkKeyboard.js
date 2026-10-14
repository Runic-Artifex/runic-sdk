// Browser keyboard behaviour that WebKitGTK leaves to its embedder. Runic Desktop's
// GTK 3 host and GTK 4 provider add this script to the top frame at document start,
// in its own script world, so the page neither sees nor changes its state. Only
// trusted (user) events act. The variable is the script's interface for tests.
//
// Tab: WebKitGTK cannot give a page focus from one end. When Tab leaves the last
// control, WebKit clears the focused element and hands focus to the GTK window, which
// has nothing else to focus: a silent stop for screen readers. When a Tab or
// Shift+Tab that the page did not cancel clears the focus this way, the script
// focuses the first or last control, as a browser window does.
//
// Wheel: WebKit scrolls on Control and the wheel, so the host cannot wait for the
// page to leave the event unhandled, as it does for keys. While Control is held, the
// script cancels a wheel event that the page did not cancel and asks the host to
// zoom. The cancelling listener exists only while Control is held: a non-passive
// wheel listener makes WebKit wait for the page before it scrolls.
"use strict";
var runicDesktopKeyboard = (() => {
  // Focus attempts per wrap; a target that cannot take focus moves on to the next.
  const maxAttempts = 8;
  let tab = null;
  let watching = false;
  let wheel = 0;

  addEventListener("keydown", event => {
    if (!event.isTrusted) return;
    if (event.key === "Control") {
      watchWheel(true);
      return;
    }
    // Some keymaps give Shift+Tab no key name; WebKit still reports the Tab key code.
    if ((event.key !== "Tab" && event.keyCode !== 9) || event.ctrlKey || event.altKey || event.metaKey) return;
    tab = event;
    // WebKit moves focus in the default action of the same keydown.
    setTimeout(() => { if (tab === event) tab = null; });
  }, true);
  addEventListener("keyup", event => {
    if (event.isTrusted && event.key === "Control") watchWheel(false);
  }, true);
  addEventListener("blur", event => {
    if (!event.isTrusted || event.target !== window) return;
    watchWheel(false);
    // On a page with no focused control, only the window loses focus.
    leaving();
  }, true);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) watchWheel(false);
  });
  // Control may have been pressed while another window had focus.
  addEventListener("wheel", event => {
    if (event.isTrusted && event.ctrlKey) watchWheel(true);
  }, { capture: true, passive: true });
  addEventListener("focusout", event => {
    if (event.isTrusted && event.relatedTarget === null) leaving();
  }, true);

  // WebKit clears the focus when a Tab that the page did not cancel leaves the page.
  function leaving() {
    const pending = tab;
    if (!pending || pending.defaultPrevented) return;
    tab = null;
    setTimeout(() => wrap(pending.shiftKey));
  }

  function watchWheel(on) {
    if (on === watching) return;
    watching = on;
    wheel = 0;
    if (on) addEventListener("wheel", zoomWheel, { passive: false });
    else removeEventListener("wheel", zoomWheel, { passive: false });
  }

  // Wheels report a whole step per notch; touchpads report small steps, which add up.
  function zoomWheel(event) {
    if (!event.isTrusted) return;
    if (!event.ctrlKey) {
      watchWheel(false);
      return;
    }
    if (event.altKey || event.metaKey || event.defaultPrevented || !event.deltaY) return;
    event.preventDefault();
    const step = event.deltaMode === WheelEvent.DOM_DELTA_PIXEL ? event.deltaY / 40 : event.deltaY;
    if (step * wheel < 0) wheel = 0;
    wheel += step;
    if (Math.abs(wheel) < 1) return;
    const message = wheel < 0 ? 1 : -1;
    wheel = 0;
    window.webkit?.messageHandlers?.runicZoom?.postMessage(message);
  }

  // Focuses the first control, or the last one backward. A target that takes focus,
  // or whose focus handler moves focus elsewhere, as a focus trap does, ends the wrap.
  // Without a control that takes focus, the document itself keeps focus, as before Tab.
  function wrap(backward) {
    if (!unfocused()) return;
    const order = sequence();
    if (backward) order.reverse();
    for (const element of order.slice(0, maxAttempts)) {
      (checkedRadio(element) ?? element).focus({ focusVisible: true });
      if (!unfocused()) return;
    }
    window.focus();
  }

  function unfocused() {
    const active = document.activeElement;
    return !active || active === document.body || active === document.documentElement;
  }

  // The sequential focus order of the HTML standard: in each focus navigation scope,
  // positive tabindex values ascending, then the rest in tree order. A shadow root or
  // a slot is its own scope, which follows its host or slot in place. An open modal
  // dialog is the only scope that can take focus. Frames are left out: their content
  // belongs to another document.
  function sequence() {
    const entries = [];
    add(modalDialog() ?? document.documentElement, entries);
    return flatten(entries);
  }

  function modalDialog() {
    try {
      return [...document.querySelectorAll("dialog")].filter(dialog => dialog.matches(":modal")).at(-1) ?? null;
    } catch {
      return null;
    }
  }

  function add(element, entries) {
    if (element.inert || element.hasAttribute("inert")) return;
    const entry = { element, scope: null };
    entries.push(entry);
    if (element.shadowRoot) {
      entry.scope = [];
      for (const child of element.shadowRoot.children) add(child, entry.scope);
    } else if (element instanceof HTMLSlotElement) {
      entry.scope = [];
      const assigned = element.assignedElements();
      for (const child of assigned.length ? assigned : element.children) add(child, entry.scope);
    } else {
      for (const child of element.children) add(child, entries);
    }
  }

  function flatten(entries) {
    const positive = entries.filter(entry => index(entry.element) > 0);
    positive.sort((left, right) => index(left.element) - index(right.element));
    const ordered = positive.concat(entries.filter(entry => !(index(entry.element) > 0)));
    const elements = [];
    for (const entry of ordered) {
      if (tabbable(entry.element)) elements.push(entry.element);
      if (entry.scope) elements.push(...flatten(entry.scope));
    }
    return elements;
  }

  // An editing host is a tab stop although its tabIndex property is -1.
  function index(element) {
    if (element.hasAttribute("tabindex") || !element.isContentEditable) return element.tabIndex;
    return element.parentElement?.isContentEditable ? -1 : 0;
  }

  function tabbable(element) {
    if (index(element) < 0 || element.matches(":disabled")) return false;
    if (/^(IFRAME|FRAME|OBJECT|EMBED)$/.test(element.tagName)) return false;
    if (typeof element.checkVisibility === "function")
      return element.checkVisibility({ visibilityProperty: true });
    return element.getClientRects().length > 0;
  }

  // Tab enters a radio group at its checked button.
  function checkedRadio(element) {
    if (!(element instanceof HTMLInputElement) || element.type !== "radio" || !element.name || element.checked) return null;
    const scope = element.form ?? element.getRootNode();
    for (const radio of scope.querySelectorAll("input[type=radio]")) {
      if (radio.name === element.name && radio.checked && radio.form === element.form && tabbable(radio)) return radio;
    }
    return null;
  }

  return { sequence, wrap };
})();
