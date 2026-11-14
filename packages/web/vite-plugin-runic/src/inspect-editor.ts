// The "Edit" panel of Runic Inspect's overview (#957): plain DOM inside the overlay's shadow root, rendering the state of
// inspect-edit.ts. It edits a message's raw MF2 source per locale and saves it through Runic Translations' in-page
// editing API; the page then shows the new text in place, and the overview keeps its selection.
//
// Keyboard: Tab moves through the locale list, the text, Save and Revert; Ctrl+Enter (⌘+Enter) saves; Esc closes the
// panel (Runic Inspect's key handler hands it here) and returns the focus to the message's Edit button.

import {
  canSave, currentLocale, describeStructure, draftText, editReducer, isDirty, saveRequest, startEdit,
  type EditEvent, type EditReply, type EditState,
} from "./inspect-edit.js";
import type { RunicInspectLocation } from "./inspect-data.js";

const messagePath = "__runic-translations/message";

export const editorStyles = `
.editor { margin-top: 10px; padding-top: 8px; border-top: 1px solid #4b3f8f; }
.editor:focus { outline: none; }
.editor .head { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; }
.editor .row { display: flex; gap: 6px; align-items: center; margin: 6px 0; flex-wrap: wrap; }
.editor label { color: #b9b0ff; font-size: 11px; }
.editor select, .editor textarea, .editor button.action {
  font: 12px/1.4 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; color: #f4f2ff; background: #241f3d;
  border: 1px solid #4b3f8f; border-radius: 4px; }
.editor select { padding: 2px 4px; }
.editor textarea { width: 100%; min-height: 4.5em; resize: vertical; padding: 4px 6px; tab-size: 2; white-space: pre; }
.editor textarea[readonly] { opacity: 0.7; }
.editor button.action { cursor: pointer; padding: 2px 10px; }
.editor button.action.primary { background: #4b3f8f; color: #fff; }
.editor button.action:disabled { cursor: default; opacity: 0.5; }
.editor select:focus-visible, .editor textarea:focus-visible, .editor button:focus-visible { outline: 2px solid #8fd3ff; outline-offset: 1px; }
.editor .structure { color: #c8c3e6; }
.editor .status { min-height: 1.4em; margin: 4px 0; }
.editor .status.error { color: #ff9e9e; }
.editor .status.ok { color: #9ee6b0; }
.editor .diagnostics li { color: #ff9e9e; }
.editor .conflict { border: 1px solid #b38b2e; border-radius: 4px; padding: 6px; margin: 6px 0; }
.editor .conflict pre { margin: 4px 0; white-space: pre-wrap; overflow-wrap: anywhere;
  font: 11.5px/1.4 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; color: #ffd98e; }
.card button.edit { all: unset; cursor: pointer; color: #8fd3ff; text-decoration: underline; margin-left: 6px; }
.card button.edit:focus-visible { outline: 2px solid #8fd3ff; outline-offset: 1px; }
`;

export interface MessageEditorOptions {
  readonly base: string;
  /** A file:line link (or, for a remote client, its text) for the panel's source location. */
  readonly location: (location: RunicInspectLocation) => Node;
  /** Called after a write was accepted, and when the panel closes. */
  readonly onSaved?: () => void;
  readonly onClose?: (returnFocus: boolean) => void;
}

export interface MessageEditor {
  /** The panel's element; Runic Inspect keeps it in the card while the overview is rendered again. */
  readonly root: HTMLElement;
  readonly state: EditState | undefined;
  /** Opens the panel on a message and reads it. */
  open(message: { readonly catalog: string; readonly key: string; readonly locale?: string }): Promise<void>;
  close(returnFocus?: boolean): void;
  /** Reads the message again when the page's text changed, unless the text is being edited. */
  refresh(): Promise<void>;
  /** Saves the current locale; resolves when the API answered. */
  save(): Promise<void>;
  readonly isOpen: boolean;
}

/** The message API's URL below Vite's base. */
export function messageApiUrl(base: string, query?: { readonly catalog: string; readonly key: string }): string {
  const prefix = base.endsWith("/") ? base : `${base}/`;
  const path = `${/^https?:/iu.test(prefix) ? new URL(prefix).pathname : prefix}${messagePath}`;
  return query === undefined ? path : `${path}?key=${encodeURIComponent(query.key)}&catalog=${encodeURIComponent(query.catalog)}`;
}

async function reply(response: Promise<Response>): Promise<EditReply> {
  try {
    const answer = await response;
    let body: unknown;
    try { body = await answer.json(); } catch { body = undefined; }
    return { status: answer.status, body };
  } catch {
    return { status: 0 };
  }
}

