// Custom object formatters (#1042), in the development build only: the browser console and the Sources panel show a
// View client as `EditorViewModel · editor · rev 12`, a state as `EditorViewModel state · rev 12 {title: "Draft", …}`,
// and a field's state as `title = "Draft" · editing`, each with a body that expands into its parts, instead of the
// client's closures and internals. Chromium (an installed browser, WebView2) and Firefox apply
// `globalThis.devtoolsFormatters` once custom formatters are enabled in their DevTools settings; WebKit ignores them.
// The first client this copy of the package connects adds one formatter to the array; the production build does not
// contain this module.
import { describe, viewName, type ViewDescription } from "./describe.js";

type JsonML = readonly unknown[];
interface Formatter {
  header(value: unknown, config?: unknown): JsonML | null;
  hasBody(value: unknown, config?: unknown): boolean;
  body(value: unknown, config?: unknown): JsonML | null;
}

interface SnapshotInfo { readonly route: string; readonly contract: string; readonly revision: number | undefined }
interface FieldInfo { readonly name: string | undefined }

// What this copy's development build delivered: each exposed state and each field state, with what names them.
const snapshots = new WeakMap<object, SnapshotInfo>();
const fields = new WeakMap<object, FieldInfo>();
let installed = false;

// The DevTools of Chromium and Firefox render the inline styles of JsonML; the colours follow Chromium's console.
const styles = {
  header: "font-weight: bold",
  name: "color: #6d28d9; font-weight: bold",
  muted: "color: #6b7280",
  key: "color: #881391",
  string: "color: #c41a16",
  value: "color: #1c00cf",
  list: "list-style: none; margin: 0; padding: 0 0 0 12px",
};
// Marks a child object rendered without the formatter (`raw`), so its own internals stay reachable.
const rawConfig = { runicRaw: true };

/** @internal Remembers an exposed state of a route, for the console. */
export function markSnapshot(state: object, route: string, contract: string, revision: number | undefined): void {
  installFormatters();
  snapshots.set(state, { route, contract, revision });
}

/** @internal Remembers a field's state, for the console. */
export function markFieldState(state: object, name: string | undefined): void {
  installFormatters();
  fields.set(state, { name });
}

/** @internal Adds this copy's formatter to `globalThis.devtoolsFormatters` once. */
export function installFormatters(): void {
  if (installed) return;
  installed = true;
  const host = globalThis as { devtoolsFormatters?: unknown };
  try {
    if (!Array.isArray(host.devtoolsFormatters)) host.devtoolsFormatters = [];
    (host.devtoolsFormatters as Formatter[]).push(formatter);
  } catch { /* A frozen global: no formatter. */ }
}

function preview(value: unknown): JsonML {
  if (typeof value === "string") {
    const text = value.length > 40 ? `${value.slice(0, 39)}…` : value;
    return ["span", { style: styles.string }, JSON.stringify(text)];
  }
  if (value === null || typeof value !== "object") return ["span", { style: styles.value }, typeof value === "function" ? "ƒ" : String(value)];
  if (Array.isArray(value)) return ["span", {}, `Array(${value.length})`];
  return ["span", {}, "{…}"];
}

// A value in a body: a primitive inline, an object that expands (through this formatter when it is one of ours).
function child(value: unknown): JsonML {
  return value !== null && (typeof value === "object" || typeof value === "function") ? ["object", { object: value }] : preview(value);
}

function row(key: string, value: unknown): JsonML {
  return ["li", {}, ["span", { style: styles.key }, key], ": ", child(value)];
}

function list(rows: readonly JsonML[]): JsonML {
  return ["ol", { style: styles.list }, ...rows];
}

function viewHeader(view: ViewDescription): JsonML {
  return ["span", { style: styles.header },
    ["span", { style: styles.name }, view.view], ` · ${view.route}`,
    ...view.revision === undefined ? [] : [["span", { style: styles.muted }, ` · rev ${view.revision}`]],
    ...view.released ? [["span", { style: styles.muted }, " · released"]] : []];
}

function stateHeader(state: object, info: SnapshotInfo): JsonML {
  const entries = Object.entries(state);
  const shown = entries.slice(0, 3).flatMap(([key, value], index) => [index === 0 ? "" : ", ", ["span", { style: styles.key }, key], ": ", preview(value)]);
  return ["span", {},
    ["span", { style: styles.name }, viewName(info.contract, info.route)], " state",
    ...info.revision === undefined ? [] : [["span", { style: styles.muted }, ` · rev ${info.revision}`]],
    " {", ...shown, entries.length > 3 ? ", …}" : "}"];
}

function fieldHeader(state: Record<string, unknown>, info: FieldInfo): JsonML {
  const parts: unknown[] = [];
  if (state["editing"] === true) parts.push(" · editing");
  if (state["validating"] === true) parts.push(" · validating");
  if (state["conflict"] !== undefined) parts.push(" · conflict");
  if (state["error"] !== undefined) parts.push(" · failed");
  const errors = Array.isArray(state["errors"]) ? state["errors"].length : 0;
  if (errors > 0) parts.push(errors === 1 ? " · 1 error" : ` · ${errors} errors`);
  return ["span", {}, ["span", { style: styles.name }, info.name ?? "field"], " = ", preview(state["value"]),
    ["span", { style: styles.muted }, ...parts]];
}

const formatter: Formatter = {
  header(value, config) {
    if (config === rawConfig || value === null || typeof value !== "object") return null;
    try {
      const view = describe(value);
      if (view) return viewHeader(view);
      const snapshot = snapshots.get(value);
      if (snapshot) return stateHeader(value, snapshot);
      const field = fields.get(value);
      if (field) return fieldHeader(value as Record<string, unknown>, field);
    } catch { /* Let DevTools show the object itself. */ }
    return null;
  },
  hasBody: () => true,
  body(value) {
    const object = value as object;
    const view = describe(object);
    if (view) {
      return list([
        row("view model", view.view), row("route", view.route), row("contract", view.contract),
        row("revision", view.revision), row("presentations", view.presentations), row("released", view.released),
        row("state", (object as { snapshot?: unknown }).snapshot),
        ["li", {}, ["span", { style: styles.muted }, "client"], ": ", ["object", { object, config: rawConfig }]],
      ]);
    }
    const snapshot = snapshots.get(object);
    if (snapshot) {
      return list([
        ...Object.entries(object).map(([key, member]) => row(key, member)),
        ["li", {}, ["span", { style: styles.muted }, `route ${snapshot.route}, contract ${snapshot.contract}`]],
      ]);
    }
    return list(Object.entries(object).map(([key, member]) => row(key, member)));
  },
};
