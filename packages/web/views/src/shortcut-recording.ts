// Recording a shortcut from a key press, for settings pages where the user chooses a key, such as a global shortcut
// that the application passes to IDesktopGlobalShortcuts.BindAsync as DesktopShortcut text. The shared cases are the
// "record" cases of specs/desktop/fixtures/shortcuts.json.
import { isApplePlatform, parseShortcut, shortcutText, type ShortcutEvent, type ShortcutModifier, type ShortcutOptions } from "./shortcuts.js";

// Keys that only modify another key, and the values browsers give a press that types no key of its own.
const notKeys = new Set([
  "Shift", "Control", "Alt", "AltGraph", "Meta", "OS", "Super", "Hyper", "Fn", "FnLock", "CapsLock", "NumLock", "ScrollLock",
  "Symbol", "SymbolLock", "Dead", "Process", "Compose", "Unidentified", "",
]);

// The key of the press as DesktopShortcut names it: digits by their key on the top row or the keypad, so a layout that
// types another character there (AZERTY) records the digit; letters by the character, or by their key when a modifier
// such as Option makes the key type another character.
function recordedKey(event: ShortcutEvent): string | null {
  const digit = /^(?:Digit|Numpad)([0-9])$/.exec(event.code ?? "");
  if (digit) return digit[1] ?? null;
  if (event.key === " ") return "Space";
  if (event.key === "+") return "Plus";
  const key = parseShortcut(event.key)?.key;
  if (key) return key;
  const letter = /^Key([A-Z])$/.exec(event.code ?? "");
  return letter ? letter[1] ?? null : null;
}

/**
 * The shortcut text that `DesktopShortcut.Parse` reads for a `keydown` event, such as `Primary+Shift+S`, so a page can
 * record the key a user chooses and send it through an ordinary command; `null` for a press that is not a shortcut.
 *
 * `Primary` is the Command key on Apple platforms and Ctrl elsewhere, and `Control` is the Control key on Apple
 * platforms. A punctuation character is recorded as typed, without Shift, as {@link matchesShortcut} reads it. Presses
 * of a modifier alone, dead keys, input method composition, keys outside DesktopShortcut's set and, outside Apple
 * platforms, presses with the Windows or Super key give `null`.
 */
export function shortcutFromEvent(event: ShortcutEvent, options: ShortcutOptions = {}): string | null {
  if (event.isComposing || notKeys.has(event.key)) return null;
  const apple = options.apple ?? isApplePlatform();
  if (!apple && event.metaKey) return null;
  const key = recordedKey(event);
  if (!key) return null;
  const modifiers = new Set<ShortcutModifier>();
  if (apple ? event.metaKey : event.ctrlKey) modifiers.add("Primary");
  if (apple && event.ctrlKey) modifiers.add("Control");
  if (event.altKey) modifiers.add("Alt");
  // A punctuation character already carries Shift.
  const shiftFree = key === "Plus" || key.length === 1 && !/^[A-Z0-9]$/.test(key);
  if (event.shiftKey && !shiftFree) modifiers.add("Shift");
  return shortcutText({ key, modifiers });
}