export function createMessageEditor(options: MessageEditorOptions): MessageEditor {
  let state: EditState | undefined;
  let generation = 0;

  const element = <K extends keyof HTMLElementTagNameMap>(tag: K, className?: string, text?: string): HTMLElementTagNameMap[K] => {
    const created = document.createElement(tag);
    if (className !== undefined) created.className = className;
    if (text !== undefined) created.textContent = text;
    return created;
  };

  const root = element("section", "editor");
  root.tabIndex = -1;
  root.hidden = true;
  const head = element("div", "head");
  const title = element("h3");
  const close = element("button", "close", "×");
  close.type = "button";
  close.setAttribute("aria-label", "Close the editor");
  head.append(title, close);
  const localeRow = element("div", "row");
  const localeLabel = element("label", undefined, "Locale");
  const localeSelect = element("select");
  localeSelect.id = "runic-inspect-edit-locale";
  localeLabel.htmlFor = localeSelect.id;
  const where = element("span", "muted");
  localeRow.append(localeLabel, localeSelect, where);
  const structure = element("ul", "structure");
  const textLabel = element("label", undefined, "MF2 source");
  const textarea = element("textarea");
  textarea.id = "runic-inspect-edit-text";
  textarea.spellcheck = false;
  textarea.setAttribute("autocomplete", "off");
  textarea.setAttribute("autocapitalize", "off");
  textarea.setAttribute("aria-keyshortcuts", "Control+Enter Meta+Enter");
  textLabel.htmlFor = textarea.id;
  const conflict = element("div", "conflict");
  conflict.setAttribute("role", "group");
  conflict.setAttribute("aria-label", "Conflict");
  const conflictText = element("p");
  const conflictCurrent = element("pre");
  const reapply = element("button", "action", "Reapply my text");
  const discard = element("button", "action", "Use the file's text");
  reapply.type = discard.type = "button";
  const conflictRow = element("div", "row");
  conflictRow.append(reapply, discard);
  conflict.append(conflictText, conflictCurrent, conflictRow);
  const diagnostics = element("ul", "diagnostics");
  const status = element("p", "status");
  status.setAttribute("role", "status");
  status.setAttribute("aria-live", "polite");
  const actions = element("div", "row");
  const saveButton = element("button", "action primary", "Save");
  saveButton.type = "button";
  saveButton.title = "Save (Ctrl+Enter)";
  const revert = element("button", "action", "Revert");
  revert.type = "button";
  actions.append(saveButton, revert);
  root.append(head, localeRow, structure, textLabel, textarea, conflict, diagnostics, status, actions);

  function dispatch(event: EditEvent): void {
    if (state === undefined) return;
    state = editReducer(state, event);
    render();
  }

  function render(): void {
    if (state === undefined) return;
    const current = currentLocale(state);
    const message = state.message;
    title.replaceChildren("Edit ", Object.assign(element("code"), { textContent: `${state.catalog}:${state.key}` }));
    root.setAttribute("aria-label", `Edit ${state.catalog}:${state.key}`);
    // The locale list, base locale first, as the API lists them.
    const locales = message?.locales ?? [];
    if (localeSelect.options.length !== locales.length || locales.some((entry, index) => localeSelect.options[index]?.value !== entry.locale)) {
      localeSelect.replaceChildren(...locales.map((entry) => {
        const option = element("option", undefined, `${entry.locale}${entry.locale === message?.baseLocale ? " (base)" : ""}${
          entry.origin === "missing" ? " (missing)" : entry.origin === "override" ? " (override)" : entry.origin === "library" ? " (library)" : ""}`);
        option.value = entry.locale;
        return option;
      }));
    }
    for (const option of localeSelect.options) {
      const entry = locales.find((candidate) => candidate.locale === option.value);
      const dirty = entry !== undefined && isDirty(state, entry.locale);
      option.textContent = option.textContent!.replace(/ ●$/u, "") + (dirty ? " ●" : "");
    }
    localeSelect.value = state.locale;
    localeSelect.disabled = locales.length === 0 || state.status === "saving";
    where.replaceChildren(...(current?.source === undefined && current?.file.path === undefined ? [] : [
      " ", options.location({
        editor: "translations", file: current.source?.path ?? current.file.path, line: current.source?.line ?? 1, column: current.source?.column ?? 1,
        message: { catalog: state.catalog, key: state.key, locale: current.locale },
      }),
      ...(current.file.path !== "" && current.source !== undefined && current.file.path !== current.source.path
        ? [element("span", "muted", ` · saves to ${current.file.path.split(/[\\/]/u).slice(-3).join("/")}`)] : []),
    ]));
    structure.replaceChildren(...(message === undefined || current === undefined ? [] : describeStructure(message, current))
      .map((line) => element("li", undefined, line)));
    // The text: typing makes the draft what the field shows, so the field is set only when the draft differs (another
    // locale, a read, Revert, a conflict's choice), and the caret stays while typing.
    const text = draftText(state);
    if (textarea.value !== text) textarea.value = text;
    textarea.readOnly = current === undefined || !current.editable || state.status === "saving" || state.status === "conflict";
    textarea.rows = Math.min(12, Math.max(3, text.split("\n").length + 1));
    // Conflict: the file's text now, and the two ways on.
    conflict.hidden = state.status !== "conflict";
    if (state.conflict !== undefined) {
      conflictText.textContent = `${state.detail ?? "The file changed since it was read."} In the file now:`;
      conflictCurrent.textContent = state.conflict.text;
    }
    diagnostics.replaceChildren(...state.diagnostics.map((diagnostic) => element("li", undefined,
      `${diagnostic.code}${diagnostic.line === undefined ? "" : ` line ${diagnostic.line}${diagnostic.column === undefined ? "" : `:${diagnostic.column}`}`}: ${diagnostic.message}`)));
    diagnostics.setAttribute("role", state.diagnostics.length > 0 ? "alert" : "list");
    const [statusText, tone] = statusLine(state);
    status.textContent = statusText;
    status.className = `status${tone === undefined ? "" : ` ${tone}`}`;
    saveButton.disabled = !canSave(state);
    saveButton.textContent = state.status === "saving" ? "Saving…" : "Save";
    revert.disabled = !isDirty(state) || state.status === "saving";
    root.dataset["status"] = state.status;
  }

  function statusLine(current: EditState): [string, "error" | "ok" | undefined] {
    const locale = currentLocale(current);
    switch (current.status) {
      case "loading": return ["Reading the message…", undefined];
      case "saving": return ["Saving…", undefined];
      case "saved": return [`Saved to ${(current.written ?? []).map((path) => path.split(/[\\/]/u).pop()).join(", ") || "its source"}; the page shows it in place.`, "ok"];
      case "invalid": return [current.detail ?? "The text does not compile; nothing was written.", "error"];
      case "conflict": return ["Reapply your text over the file's, or take the file's text.", "error"];
      case "forbidden": return [`${current.detail ?? "Not allowed."} To edit from another machine, start the dev server with RUNIC_DEV_ALLOW listing this machine or its network (or runicTranslations({ inPageEditing: { allow } })).`, "error"];
      case "failed": return [current.detail ?? "The request failed.", "error"];
      case "ready":
        if (locale !== undefined && !locale.editable) return [locale.reason ?? "This locale cannot be written here.", "error"];
        if (locale?.origin === "missing") return [`${locale.locale} has no text of its own${locale.fallback ? ` (shows ${locale.fallback})` : ""}; saving adds it beside the base locale's.`, undefined];
        if (locale?.origin === "library") return ["The library's own text: saving writes the application's override, not the installed package.", undefined];
        return [isDirty(current) ? "Edited; Save writes it to the source." : "", undefined];
    }
  }

  async function read(event: "read" | "refreshed"): Promise<void> {
    if (state === undefined) return;
    const pending = ++generation;
    const answer = await reply(fetch(messageApiUrl(options.base, state), { credentials: "same-origin", cache: "no-store" }));
    if (pending !== generation || state === undefined) return;
    dispatch({ type: event, reply: answer });
    if (event === "read" && state?.status === "ready" && root.isConnected) textarea.focus();
  }

  async function save(): Promise<void> {
    if (state === undefined || !canSave(state)) return;
    const body = saveRequest(state);
    dispatch({ type: "save" });
    const pending = ++generation;
    const answer = await reply(fetch(messageApiUrl(options.base), {
      method: "POST", credentials: "same-origin", cache: "no-store",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify(body),
    }));
    if (pending !== generation) return;
    dispatch({ type: "saved", reply: answer });
    if (state?.status === "saved") options.onSaved?.();
    else if (state?.status === "conflict") reapply.focus();
  }

  close.addEventListener("click", () => editor.close(true));
  localeSelect.addEventListener("change", () => {
    dispatch({ type: "locale", locale: localeSelect.value });
  });
  textarea.addEventListener("input", () => dispatch({ type: "input", text: textarea.value }));
  textarea.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      void save();
    }
  });
  saveButton.addEventListener("click", () => void save());
  revert.addEventListener("click", () => {
    dispatch({ type: "revert" });
    textarea.focus();
  });
  reapply.addEventListener("click", () => {
    dispatch({ type: "reapply" });
    textarea.focus();
  });
  discard.addEventListener("click", () => {
    dispatch({ type: "discard" });
    textarea.focus();
  });

  const editor: MessageEditor = {
    root,
    get state() { return state; },
    get isOpen() { return !root.hidden; },
    async open(message) {
      generation++;
      state = startEdit(message);
      textarea.value = "";
      root.hidden = false;
      render();
      root.focus();
      await read("read");
    },
    close(returnFocus = false) {
      if (root.hidden) return;
      generation++;
      root.hidden = true;
      state = undefined;
      options.onClose?.(returnFocus);
    },
    async refresh() {
      if (state === undefined || root.hidden || state.status === "loading" || state.status === "saving") return;
      await read("refreshed");
    },
    save,
  };
  return editor;
}
