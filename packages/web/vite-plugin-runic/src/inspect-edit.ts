// Runic Inspect's translation editor (#957): the state of the "Edit" panel of a message, without the DOM. It reads the
// message from Runic Translations' in-page editing API (`GET /__runic-translations/message`, vite-plugin-runic-translations
// 0.12 under `vite dev`), keeps an edited text per locale, and writes one locale with the file hash it read (`POST`). The
// API answers 422 with diagnostics when the text does not compile, 409 with the current message when the file changed
// since the read, and 403 when this client is not allowed. The page then updates in place through the Translations
// text-change channel; the panel reads the message again when that channel changes and the text was not edited.
//
// `editReducer` is pure: inspect-editor.ts renders a state and sends what `saveRequest` returns.

/** A compiler diagnostic of a refused text (RTR codes), located in the edited source. */
export interface EditDiagnostic {
  readonly code: string;
  readonly severity: string;
  readonly message: string;
  readonly path?: string;
  readonly line?: number;
  readonly column?: number;
  readonly helpUri?: string;
}

/** The authoring projection of a locale's text: inputs, selectors and variants (`locales[].structure`). */
export interface EditStructure {
  readonly supported: boolean;
  readonly inputs: readonly { readonly name: string; readonly type?: string; readonly function?: string }[];
  readonly selectors: readonly string[];
  readonly selectorFunctions: readonly string[];
  readonly variants: readonly { readonly id: string; readonly keys: readonly string[]; readonly pattern: string }[];
  readonly pluralCategories: readonly string[];
}

/** One locale of a message, as the API returns it. */
export interface EditLocale {
  readonly locale: string;
  readonly fallback?: string;
  /** `catalog`, `override`, `library` or `missing` (no text of its own). */
  readonly origin: string;
  /** The whole MF2 message as authored; null for a locale without text of its own. */
  readonly text: string | null;
  readonly source?: { readonly path: string; readonly line: number; readonly column: number };
  /** The file a write changes, with the hash a write must name. */
  readonly file: { readonly path: string; readonly exists: boolean; readonly hash: string };
  readonly editable: boolean;
  readonly reason?: string;
  readonly structure?: EditStructure;
}

/** A message in every locale, base locale first. */
export interface EditMessage {
  readonly catalog: string;
  readonly kind: string;
  readonly package?: string;
  readonly key: string;
  readonly logicalKey?: string;
  readonly baseLocale: string;
  readonly content?: string;
  readonly structured: boolean;
  readonly inputs: readonly { readonly name: string; readonly type?: string }[];
  readonly markup: readonly string[];
  readonly locales: readonly EditLocale[];
}

/** What the panel shows: */
export type EditStatus =
  | "loading" // reading the message
  | "ready" // editing
  | "saving" // the write is on its way
  | "saved" // written; the page updates in place
  | "invalid" // 422: the text does not compile (diagnostics), or the locale cannot be written
  | "conflict" // 409: the file changed since the read; `conflict` has the current text
  | "forbidden" // 403: this client may not use the API
  | "failed"; // the API is missing (404/501/503), or the request failed

export interface EditState {
  readonly status: EditStatus;
  readonly catalog: string;
  readonly key: string;
  /** The locale being edited. */
  readonly locale: string;
  readonly message?: EditMessage;
  /** The text being edited, per locale; a locale not listed has the text as read. */
  readonly drafts: Readonly<Record<string, string>>;
  /** The file hash each locale was read with, which a write must name. */
  readonly hashes: Readonly<Record<string, string>>;
  readonly diagnostics: readonly EditDiagnostic[];
  /** For `conflict`: the locale's text and hash in the file now. */
  readonly conflict?: { readonly locale: string; readonly text: string; readonly hash: string; readonly message: EditMessage } | undefined;
  /** The server's explanation of a refusal or failure. */
  readonly detail?: string | undefined;
  /** For `saved`: the files the write changed. */
  readonly written?: readonly string[] | undefined;
}

