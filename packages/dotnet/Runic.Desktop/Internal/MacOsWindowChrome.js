// Reports the page's drag regions to the macOS window (MacOsWindowChrome.cs), which tests a mouse-down against the
// last report in its own process and drags the window from it: no round trip while the button is down.
//
// A region is an element whose CSS sets app-region (or -webkit-app-region) to drag or no-drag, as in Electron, or the
// custom property --app-region. WebKit drops the app-region declarations it does not implement when it parses CSS, so
// the script copies them from the source of <style> elements, same-origin <link rel="stylesheet"> sheets and style
// attributes into --app-region rules of its own <style> element, and reads the computed --app-region of every element
// that a rule with it selects. Later regions in document order lie over earlier ones, as in Electron.
//
// Runs at document start in the main frame, in its own content world where WebKit offers one. Posts
// {"w": innerWidth, "r": [[1 for drag or 0 for no-drag, x, y, width, height], ...]} in CSS pixels of the viewport,
// whenever the regions change.
(() => {
  "use strict";
  const handler = globalThis.webkit?.messageHandlers?.runicWindowChrome;
  if (!handler) return;
  const post = handler.postMessage.bind(handler);

  const MaximumRegions = 512;
  const property = /(^|[^\w-])(?:-webkit-)?app-region(\s*:)/gi;
  const inline = /(?:^|;)\s*(?:-webkit-)?app-region\s*:\s*(drag|no-drag)\s*(?:!important\s*)?(?:;|$)/i;
  const mirrorAttribute = "data-runic-app-region";
  const fetched = new Map();
  let mirror = null;
  let selectors = null;
  let last = null;
  let scheduled = false;

  const kind = value => {
    const text = String(value ?? "").trim().toLowerCase();
    return text === "drag" ? 1 : text === "no-drag" ? 0 : -1;
  };

  // The --app-region rules of a style sheet's source, with the conditions of the group rules around them.
  const mirrorRules = rules => {
    let text = "";
    for (const rule of rules) {
      const nested = rule.cssRules ? mirrorRules(rule.cssRules) : "";
      if (rule.selectorText !== undefined) {
        const value = rule.style?.getPropertyValue("--app-region").trim();
        if (value || nested) text += `${rule.selectorText}{${value ? `--app-region:${value};` : ""}${nested}}`;
      } else if (nested) {
        const header = rule.cssText.slice(0, rule.cssText.indexOf("{")).trim();
        text += `${header}{${nested}}`;
      }
    }
    return text;
  };

  const mirrorSource = source => {
    if (!source || !/app-region/i.test(source)) return "";
    const rewritten = source.replace(property, "$1--app-region$2");
    try {
      const sheet = new CSSStyleSheet();
      sheet.replaceSync(rewritten);
      return mirrorRules(sheet.cssRules);
    } catch {
      return "";
    }
  };

  // Linked sheets of the document's origin, fetched once each; a sheet of another origin keeps what WebKit parsed.
  const linkedSource = link => {
    const href = link.href;
    if (!href) return "";
    let url;
    try { url = new URL(href, location.href); } catch { return ""; }
    if (url.origin !== location.origin) return "";
    const known = fetched.get(url.href);
    if (known !== undefined) return known ?? "";
    fetched.set(url.href, null);
    fetch(url.href, { credentials: "same-origin" })
      .then(response => (response.ok ? response.text() : ""))
      .catch(() => "")
      .then(text => { fetched.set(url.href, text); invalidate(); });
    return "";
  };

  const updateMirror = () => {
    let text = "";
    for (const node of document.querySelectorAll("style, link[rel~='stylesheet' i]")) {
      if (node === mirror || node.disabled) continue;
      text += node.localName === "style" ? mirrorSource(node.textContent) : mirrorSource(linkedSource(node));
    }
    if (!text) {
      mirror?.remove();
      mirror = null;
      return;
    }
    if (!mirror) {
      mirror = document.createElement("style");
      mirror.setAttribute(mirrorAttribute, "");
    }
    if (mirror.textContent !== text) mirror.textContent = text;
    // Last in the document, so the copied rules keep their order; they set nothing but --app-region.
    const parent = document.head ?? document.documentElement;
    if (parent && (mirror.parentNode !== parent || mirror.nextSibling)) parent.append(mirror);
  };

  // The selectors of every rule that sets --app-region, in the sheets the script can read.
  const collectSelectors = () => {
    const found = new Set();
    const visit = rules => {
      for (const rule of rules) {
        if (rule.selectorText !== undefined && rule.style?.getPropertyValue("--app-region").trim()) {
          for (const selector of splitSelectors(rule.selectorText)) found.add(selector);
        }
        if (rule.cssRules) visit(rule.cssRules);
      }
    };
    const sheets = [...document.styleSheets, ...(document.adoptedStyleSheets ?? [])];
    for (const sheet of sheets) {
      let rules;
      try { rules = sheet.cssRules; } catch { continue; }
      if (rules) visit(rules);
    }
    return [...found];
  };

  // A selector list split at its top-level commas, so one selector the engine rejects leaves the others.
  const splitSelectors = list => {
    const parts = [];
    let depth = 0, quote = "", start = 0;
    for (let index = 0; index < list.length; index++) {
      const character = list[index];
      if (quote) {
        if (character === "\\") index++;
        else if (character === quote) quote = "";
      } else if (character === "\"" || character === "'") quote = character;
      else if (character === "(" || character === "[") depth++;
      else if (character === ")" || character === "]") depth--;
      else if (character === "," && depth === 0) {
        parts.push(list.slice(start, index).trim());
        start = index + 1;
      }
    }
    parts.push(list.slice(start).trim());
    return parts.filter(part => part && !part.includes("::"));
  };

  const regions = () => {
    const elements = new Set(document.querySelectorAll("[style*='app-region' i]"));
    for (const selector of selectors) {
      try {
        for (const element of document.querySelectorAll(selector)) elements.add(element);
      } catch {
        // A selector this engine cannot match, such as one with a nesting selector.
      }
    }
    const ordered = [...elements].sort((first, second) =>
      first === second ? 0 : first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1);
    const result = [];
    for (const element of ordered) {
      const attribute = element.getAttribute("style");
      const declared = attribute ? inline.exec(attribute) : null;
      const value = declared ? kind(declared[1]) : kind(getComputedStyle(element).getPropertyValue("--app-region"));
      if (value < 0) continue;
      for (const rect of element.getClientRects()) {
        if (rect.width <= 0 || rect.height <= 0) continue;
        result.push([value, round(rect.left), round(rect.top), round(rect.width), round(rect.height)]);
        if (result.length >= MaximumRegions) return result;
      }
    }
    return result;
  };

  const round = value => Math.round(value * 100) / 100;

  const update = () => {
    scheduled = false;
    if (!document.documentElement) return;
    if (selectors === null) {
      updateMirror();
      selectors = collectSelectors();
    }
    const message = JSON.stringify({ w: round(window.innerWidth), r: regions() });
    if (message === last) return;
    last = message;
    post(message);
  };

  const schedule = () => {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(update);
  };

  // The style sheets changed: copy and collect again before the next report.
  const invalidate = () => {
    selectors = null;
    schedule();
  };

  const isStyleNode = node => node !== mirror && (node.localName === "style" || node.localName === "link");

  const observer = new MutationObserver(records => {
    let sheets = false;
    for (const record of records) {
      if (record.target === mirror || record.target.parentNode === mirror) continue;
      if (record.type === "characterData" ? record.target.parentNode?.localName === "style"
        : record.type === "childList" ? record.target.localName === "style" || [...record.addedNodes, ...record.removedNodes].some(isStyleNode)
        : isStyleNode(record.target)) {
        sheets = true;
      }
    }
    if (sheets) invalidate();
    else schedule();
  });

  const start = () => {
    observer.observe(document, { subtree: true, childList: true, attributes: true, characterData: true });
    if (typeof ResizeObserver === "function" && document.documentElement) {
      new ResizeObserver(schedule).observe(document.documentElement);
    }
    invalidate();
  };

  addEventListener("resize", schedule, { passive: true });
  addEventListener("scroll", schedule, { capture: true, passive: true });
  for (const name of ["transitionend", "animationend"]) addEventListener(name, schedule, { capture: true, passive: true });
  // A linked sheet finished loading; an image may have moved the layout.
  addEventListener("load", event => (event.target?.localName === "link" ? invalidate() : schedule()), { capture: true });
  if (document.readyState === "loading") addEventListener("DOMContentLoaded", start, { once: true });
  else start();
})();
