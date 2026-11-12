// appearance/1 in the page runtime: it keeps the host's last `appearance` push as it arrived, in a channel of its
// instance that every copy of this package on the page shares, and wakes the appearance store if a page loaded it. Reading the push, and the
// store that combines it with the engine's media queries, live in appearance.ts, which only a page that reads the
// appearance (appearance(), onAppearance(), the adapters' hooks) bundles.

import type { Slots } from "./scope.js";

/** `appearance/1`: the desktop's appearance that the host pushes to its pages. */
export const appearanceFeature = "appearance/1";

/** The channel of an instance: the last push as received, and the store's wake-up once the store is loaded. */
export interface AppearancePushChannel {
  push: Readonly<Record<string, unknown>> | undefined;
  received: (() => void) | undefined;
}

/** The channel of the instance with `slots`, which every copy of the package shares. */
export function appearancePushChannel(slots: Slots): AppearancePushChannel {
  return (slots[Symbol.for("runic.views.appearance")] ??= { push: undefined, received: undefined }) as AppearancePushChannel;
}

/**
 * The page runtime reports each `appearance` push (not exported from the package). A new connection keeps the last push
 * until the host pushes again; `undefined` forgets it.
 */
export function receiveAppearancePush(slots: Slots, push: Readonly<Record<string, unknown>> | undefined): void {
  const channel = appearancePushChannel(slots);
  channel.push = push;
  channel.received?.();
}
