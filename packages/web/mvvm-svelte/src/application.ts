import {
  startNativeMvvmApplication,
  type NativeMvvmApplication,
  type NativeMvvmApplicationOptions,
} from "@webuitoolkit/mvvm";
import { getContext, onDestroy, setContext } from "svelte";

import {
  createSvelteMvvmStore,
  type SvelteMvvmStore,
} from "./store.js";

const applicationContext = Symbol("webuitoolkit.mvvm.svelte.application");

/** A typed native application with its Svelte-readable projection store. */
export interface SvelteMvvmApplication<TContract>
  extends NativeMvvmApplication<TContract>
{
  readonly store: SvelteMvvmStore;
}

/** Opens the native application and owns its Svelte store. */
export async function startSvelteMvvmApplication<TContract>(
  options: Readonly<NativeMvvmApplicationOptions<TContract>>,
): Promise<SvelteMvvmApplication<TContract>> {
  const application = await startNativeMvvmApplication(options);
  const store = createSvelteMvvmStore(application.projection);
  application.addCleanup(() => store.dispose());
  return Object.freeze({
    ...application,
    store,
  });
}

/**
 * Provides an application from a root component and disposes it with that
 * component. Descendants consume it through `useSvelteMvvmApplication`.
 */
export function provideSvelteMvvmApplication<TContract>(
  application: SvelteMvvmApplication<TContract>,
): SvelteMvvmApplication<TContract> {
  setContext(applicationContext, application);
  onDestroy(() => void application.dispose("Svelte MVVM root unmounted"));
  return application;
}

/** Returns the typed native application provided by the nearest Svelte root. */
export function useSvelteMvvmApplication<TContract>():
  SvelteMvvmApplication<TContract> {
  const application =
    getContext<SvelteMvvmApplication<TContract> | undefined>(applicationContext);
  if (application === undefined) {
    throw new Error("No Svelte MVVM application was provided.");
  }
  return application;
}
