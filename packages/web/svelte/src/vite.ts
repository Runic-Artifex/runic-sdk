import {
  createRunicDevtoolsObserver,
  disposeRunicHmrResource,
  preserveRunicHmrResource,
} from "@runic-artifex/vite-plugin-runic/client";
import type { RunicDevtoolsObserver } from "@runic-artifex/vite-plugin-runic/client";

export type { RunicDevtoolsObserver } from "@runic-artifex/vite-plugin-runic/client";

/** Connects a Svelte bridge projection to the optional Runic Vite client. */
export function createViteApplicationBridgeObserver(): RunicDevtoolsObserver {
  return createRunicDevtoolsObserver();
}

/** Preserves one application-owned resource across a Vite hot replacement. */
export function preserveViteHmrResource<T>(key: string, create: () => T): T {
  return preserveRunicHmrResource(key, create);
}

/** Explicitly releases a resource preserved for Vite hot replacement. */
export function disposeViteHmrResource(
  key: string,
  dispose?: (resource: unknown) => void | Promise<void>,
): Promise<void> {
  return disposeRunicHmrResource(key, dispose);
}
