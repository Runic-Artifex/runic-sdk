import { createContext, onMount } from "svelte";
import type { SvelteApplicationBridge } from "./bridge.svelte.js";

export interface ApplicationBridgeContext<Command, Receipt, HostEvent, Snapshot, Failure = unknown> {
  provide(
    bridge: SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot, Failure>,
    options?: Readonly<{ start?: boolean; dispose?: boolean }>,
  ): SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot, Failure>;
  use(): SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot, Failure>;
}

export function createApplicationBridgeContext<Command, Receipt, HostEvent, Snapshot, Failure = unknown>():
  ApplicationBridgeContext<Command, Receipt, HostEvent, Snapshot, Failure> {
  const [use, set] = createContext<SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot, Failure>>();
  return {
    provide(bridge, options = {}) {
      set(bridge);
      const shouldStart = options.start ?? true;
      const shouldDispose = options.dispose ?? true;
      onMount(() => {
        if (shouldStart) void bridge.start();
        return () => {
          if (shouldDispose) void bridge.dispose();
        };
      });
      return bridge;
    },
    use,
  };
}
