// Keyboard shortcuts for the page, with the rule of Runic.Platform's DesktopShortcut: "Primary" is Command on Apple
// platforms and Ctrl elsewhere, so one shortcut such as "Primary+S" means Save on every platform. The shared cases
// are in specs/desktop/fixtures/shortcuts.json.

/** A modifier of a shortcut. `Control` is the Control key on Apple platforms and the same Ctrl as `Primary` elsewhere. */
export type ShortcutModifier = "Primary" | "Control" | "Alt" | "Shift";

/** A shortcut read by {@link parseShortcut}: its normalized key and modifiers. */
export interface Shortcut {
  readonly key: string;
  readonly modifiers: ReadonlySet<ShortcutModifier>;
}

/** Which platform's rule {@link matchesShortcut} and {@link formatShortcut} apply. */
export interface ShortcutOptions {
  /** Whether the page runs on an Apple platform; detected from `navigator` when omitted. */
  readonly apple?: boolean;
}

/** The parts of a `KeyboardEvent` a shortcut reads. */
export interface ShortcutEvent {
  readonly key: string;
  readonly code?: string;
  readonly metaKey?: boolean;
  readonly ctrlKey?: boolean;
  readonly altKey?: boolean;
  readonly shiftKey?: boolean;
  readonly isComposing?: boolean;
}

const namedKeys = [
  "Enter", "Escape", "Tab", "Space", "Backspace", "Delete", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
  "Home", "End", "PageUp", "PageDown", "Plus",
] as const;
const modifierNames: Record<string, ShortcutModifier> = { PRIMARY: "Primary", CONTROL: "Control", ALT: "Alt", SHIFT: "Shift" };
const order: readonly ShortcutModifier[] = ["Primary", "Control", "Alt", "Shift"];
const cache = new Map<string, Shortcut | null>();

function normalizeKey(key: string): string | null {
  if (key.length === 0) return null;
  if (key === "+") return "Plus";
  if (key.length === 1) {
    if (/^[a-z]$/i.test(key)) return key.toUpperCase();
    if (/^[0-9]$/.test(key)) return key;
    const code = key.charCodeAt(0);
    return code > 0x20 && code < 0x7f ? key : null;
  }
  const named = namedKeys.find((name) => name.toLowerCase() === key.toLowerCase());
  if (named) return named;
  const f = /^[fF]([1-9][0-9]?)$/.exec(key);
  return f && Number(f[1]) <= 24 ? `F${f[1]}` : null;
}

/** Reads a shortcut such as `Primary+Shift+S`; `null` when the text is not one. */
export function parseShortcut(text: string): Shortcut | null {
  const cached = cache.get(text);
  if (cached !== undefined) return cached;
  let result: Shortcut | null = null;
  const trimmed = text.trim();
  if (trimmed.length > 0) {
    let parts = trimmed.split("+");
    if (parts.length >= 2 && parts[parts.length - 1] === "" && parts[parts.length - 2] === "") {
      parts = [...parts.slice(0, -2), "+"];
    }
    const modifiers = new Set<ShortcutModifier>();
    let valid = true;
    for (const part of parts.slice(0, -1)) {
      const modifier = modifierNames[part.trim().toUpperCase()];
      if (!modifier || modifiers.has(modifier)) {
        valid = false;
        break;
      }
      modifiers.add(modifier);
    }
    const key = valid ? normalizeKey((parts[parts.length - 1] ?? "").trim()) : null;
    if (key) result = Object.freeze({ key, modifiers });
  }
  cache.set(text, result);
  return result;
}

/** Whether the page runs on macOS, iOS or iPadOS, where `Primary` is Command. */
export function isApplePlatform(): boolean {
  const navigatorLike = (globalThis as { navigator?: { userAgentData?: { platform?: string }; platform?: string } }).navigator;
  const platform = navigatorLike?.userAgentData?.platform ?? navigatorLike?.platform ?? "";
  return /mac|iphone|ipad|ipod/i.test(platform);
}

function keyMatches(key: string, event: ShortcutEvent): boolean {
  if (/^[A-Z]$/.test(key)) return event.code === `Key${key}` || event.key.toUpperCase() === key;
  if (/^[0-9]$/.test(key)) return event.code === `Digit${key}` || event.code === `Numpad${key}` || event.key === key;
  switch (key) {
    case "Space": return event.key === " " || event.code === "Space";
    case "Plus": return event.key === "+";
    default: return event.key === key;
  }
}

/**
 * Whether a `keydown` event is the shortcut, with exactly its modifiers. `Primary` is the Command key on Apple
 * platforms and Ctrl elsewhere. Events of an input method's composition never match.
 */
export function matchesShortcut(event: ShortcutEvent, shortcut: string | Shortcut, options: ShortcutOptions = {}): boolean {
  const parsed = typeof shortcut === "string" ? parseShortcut(shortcut) : shortcut;
  if (!parsed || event.isComposing) return false;
  const apple = options.apple ?? isApplePlatform();
  const wants = parsed.modifiers;
  const meta = apple ? wants.has("Primary") : false;
  const ctrl = apple ? wants.has("Control") : wants.has("Primary") || wants.has("Control");
  // A shifted punctuation key, such as + on many layouts, carries Shift in the event but not in the shortcut.
  const shiftFree = parsed.key.length === 1 && !/^[A-Z0-9]$/.test(parsed.key) || parsed.key === "Plus";
  return !!event.metaKey === meta && !!event.ctrlKey === ctrl && !!event.altKey === wants.has("Alt")
    && (shiftFree ? !wants.has("Shift") || !!event.shiftKey : !!event.shiftKey === wants.has("Shift"))
    && keyMatches(parsed.key, event);
}

const appleKeys: Record<string, string> = {
  Enter: "↩", Escape: "⎋", Tab: "⇥", Space: "Space", Backspace: "⌫", Delete: "⌦", ArrowUp: "↑", ArrowDown: "↓",
  ArrowLeft: "←", ArrowRight: "→", Home: "↖", End: "↘", PageUp: "⇞", PageDown: "⇟", Plus: "+",
};
const otherKeys: Record<string, string> = {
  Escape: "Esc", Delete: "Del", ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
  PageUp: "Page Up", PageDown: "Page Down", Plus: "+",
};

/** The shortcut as the platform shows it, such as `⇧⌘S` on Apple platforms and `Ctrl+Shift+S` elsewhere; `""` for invalid text. */
export function formatShortcut(shortcut: string | Shortcut, options: ShortcutOptions = {}): string {
  const parsed = typeof shortcut === "string" ? parseShortcut(shortcut) : shortcut;
  if (!parsed) return "";
  const has = (modifier: ShortcutModifier) => parsed.modifiers.has(modifier);
  if (options.apple ?? isApplePlatform()) {
    return (has("Control") ? "⌃" : "") + (has("Alt") ? "⌥" : "") + (has("Shift") ? "⇧" : "") + (has("Primary") ? "⌘" : "")
      + (appleKeys[parsed.key] ?? parsed.key);
  }
  return (has("Primary") || has("Control") ? "Ctrl+" : "") + (has("Alt") ? "Alt+" : "") + (has("Shift") ? "Shift+" : "")
    + (otherKeys[parsed.key] ?? parsed.key);
}

/** The shortcut as `parseShortcut` reads it, in canonical order: `Primary+Control+Alt+Shift+Key`. */
export function shortcutText(shortcut: Shortcut): string {
  return [...order.filter((modifier) => shortcut.modifiers.has(modifier)), shortcut.key].join("+");
}
