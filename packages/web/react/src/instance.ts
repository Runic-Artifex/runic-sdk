import { createContext, createElement, useContext, type ReactNode } from "react";
import { defaultInstance, type RunicInstance } from "@runic-artifex/views";

// The Runic instance of a subtree (`createRunicInstance` in @runic-artifex/views). Without a provider it is the page
// instance, on `globalThis.__runicBridge`, as before instances existed.
const RunicInstanceContext = createContext<RunicInstance | undefined>(undefined);

export interface RunicInstanceProviderProps {
  /** The instance whose Views, connection state, text and appearance the subtree's hooks use. */
  readonly instance: RunicInstance;
  readonly children?: ReactNode;
}

/**
 * Makes `instance` the Runic instance of its subtree: `useView`, `useField`, `useText`, `useBridgeConnection`,
 * `useAppearance` and the other hooks below it connect and read through it instead of the page instance. A guest that
 * renders inside another page, such as an embedded component, wraps its root in it.
 *
 * ```tsx
 * const instance = createRunicInstance({ bridge });
 * root.render(<RunicInstanceProvider instance={instance}><App /></RunicInstanceProvider>);
 * ```
 */
export function RunicInstanceProvider({ instance, children }: RunicInstanceProviderProps): ReactNode {
  return createElement(RunicInstanceContext.Provider, { value: instance }, children);
}

/** The Runic instance of the calling component: that of the nearest `RunicInstanceProvider`, or the default instance (`defaultInstance()`): the page instance, or in a component bundle its element's. */
export function useRunicInstance(): RunicInstance {
  return useContext(RunicInstanceContext) ?? defaultInstance();
}
