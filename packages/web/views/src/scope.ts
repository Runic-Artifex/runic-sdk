// Runic instances (specs/application/README.md, "Runic instances"): the page-wide state of the Views runtime — its
// runtime and connection, the connection state, diagnostics, translated text, the appearance, the page's fields and the
// keep-alive pool — lives in the slots of one instance. The page instance keeps them on `globalThis` under the
// registered keys of "Shared globals", with `__runicBridge` as its Bridge, so a page and every copy of this package on
// it behave as before. `createRunicInstance` makes another instance with slots of its own and a Bridge it is given, so
// several Runic guests, or a guest inside a host page, can share one document without sharing state.
//
// Code that runs later (a push, a timer, a reply) uses the slots of the object it belongs to: a runtime, a route, a
// controller or a subscription captures them when it is created. `RunicInstance.run` makes an instance the current one
// while a function runs synchronously, so a connect call, a controller or a text function called inside it, including
// one of a generated client, belongs to that instance. Outside `run` the current instance is the default instance: the
// page instance, except in a component bundle (component.ts), whose copy of this package defaults to the instance of
// its one connected component element instead.
import type { RunicBridgeClient } from "./transport.js";

/** The slots of one Runic instance, which hold its page-wide state: `globalThis` for the page instance. Not part of the public API. */
export type Slots = Record<string | symbol, unknown>;

// The slots of the instance whose `run` is on the stack, shared by every copy of this package on the page, so a copy
// that a generated client or another bundle uses sees the same current instance. It holds no state of its own.
const currentKey = Symbol.for("runic.views.current-instance");
// The slots of an instance object, which every copy of this package reads.
const slotsKey = Symbol.for("runic.views.instance");

function page(): Slots {
  return globalThis as unknown as Slots;
}

// What this copy of the package uses outside `run`: the page's slots, unless a component bundle set a resolver
// (component.ts). It is a module variable, not a shared key: each component bundles a copy of its own.
let defaultResolver: (() => Slots) | undefined;

function defaultSlots(): Slots {
  return defaultResolver?.() ?? page();
}

/**
 * @internal Makes `resolve` the source of this copy's default instance, used outside `run` and outside a binding's
 * provider. Only a component bundle sets it.
 */
export function setDefaultSlots(resolve: (() => Slots) | undefined): void {
  defaultResolver = resolve;
}

/** @internal The slots of the current instance: those of the innermost `run`, or the default instance's. */
export function currentSlots(): Slots {
  return (page()[currentKey] as Slots | undefined) ?? defaultSlots();
}

/** @internal Whether `slots` belong to the page instance. */
export function isPageSlots(slots: Slots): boolean {
  return slots === page();
}

/** @internal Runs `run` with `slots` as the current instance's. */
export function runInSlots<T>(slots: Slots, run: () => T): T {
  const host = page();
  const previous = host[currentKey];
  // The page's own slots too, so that `pageInstance().run` in a component bundle reaches the page.
  host[currentKey] = slots;
  try { return run(); }
  finally { host[currentKey] = previous; }
}

/** @internal The Bridge of the instance with `slots`: `__runicBridge` on the page. */
export function bridgeOf(slots: Slots): RunicBridgeClient | undefined {
  return slots["__runicBridge"] as RunicBridgeClient | undefined;
}

/**
 * One Runic page runtime: a Views connection over one Bridge, with its own connection state, diagnostics, translated
 * text, window locale, appearance, fields and kept clients. The page instance is the document's own (`pageInstance()`),
 * which the served host bootstrap installs `__runicBridge` for; `createRunicInstance` makes others, such as one per
 * embedded component.
 */
export interface RunicInstance {
  /** Whether this is the page instance, whose Bridge is `globalThis.__runicBridge`. */
  readonly page: boolean;
  /** The instance's Bridge; for the page instance, the one installed now, if any. */
  readonly bridge: RunicBridgeClient | undefined;
  /**
   * Runs `run` with this instance as the current one and returns its result. What `run` calls synchronously belongs to
   * this instance: a generated `connect{View}` call, a controller it creates, `onBridgeConnection`, `onBridgeDiagnostic`,
   * the text and locale functions, `appearance()`, `commitAllFields()` and the like. What they start keeps the instance
   * after `run` returns; code after an `await` inside `run` does not, so call `run` again there.
   */
  run<T>(run: () => T): T;
  /**
   * Ends the instance: stops listening to its Bridge, reports its Views and operations disconnected, and stops its
   * `onBridgeConnection` listeners at their last state. A connect call in the instance afterwards rejects with an `unavailable` BridgeError. The page instance
   * is never disposed: calling it there throws a TypeError.
   */
  dispose(): void;
}

