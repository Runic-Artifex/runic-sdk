import { getContext, setContext } from "svelte";
import { defaultInstance, type RunicInstance } from "@runic-artifex/views";

// The Runic instance of a component subtree (`createRunicInstance` in @runic-artifex/views). Without one set it is the
// default instance: the page instance, on `globalThis.__runicBridge`, as before instances existed, or in a component
// bundle its element's.
const instanceKey = Symbol("runic.svelte.instance");

/**
 * Makes `instance` the Runic instance of the calling component and its children: `useView`, `useField`, `useText`,
 * `useBridgeConnection`, `useAppearance`, `ViewOutlet` and the other helpers below it connect and read through it
 * instead of the page instance. Call it during component initialization, before those helpers. A guest that renders
 * inside another page, such as an embedded component, calls it in its root component.
 *
 * ```svelte
 * <script lang="ts">
 *   let { bridge } = $props();
 *   const instance = setRunicInstance(createRunicInstance({ bridge }));
 *   onDestroy(() => instance.dispose());
 * </script>
 * ```
 */
export function setRunicInstance(instance: RunicInstance): RunicInstance {
  return setContext(instanceKey, instance);
}

/**
 * The Runic instance of the calling component: the one the nearest `setRunicInstance` set, or the default instance
 * (`defaultInstance()`: the page instance, or in a component bundle its element's). Outside component initialization,
 * where Svelte has no context, it is the default instance.
 */
export function getRunicInstance(): RunicInstance {
  let instance: RunicInstance | undefined;
  try { instance = getContext<RunicInstance | undefined>(instanceKey); }
  catch { instance = undefined; }
  return instance ?? defaultInstance();
}
