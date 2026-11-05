// appearance/1 in the page runtime: it keeps the host's last `appearance` push as it arrived, in a channel that every
// copy of this package on the page shares, and wakes the appearance store if a page loaded it. Reading the push, and the
// store that combines it with the engine's media queries, live in appearance.ts, which only a page that reads the
// appearance (appearance(), onAppearance(), the adapters' hooks) bundles.

/** `appearance/1`: the desktop's appearance that the host pushes to its pages. */
export const appearanceFeature = "appearance/1";

/** The page-wide channel: the last push as received, and the store's wake-up once the store is loaded. */
export interface AppearancePushChannel {
  push: Readonly<Record<string, unknown>> | undefined;
  received: (() => void) | undefined;
}

/** The channel of every copy of the package on the page. */
export function appearancePushChannel(): AppearancePushChannel {
  const host = globalThis as unknown as Record<symbol, AppearancePushChannel | undefined>;
  return host[Symbol.for("runic.views.appearance")] ??= { push: undefined, received: undefined };
}

/**
 * The page runtime reports each `appearance` push (not exported from the package). A new connection keeps the last push
 * until the host pushes again; `undefined` forgets it.
 */
export function receiveAppearancePush(push: Readonly<Record<string, unknown>> | undefined): void {
  const channel = appearancePushChannel();
  channel.push = push;
  channel.received?.();
}
