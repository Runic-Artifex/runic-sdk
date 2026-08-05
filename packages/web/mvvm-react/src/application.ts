import {
  startNativeMvvmApplication,
  type NativeMvvmApplication,
  type NativeMvvmApplicationOptions,
} from "@runic-artifex/mvvm";

import {
  createReactMvvmStore,
  type ReactMvvmStore,
} from "./store.js";

/** A typed native application with its React external store. */
export interface ReactMvvmApplication<TContract>
  extends NativeMvvmApplication<TContract>
{
  readonly store: ReactMvvmStore;
}

/**
 * Opens a native MVVM application and owns its React store under the same
 * reconnect/page/disposal lifetime.
 */
export async function startReactMvvmApplication<TContract>(
  options: Readonly<NativeMvvmApplicationOptions<TContract>>,
): Promise<ReactMvvmApplication<TContract>> {
  const application = await startNativeMvvmApplication(options);
  const store = createReactMvvmStore(application.projection);
  application.addCleanup(() => store.dispose());
  return Object.freeze({
    ...application,
    store,
  });
}