/** An HTTP reply as the panel sees it: the status (0 when the request failed) and the JSON body, if any. */
export interface EditReply {
  readonly status: number;
  readonly body?: unknown;
}

export type EditEvent =
  | { readonly type: "read"; readonly reply: EditReply }
  /** A read after the page's text changed (`read` without leaving the current status or the edited text). */
  | { readonly type: "refreshed"; readonly reply: EditReply }
  | { readonly type: "locale"; readonly locale: string }
  | { readonly type: "input"; readonly text: string }
  | { readonly type: "save" }
  | { readonly type: "saved"; readonly reply: EditReply }
  /** After a conflict: keep the edited text and write it over the current file. */
  | { readonly type: "reapply" }
  /** After a conflict: take the current text, dropping the edit. */
  | { readonly type: "discard" }
  /** Back to the text as read. */
  | { readonly type: "revert" };

/** The panel's first state, for a message the overview lists; `locale` is the one the page shows. */
export function startEdit(message: { readonly catalog: string; readonly key: string; readonly locale?: string }): EditState {
  return { status: "loading", catalog: message.catalog, key: message.key, locale: message.locale ?? "", drafts: {}, hashes: {}, diagnostics: [] };
}

/** The locale's text as read: its source, or empty for a locale without text of its own. */
export function readText(state: EditState, locale = state.locale): string {
  return state.message?.locales.find((entry) => entry.locale === locale)?.text ?? "";
}

/** The text being edited in a locale. */
export function draftText(state: EditState, locale = state.locale): string {
  return state.drafts[locale] ?? readText(state, locale);
}

/** Whether a locale's text differs from what was read. */
export function isDirty(state: EditState, locale = state.locale): boolean {
  return draftText(state, locale) !== readText(state, locale);
}

/** Whether any locale has an edited text that was not saved. */
export function hasEdits(state: EditState): boolean {
  return Object.keys(state.drafts).some((locale) => isDirty(state, locale));
}

/** The locale entry being edited. */
export function currentLocale(state: EditState): EditLocale | undefined {
  return state.message?.locales.find((entry) => entry.locale === state.locale);
}

/** Whether Save does something now. */
export function canSave(state: EditState): boolean {
  const locale = currentLocale(state);
  return locale !== undefined && locale.editable && isDirty(state)
    && (state.status === "ready" || state.status === "saved" || state.status === "invalid");
}

/** The POST body for the current locale. */
export function saveRequest(state: EditState): { catalog: string; key: string; locale: string; text: string; expectedHash: string } {
  return {
    catalog: state.message?.catalog ?? state.catalog, key: state.message?.key ?? state.key, locale: state.locale,
    text: draftText(state), expectedHash: state.hashes[state.locale] ?? "none",
  };
}