/** Options of {@link createRunicInstance}. */
export interface RunicInstanceOptions {
  /** The instance's Bridge to its .NET host (Views protocol 4, specs/application/README.md, "Host bridge (page)"). */
  readonly bridge: RunicBridgeClient;
}

// What disposing an instance ends, in its slots so that every copy of this package adds to the same set, and whether it
// was disposed.
const disposersKey = Symbol.for("runic.views.instance-disposers");
const disposedKey = Symbol.for("runic.views.instance-disposed");
// The instance object of a set of slots, so `currentInstance()` returns the same object each time.
const instanceKey = Symbol.for("runic.views.instance-object");

/**
 * @internal Registers `dispose` to run when the instance with `slots` is disposed; returns a function that unregisters
 * it. The page instance is never disposed, so nothing is kept for it.
 */
export function onInstanceDisposed(slots: Slots, dispose: () => void): () => void {
  if (isPageSlots(slots)) return () => {};
  const set = (slots[disposersKey] ??= new Set<() => void>()) as Set<() => void>;
  set.add(dispose);
  return () => { set.delete(dispose); };
}

/**
 * @internal Slots of an instance that has no Bridge and is already disposed: the default instance of a component
 * bundle without exactly one connected element. A connect call in it rejects as `unavailable`.
 */
export function detachedSlots(): Slots {
  const slots: Slots = Object.create(null) as Slots;
  slots[disposedKey] = true;
  return slots;
}

/** @internal Whether the instance with `slots` was disposed. */
export function isDisposedSlots(slots: Slots): boolean {
  return slots[disposedKey] === true;
}

function instanceFor(slots: Slots): RunicInstance {
  const existing = slots[instanceKey] as RunicInstance | undefined;
  if (existing !== undefined) return existing;
  const instance: RunicInstance = {
    get page() { return isPageSlots(slots); },
    get bridge() { return bridgeOf(slots); },
    run: <T>(run: () => T): T => runInSlots(slots, run),
    dispose() {
      if (isPageSlots(slots)) throw new TypeError("The page instance cannot be disposed.");
      if (slots[disposedKey] === true) return;
      slots[disposedKey] = true;
      const set = slots[disposersKey] as Set<() => void> | undefined;
      for (const dispose of [...set ?? []]) {
        try { dispose(); } catch (error) { console.error(error); }
      }
      set?.clear();
    },
  };
  Object.defineProperty(instance, slotsKey, { value: slots });
  Object.freeze(instance);
  // The page instance object is this copy's own; `globalThis` keeps none.
  if (!isPageSlots(slots)) slots[instanceKey] = instance;
  return instance;
}

/**
 * Creates a Runic instance on `options.bridge`. It shares nothing with the page instance or another instance: not the
 * connection, its routes and operations, the connection state, diagnostics, the translated text and window locale, the
 * appearance, the fields or the kept clients. Code connects its Views in it with {@link RunicInstance.run}, and a
 * framework binding through its instance provider.
 */
export function createRunicInstance(options: RunicInstanceOptions): RunicInstance {
  const bridge = options?.bridge;
  if (bridge === null || typeof bridge !== "object" || typeof bridge.request !== "function"
    || typeof bridge.onMessage !== "function" || typeof bridge.isConnected !== "function")
    throw new TypeError("createRunicInstance needs a Bridge with isConnected, request, onMessage and onConnectionChange.");
  const slots: Slots = Object.create(null) as Slots;
  slots["__runicBridge"] = bridge;
  return instanceFor(slots);
}

let pageInstanceObject: RunicInstance | undefined;

/** The page instance: the document's own, on `globalThis.__runicBridge` and the shared global keys. */
export function pageInstance(): RunicInstance {
  return pageInstanceObject ??= instanceFor(page());
}

/**
 * The instance that code uses outside {@link RunicInstance.run} and outside a framework binding's instance provider.
 * On a page it is the page instance. In a component bundle (`@runic-artifex/views/component`), which carries its own
 * copy of this package, it is the instance of the bundle's connected component element while exactly one is connected,
 * and otherwise an instance without a Bridge, never the host page's.
 */
export function defaultInstance(): RunicInstance {
  const slots = defaultSlots();
  return isPageSlots(slots) ? pageInstance() : instanceFor(slots);
}

/** The current instance: that of the innermost {@link RunicInstance.run}, or the {@link defaultInstance}. */
export function currentInstance(): RunicInstance {
  const slots = currentSlots();
  return isPageSlots(slots) ? pageInstance() : instanceFor(slots);
}

/** @internal The slots of an instance, also one another copy of this package created. */
export function slotsOf(instance: RunicInstance): Slots {
  const slots = (instance as unknown as Record<symbol, Slots | undefined>)?.[slotsKey];
  if (slots === undefined) throw new TypeError("Not a Runic instance: create one with createRunicInstance().");
  return slots;
}
