import {
  InjectionToken,
  inject,
  type Provider,
} from "@angular/core";
import {
  startNativeMvvmApplication,
  type NativeMvvmApplication,
  type NativeMvvmApplicationOptions,
} from "@runic-artifex/mvvm";

import { AngularMvvmStore } from "./store.js";

/** A typed native application with its Angular signal store. */
export interface AngularMvvmApplication<TContract>
  extends NativeMvvmApplication<TContract>
{
  readonly store: AngularMvvmStore;
}

/** Injection token for the framework-neutral typed Angular application owner. */
export const ANGULAR_MVVM_APPLICATION =
  new InjectionToken<AngularMvvmApplication<unknown>>(
    "runic.toolkit.mvvm.angular.application",
  );

/** Opens the native application and owns its Angular signal store. */
export async function startAngularMvvmApplication<TContract>(
  options: Readonly<NativeMvvmApplicationOptions<TContract>>,
): Promise<AngularMvvmApplication<TContract>> {
  const application = await startNativeMvvmApplication(options);
  const store = new AngularMvvmStore(application.projection);
  application.addCleanup(() => store.destroy());
  return Object.freeze({
    ...application,
    store,
  });
}

/** Provides one caller-created application to Angular dependency injection. */
export function provideAngularMvvmApplication(
  application: AngularMvvmApplication<unknown>,
): Provider[] {
  return [
    { provide: ANGULAR_MVVM_APPLICATION, useValue: application },
  ];
}

/** Injects the typed application owner from the nearest environment injector. */
export function injectAngularMvvmApplication<TContract>():
  AngularMvvmApplication<TContract> {
  return inject(ANGULAR_MVVM_APPLICATION) as AngularMvvmApplication<TContract>;
}