export function editReducer(state: EditState, event: EditEvent): EditState {
  switch (event.type) {
    case "read": {
      const message = event.reply.status === 200 ? parseEditMessage((event.reply.body as { message?: unknown } | undefined)?.message) : undefined;
      if (message === undefined) return refused(state, event.reply, "loading");
      const locale = message.locales.some((entry) => entry.locale === state.locale) ? state.locale
        : message.locales.find((entry) => entry.locale === message.baseLocale)?.locale ?? message.locales[0]?.locale ?? "";
      return { ...state, status: "ready", message, locale, drafts: {}, hashes: hashesOf(message), diagnostics: [], ...clear };
    }
    case "refreshed": {
      // Busy, or showing a conflict: the next action reads or writes anyway.
      if (state.status === "saving" || state.status === "conflict" || state.status === "loading" || state.message === undefined) return state;
      const message = event.reply.status === 200 ? parseEditMessage((event.reply.body as { message?: unknown } | undefined)?.message) : undefined;
      if (message === undefined) return state;
      return withMessage(state, message);
    }
    case "locale":
      if (state.message === undefined || !state.message.locales.some((entry) => entry.locale === event.locale)) return state;
      return { ...state, locale: event.locale, diagnostics: [], ...(state.status === "invalid" || state.status === "saved" ? { status: "ready" as const } : {}), ...clear };
    case "input": {
      if (state.message === undefined || state.status === "saving" || state.status === "loading") return state;
      const status = state.status === "saved" || state.status === "invalid" ? "ready" : state.status;
      return { ...state, status, drafts: { ...state.drafts, [state.locale]: event.text }, ...(status === state.status ? {} : clear) };
    }
    case "revert": {
      if (state.message === undefined || state.status === "saving") return state;
      const { [state.locale]: _removed, ...drafts } = state.drafts;
      return { ...state, status: "ready", drafts, diagnostics: [], ...clear };
    }
    case "save":
      return canSave(state) ? { ...state, status: "saving", diagnostics: [], ...clear } : state;
    case "saved": {
      if (state.status !== "saving") return state;
      const { status, body } = event.reply;
      const reply = (body ?? {}) as { message?: unknown; written?: unknown; diagnostics?: unknown; detail?: unknown; error?: unknown };
      if (status === 200) {
        const message = parseEditMessage(reply.message);
        const written = Array.isArray(reply.written) ? reply.written.filter((path): path is string => typeof path === "string") : [];
        if (message === undefined) return { ...state, status: "saved", written };
        const { [state.locale]: _saved, ...drafts } = state.drafts;
        return { ...withMessage({ ...state, drafts }, message), status: "saved", diagnostics: [], written, ...noConflict };
      }
      if (status === 409) {
        const current = parseEditMessage(reply.message);
        const locale = current?.locales.find((entry) => entry.locale === state.locale);
        if (current === undefined || locale === undefined) return { ...state, status: "failed", detail: detailOf(reply, "The file changed since it was read.") };
        return { ...state, status: "conflict", diagnostics: [], conflict: { locale: state.locale, text: locale.text ?? "", hash: locale.file.hash, message: current },
          detail: detailOf(reply, "The file changed since it was read.") };
      }
      if (status === 422) {
        return { ...state, status: "invalid", diagnostics: parseDiagnostics(reply.diagnostics),
          detail: detailOf(reply, reply.error === "unsupported" ? "This locale cannot be written." : "The text does not compile; nothing was written.") };
      }
      return refused(state, event.reply, "saving");
    }
    case "reapply": {
      const conflict = state.conflict;
      if (state.status !== "conflict" || conflict === undefined) return state;
      // The edited text stays; the hashes are the file's now, so the next Save writes over it.
      return { ...state, status: "ready", message: conflict.message, hashes: hashesOf(conflict.message),
        drafts: { ...state.drafts, [conflict.locale]: draftText(state, conflict.locale) }, diagnostics: [], ...clear };
    }
    case "discard": {
      const conflict = state.conflict;
      if (state.status !== "conflict" || conflict === undefined) return state;
      const { [conflict.locale]: _dropped, ...drafts } = state.drafts;
      return { ...state, status: "ready", message: conflict.message, hashes: hashesOf(conflict.message), drafts, diagnostics: [], ...clear };
    }
  }
}

const noConflict = { conflict: undefined } as const;
const clear = { conflict: undefined, detail: undefined, written: undefined } as const;

// A message read again: the hashes are the file's now, and a locale whose text was not edited takes the new text.
function withMessage(state: EditState, message: EditMessage): EditState {
  const drafts: Record<string, string> = {};
  for (const [locale, text] of Object.entries(state.drafts)) {
    if (text !== readText(state, locale)) drafts[locale] = text;
  }
  const locale = message.locales.some((entry) => entry.locale === state.locale) ? state.locale : message.locales[0]?.locale ?? state.locale;
  return { ...state, message, locale, drafts, hashes: hashesOf(message) };
}

