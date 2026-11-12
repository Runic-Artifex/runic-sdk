// Runic's own page text: the sentences `describeBridgeError` writes and the key names `formatShortcut` writes. Each is
// a message of the `runic-views` catalog (Runic.Application.Views.Translations, @runic-artifex/views-translations),
// named `group.message` as in the catalog's sources; the English here is the catalog's `en` text, which the
// views-translations tests compare. `configureRunicText` gives the page other text for them.

/** Runic's English text of each {@link RunicTextKey}, the text shown without `configureRunicText`. */
const english = {
  // describeBridgeError: a BridgeError by kind.
  "bridge.unavailable": "This page is not running in a Runic host, so it cannot reach the application.",
  "bridge.timeout": "The application did not answer in time.",
  "bridge.disconnected": "The connection to the application was lost. It is restored automatically.",
  "bridge.released": "This view was closed or replaced.",
  "bridge.cancelled": "The request was cancelled.",
  "bridge.rejected": "The application rejected the request.",
  "bridge.failed": "The request failed.",
  // describeBridgeError: a BridgeError by reason, and the other errors it describes.
  "bridge.action_unavailable": "This action is not available right now.",
  "bridge.busy": "The application is busy. Try again in a moment.",
  "bridge.invalid_input": "The input is not valid.",
  "bridge.item_not_found": "The item no longer exists.",
  "bridge.protocol_mismatch": "This page and the application use different Runic versions. Rebuild the frontend.",
  "bridge.state_withheld": "The change was made, but the page cannot show it yet.",
  "bridge.outcome_unknown": "It is not known whether the operation finished. Check its result before you try again.",
  "bridge.value_not_saved": "The value was not saved.",
  // formatShortcut: modifier names (shared with Runic.Platform's DesktopPlatformTexts) and key names, outside Apple
  // platforms; on Apple platforms only the space key has a name, the others are symbols.
  "platform.control_key": "Ctrl",
  "platform.alt_key": "Alt",
  "platform.shift_key": "Shift",
  "shortcut.enter": "Enter",
  "shortcut.escape": "Esc",
  "shortcut.tab": "Tab",
  "shortcut.space": "Space",
  "shortcut.backspace": "Backspace",
  "shortcut.delete": "Del",
  "shortcut.up": "Up",
  "shortcut.down": "Down",
  "shortcut.left": "Left",
  "shortcut.right": "Right",
  "shortcut.home": "Home",
  "shortcut.end": "End",
  "shortcut.page_up": "Page Up",
  "shortcut.page_down": "Page Down",
} as const;

/**
 * A text Runic writes on the page itself, named after its message in the `runic-views` catalog (`bridge.failed` is
 * the message `failed` of the group `bridge`). Open: a minor release may add keys, so a hook returns `undefined` for a
 * key it does not know.
 */
export type RunicTextKey = keyof typeof english;

/** Every {@link RunicTextKey}, for a test that checks that an application's hook covers them. */
export const runicTextKeys: readonly RunicTextKey[] = Object.freeze(Object.keys(english) as RunicTextKey[]);

/** @internal Runic's English text of a key. */
export function englishRunicText(key: RunicTextKey): string {
  return english[key];
}
