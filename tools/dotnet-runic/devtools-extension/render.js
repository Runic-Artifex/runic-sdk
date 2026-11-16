// DOM helpers of the panel and the sidebar. Everything from the page goes in as text, never as markup: an extension
// page has more rights than the page it inspects.

/** Creates an element with properties (`className`, `title`, `onclick`, …) and text or element children. */
export function h(tag, properties = {}, ...children) {
  const element = document.createElement(tag);
  for (const [name, value] of Object.entries(properties)) {
    if (value === undefined || value === null || value === false) continue;
    if (name.startsWith("on") && typeof value === "function") element.addEventListener(name.slice(2), value);
    else if (name === "className") element.className = value;
    else if (name === "dataset") Object.assign(element.dataset, value);
    else element.setAttribute(name, value === true ? "" : String(value));
  }
  for (const child of children.flat()) {
    if (child === undefined || child === null || child === false || child === "") continue;
    element.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return element;
}

/**
 * A link that opens a location in the editor through the development server, as Runic Inspect's links do; a plain
 * label when there is no location or no opener.
 */
export function locationLink(location, label, open) {
  if (location === undefined || location === null) return h("span", { className: "muted" }, label);
  if (typeof open !== "function") return h("span", { className: "location", title: location.file }, label);
  const link = h("a", {
    href: "#", className: "location", title: `Open ${location.file}:${location.line} in the editor`,
    onclick: async (event) => {
      event.preventDefault();
      link.classList.remove("failed");
      const opened = await open(location);
      if (!opened) {
        link.classList.add("failed");
        link.title = "The development server did not open it (another machine, or no editor configured).";
      }
    },
  }, label);
  return link;
}

/** Follows DevTools' light or dark theme. */
export function applyTheme() {
  const theme = globalThis.chrome?.devtools?.panels?.themeName;
  document.documentElement.dataset.theme = theme === "dark" ? "dark" : theme === "default" ? "light" : "";
}