function hashesOf(message: EditMessage): Record<string, string> {
  return Object.fromEntries(message.locales.map((entry) => [entry.locale, entry.file.hash]));
}

// A refused or failed request: 403 says who may edit; anything else keeps the message, if there is one, and says why.
function refused(state: EditState, reply: EditReply, during: "loading" | "saving"): EditState {
  const body = (reply.body ?? {}) as { detail?: unknown; error?: unknown };
  if (reply.status === 403) {
    return { ...state, status: "forbidden", detail: detailOf(body, "The Translations dev server refused this client.") };
  }
  const fallback = reply.status === 0 ? "The request did not reach the development server."
    : reply.status === 404 && during === "loading" ? "The Translations dev server has no editing API here (vite-plugin-runic-translations 0.12 or later in project mode, with inPageEditing on), or does not know this message."
      : reply.status === 501 ? "In-page editing needs project mode (the project option of runicTranslations())."
        : `The Translations dev server answered ${reply.status}.`;
  return { ...state, status: "failed", detail: detailOf(body, fallback) };
}

function detailOf(body: { detail?: unknown }, fallback: string): string {
  return typeof body.detail === "string" && body.detail.length > 0 ? body.detail : fallback;
}

/** The API's `message`, with the members the panel uses checked; undefined for anything else. */
export function parseEditMessage(value: unknown): EditMessage | undefined {
  if (typeof value !== "object" || value === null) return undefined;
  const message = value as Record<string, unknown>;
  if (typeof message.catalog !== "string" || typeof message.key !== "string" || !Array.isArray(message.locales)) return undefined;
  const locales = message.locales.flatMap((item: unknown): EditLocale[] => {
    if (typeof item !== "object" || item === null) return [];
    const entry = item as Record<string, unknown>;
    if (typeof entry.locale !== "string" || entry.locale.length === 0) return [];
    const file = (entry.file ?? {}) as Record<string, unknown>;
    const source = entry.source as Record<string, unknown> | null | undefined;
    const structure = parseStructure(entry.structure);
    return [{
      locale: entry.locale,
      ...(typeof entry.fallback === "string" ? { fallback: entry.fallback } : {}),
      origin: typeof entry.origin === "string" ? entry.origin : "catalog",
      text: typeof entry.text === "string" ? entry.text : null,
      ...(typeof source?.path === "string" && Number.isSafeInteger(source.line)
        ? { source: { path: source.path, line: source.line as number, column: Number.isSafeInteger(source.column) ? source.column as number : 1 } } : {}),
      file: {
        path: typeof file.path === "string" ? file.path : "", exists: file.exists === true,
        hash: typeof file.hash === "string" ? file.hash : "none",
      },
      editable: entry.editable === true,
      ...(typeof entry.reason === "string" ? { reason: entry.reason } : {}),
      ...(structure === undefined ? {} : { structure }),
    }];
  });
  return {
    catalog: message.catalog,
    kind: typeof message.kind === "string" ? message.kind : "catalog",
    ...(typeof message.package === "string" ? { package: message.package } : {}),
    key: message.key,
    ...(typeof message.logicalKey === "string" ? { logicalKey: message.logicalKey } : {}),
    baseLocale: typeof message.baseLocale === "string" ? message.baseLocale : locales[0]?.locale ?? "",
    ...(typeof message.content === "string" ? { content: message.content } : {}),
    structured: message.structured === true,
    inputs: Array.isArray(message.inputs) ? message.inputs.flatMap((input: unknown) => named(input)) : [],
    markup: Array.isArray(message.markup) ? message.markup.flatMap((item: unknown) => typeof item === "string" ? [item]
      : typeof (item as { name?: unknown } | null)?.name === "string" ? [(item as { name: string }).name] : []) : [],
    locales,
  };
}

