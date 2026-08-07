import { createContext, onMount } from "svelte";
import type { SvelteApplicationBridge } from "./bridge.svelte.js";

export interface ApplicationBridgeContext<Command, Receipt, HostEvent, Snapshot> {
  provide(
    bridge: SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot>,
    options?: Readonly<{ start?: boolean; dispose?: boolean }>,
  ): SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot>;
  use(): SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot>;
}

export function createApplicationBridgeContext<Command, Receipt, HostEvent, Snapshot>():
  ApplicationBridgeContext<Command, Receipt, HostEvent, Snapshot> {
  const [use, set] = createContext<SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot>>();
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

