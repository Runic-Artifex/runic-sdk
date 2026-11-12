import { hasInjectionContext, inject, provide, type InjectionKey } from "vue";
import { currentInstance, type RunicInstance } from "@runic-artifex/views";

/**
 * The injection key of the Runic instance (`createRunicInstance` in @runic-artifex/views), for `app.provide`:
 * `createApp(App).provide(runicInstanceKey, instance)` makes it the instance of the whole app.
 */
export const runicInstanceKey: InjectionKey<RunicInstance> = Symbol("runic.instance");

/**
 * Makes `instance` the Runic instance of the calling component and its descendants: `useView`, `useField`, `useText`,
 * `useBridgeConnection`, `useAppearance` and the other composables below it connect and read through it instead of
 * the page instance. Call it in `setup()`. A guest that renders inside another page, such as an embedded component,
 * calls it in its root component (or provides `runicInstanceKey` on its app).
 *
 * ```ts
 * const instance = createRunicInstance({ bridge });
 * createApp(defineComponent({ setup() { provideRunicInstance(instance); return () => h(App); } })).mount(element);
 * ```
 */
export function provideRunicInstance(instance: RunicInstance): void {
  provide(runicInstanceKey, instance);
}

/**
 * The Runic instance of the calling component: that of the nearest `provideRunicInstance` (or `runicInstanceKey` on
 * the app). Without one, and outside a component's `setup()`, it is the current instance: that of an enclosing
 * `instance.run`, otherwise the page instance.
 */
export function useRunicInstance(): RunicInstance {
  const provided = hasInjectionContext() ? inject(runicInstanceKey, undefined) : undefined;
  return provided ?? currentInstance();
}