function named(value: unknown): { name: string; type?: string; function?: string }[] {
  if (typeof value !== "object" || value === null || typeof (value as { name?: unknown }).name !== "string") return [];
  const { name, type, function: fn } = value as { name: string; type?: unknown; function?: unknown };
  return [{ name, ...(typeof type === "string" ? { type } : {}), ...(typeof fn === "string" ? { function: fn } : {}) }];
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

function parseStructure(value: unknown): EditStructure | undefined {
  if (typeof value !== "object" || value === null) return undefined;
  const structure = value as Record<string, unknown>;
  return {
    supported: structure.supported !== false,
    inputs: Array.isArray(structure.inputs) ? structure.inputs.flatMap((input: unknown) => named(input)) : [],
    selectors: strings(structure.selectors),
    selectorFunctions: strings(structure.selectorFunctions),
    variants: Array.isArray(structure.variants) ? structure.variants.flatMap((item: unknown) => {
      const variant = item as { id?: unknown; keys?: unknown; pattern?: unknown } | null;
      return variant !== null && typeof variant === "object" && typeof variant.pattern === "string"
        ? [{ id: String(variant.id ?? ""), keys: strings(variant.keys), pattern: variant.pattern }] : [];
    }) : [],
    pluralCategories: strings(structure.pluralCategories),
  };
}

function parseDiagnostics(value: unknown): EditDiagnostic[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((item: unknown) => {
    const diagnostic = item as Record<string, unknown> | null;
    if (diagnostic === null || typeof diagnostic !== "object" || typeof diagnostic.message !== "string") return [];
    return [{
      code: typeof diagnostic.code === "string" ? diagnostic.code : "",
      severity: typeof diagnostic.severity === "string" ? diagnostic.severity : "error",
      message: diagnostic.message,
      ...(typeof diagnostic.path === "string" ? { path: diagnostic.path } : {}),
      ...(Number.isSafeInteger(diagnostic.line) ? { line: diagnostic.line as number } : {}),
      ...(Number.isSafeInteger(diagnostic.column) ? { column: diagnostic.column as number } : {}),
      ...(typeof diagnostic.helpUri === "string" ? { helpUri: diagnostic.helpUri } : {}),
    }];
  }).slice(0, 20);
}

/**
 * What the structure of a locale's text is, in short lines for the panel: its inputs, how it selects (plural or
 * selector, with the plural categories of the locale), its variants' keys and the markup it uses.
 */
export function describeStructure(message: EditMessage, locale: EditLocale): string[] {
  const lines: string[] = [];
  const structure = locale.structure;
  const inputs = structure?.inputs.length ? structure.inputs : message.inputs;
  if (inputs.length > 0) {
    lines.push(`Inputs: ${inputs.map((input) => `$${input.name}${"function" in input && input.function ? ` :${input.function}` : input.type ? ` (${input.type})` : ""}`).join(", ")}`);
  }
  if (structure !== undefined && structure.selectors.length > 0) {
    const how = structure.selectors.map((selector, index) => `$${selector}${structure.selectorFunctions[index] ? ` (${structure.selectorFunctions[index]})` : ""}`);
    lines.push(`Selects on ${how.join(", ")}: ${structure.variants.map((variant) => variant.keys.join(" ")).join(" · ")}`);
    if (structure.pluralCategories.length > 0) {
      const covered = new Set(structure.variants.flatMap((variant) => variant.keys));
      const missing = covered.has("*") ? [] : structure.pluralCategories.filter((category) => !covered.has(category));
      lines.push(`Plural categories of ${locale.locale}: ${structure.pluralCategories.join(", ")}${missing.length === 0 ? "" : ` (no variant for ${missing.join(", ")})`}`);
    }
  }
  if (message.markup.length > 0) lines.push(`Markup: ${message.markup.map((name) => `{#${name}}`).join(" ")}`);
  if (structure !== undefined && !structure.supported) lines.push("Edited as MF2 source.");
  return lines;
}
