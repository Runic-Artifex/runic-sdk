import { assertInInjectionContext, inject, InjectionToken, type Injector, type Provider } from "@angular/core";
import { pageInstance, type RunicInstance } from "@runic-artifex/views";

/**
 * The Runic instance (`createRunicInstance` in `@runic-artifex/views`) that the `inject*` helpers, `ViewOutlet` and the
 * `runicText` pipe connect and read through. Without a provider it is the page instance, on `globalThis.__runicBridge`,
 * as before instances existed.
 */
export const RUNIC_INSTANCE = new InjectionToken<RunicInstance>("RUNIC_INSTANCE", {
  providedIn: "root",
  factory: () => pageInstance(),
});

/**
 * Provides `instance` as the {@link RUNIC_INSTANCE} of an injector: `injectView`, `injectField`, `injectText`,
 * `injectBridgeConnection`, `injectAppearance` and the other helpers below it use it instead of the page instance. A
 * guest that renders inside another page, such as an embedded component, adds it to its application's or its root
 * component's `providers`.
 *
 * ```ts
 * const instance = createRunicInstance({ bridge });
 * bootstrapApplication(App, { providers: [provideRunicInstance(instance)] });
 * ```
 */
export function provideRunicInstance(instance: RunicInstance): Provider {
  return { provide: RUNIC_INSTANCE, useValue: instance };
}

/** Options of {@link injectRunicInstance}. */
export interface InjectRunicInstanceOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/** The Runic instance of the injection context: the nearest {@link provideRunicInstance}, or the page instance. */
export function injectRunicInstance({ injector }: InjectRunicInstanceOptions = {}): RunicInstance {
  if (injector) return instanceOf(injector);
  assertInInjectionContext(injectRunicInstance);
  return inject(RUNIC_INSTANCE, { optional: true }) ?? pageInstance();
}

/** @internal The Runic instance of `injector`. */
export function instanceOf(injector: Injector): RunicInstance {
  return injector.get(RUNIC_INSTANCE, null) ?? pageInstance();
}

/** @internal The Runic instance of the injection context, or the page instance outside one. */
export function contextInstance(): RunicInstance {
  try { return inject(RUNIC_INSTANCE, { optional: true }) ?? pageInstance(); }
  catch { return pageInstance(); }
}
