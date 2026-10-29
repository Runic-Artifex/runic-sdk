// The `stale-input` development warning (views-protocol-4 design, section 2.4 item 5).
//
// A text field bound with a command per key can lose typed characters: the user types
// "ab", the command for "a" is still in flight, and the framework writes the model's older
// "a" back into the focused field. Frameworks (React, the Angular renderer, Vue, Svelte)
// write through the `value` property, so wrapping the `value` setters of
// `HTMLInputElement` and `HTMLTextAreaElement` sees every such write.
//
// The warning fires when script sets `.value` on the focused field to a value that
//   - differs from the field's current value and from the user's latest value, and
//   - the field had earlier in the user's current typing burst,
// while a Bridge request is in flight. A value equal to an earlier burst value counts as
// stale even when it looks like a normalization (the user typed "ab " after "ab", and
// script sets "ab"): the check compares values, not intent.
//
// The warning never carries the typed value, which may be a secret: only the element's
// tag and its id or name.

/** Set to `false` on `globalThis` to turn the warning off, before or after this module loads. */
export const staleInputWarningKey = Symbol.for("runic.vite.staleInputWarning");
// The installed state, shared by every evaluation of this module (HMR, duplicate copies),
// so the setters and the Bridge are wrapped once per page.
const installedKey = Symbol.for("runic.vite.staleInput");
const viewsDiagnosticsKey = Symbol.for("runic.views.diagnostics");

/**
 * A typing burst ends on blur or after this long without an `input` event. One second
 * covers a normal Bridge round trip while the user keeps typing, and a script write
 * after a pause is no longer a race with the user's keystrokes.
 */
export const burstQuietPeriodMs = 1_000;
// Bounds the values remembered per burst; older values are dropped first.
const maximumBurstValues = 64;
const maximumNameLength = 40;

export const staleInputMessage =
  "to a value it had before the user's last edit while a Bridge request was in flight, which loses " +
  "typed characters. Bind text inputs with the text-field binding of your framework package " +
  "(`injectTextField` in @runic-artifex/angular, `useTextField` in @runic-artifex/react, vue or svelte) " +
  "or a setter instead of a command per key. This warning comes from @runic-artifex/vite-plugin-runic " +
  "in development; turn it off with runic({ staleInputWarning: false }) or, without runic(), by setting " +
  "globalThis[Symbol.for(\"runic.vite.staleInputWarning\")] = false.";

interface Burst {
  readonly values: Set<string>;
  latest: string | undefined;
  lastInput: number;
}

interface EventTargetLike {
  addEventListener(type: string, listener: (event: { target?: unknown }) => void, capture?: boolean): void;
}

interface BridgeLike {
  request?: unknown;
}

type StaleInputHost = Record<PropertyKey, unknown> & {
  HTMLInputElement?: abstract new () => object;
  HTMLTextAreaElement?: abstract new () => object;
  document?: EventTargetLike & { activeElement?: unknown };
  console?: { warn(...data: unknown[]): void };
  __runicBridge?: unknown;
};

export interface StaleInputOptions {
  /** The clock, in milliseconds; tests replace it. */
  readonly now?: () => number;
}

/**
 * Installs the warning on `host` (normally `globalThis`) once. Does nothing without a DOM,
 * when it is already installed, or when `host[staleInputWarningKey]` is `false`.
 * Returns whether this call installed it.
 */
export function installStaleInputWarning(host: object, options: StaleInputOptions = {}): boolean {
  const scope = host as StaleInputHost;
  if (scope[installedKey] !== undefined || scope[staleInputWarningKey] === false) return false;
  const inputClass = scope.HTMLInputElement;
  const textAreaClass = scope.HTMLTextAreaElement;
  const document = scope.document;
  if (typeof inputClass !== "function" || typeof textAreaClass !== "function" ||
      typeof document?.addEventListener !== "function") return false;
  const now = options.now ?? (() => Date.now());

  const bursts = new WeakMap<object, Burst>();
  const warned = new WeakSet<object>();
  // "In flight" counts the Bridge's `request` promises from call until they settle.
  let inFlight = 0;
  let wrappedBridge: object | undefined;
  const wrappers = new WeakSet<object>();
  const unwrappable = new WeakSet<object>();

  // The host script, or a mock bridge, may install `__runicBridge` after this client runs
  // or replace it later, so the Bridge is (re)wrapped lazily: on every user edit, before
  // the application handles it, and on every value write.
  const wrapBridge = (): void => {
    const bridge = scope.__runicBridge as BridgeLike | undefined;
    if (typeof bridge !== "object" || bridge === null || unwrappable.has(bridge)) return;
    const request = bridge.request;
    if (typeof request !== "function") return;
    if (bridge === wrappedBridge && wrappers.has(request)) return;
    if (wrappers.has(request)) { wrappedBridge = bridge; return; }
    const original = request as (this: unknown, ...args: unknown[]) => unknown;
    const wrapper = function (this: unknown, ...args: unknown[]): unknown {
      inFlight += 1;
      let result: unknown;
      try { result = original.apply(this, args); }
      catch (error) { inFlight -= 1; throw error; }
      const settled = (): void => { inFlight -= 1; };
      // The caller keeps the original promise; only this observer waits for it.
      Promise.resolve(result).then(settled, settled);
      return result;
    };
    wrappers.add(wrapper);
    try { bridge.request = wrapper; } catch { /* not writable */ }
    if (bridge.request === wrapper) wrappedBridge = bridge;
    else unwrappable.add(bridge);
  };

  const isField = (target: unknown): target is object =>
    target instanceof inputClass || target instanceof textAreaClass;

  const record = (target: object, value: string): void => {
    const time = now();
    let burst = bursts.get(target);
    if (!burst || time - burst.lastInput > burstQuietPeriodMs) {
      burst = { values: new Set(), latest: undefined, lastInput: time };
      bursts.set(target, burst);
    }
    burst.values.delete(value);
    burst.values.add(value);
    if (burst.values.size > maximumBurstValues) {
      const oldest = burst.values.values().next();
      if (!oldest.done) burst.values.delete(oldest.value);
    }
    burst.lastInput = time;
  };

  // `beforeinput` records the value before the user's edit, so overwriting the first
  // typed character with the value from before it counts too.
  document.addEventListener("beforeinput", (event) => {
    wrapBridge();
    const target = event.target;
    if (isField(target)) record(target, String((target as { value?: unknown }).value ?? ""));
  }, true);
  document.addEventListener("input", (event) => {
    wrapBridge();
    const target = event.target;
    if (!isField(target)) return;
    const value = String((target as { value?: unknown }).value ?? "");
    record(target, value);
    const burst = bursts.get(target);
    if (burst) burst.latest = value;
  }, true);
  document.addEventListener("focusout", (event) => {
    if (typeof event.target === "object" && event.target !== null) bursts.delete(event.target);
  }, true);

  const check = (element: object, current: string, next: string): void => {
    if (scope[staleInputWarningKey] === false || inFlight <= 0 || warned.has(element)) return;
    if (document.activeElement !== element || next === current) return;
    const burst = bursts.get(element);
    if (!burst || burst.latest === undefined || now() - burst.lastInput > burstQuietPeriodMs) return;
    if (next === burst.latest || !burst.values.has(next)) return;
    warned.add(element);
    report(scope, element);
  };

  for (const prototype of [inputClass.prototype, textAreaClass.prototype]) {
    const descriptor = Object.getOwnPropertyDescriptor(prototype, "value");
    const { get, set } = descriptor ?? {};
    if (!descriptor || !get || !set || !descriptor.configurable) continue;
    Object.defineProperty(prototype, "value", {
      ...descriptor,
      get,
      set(this: object, value: unknown) {
        try {
          wrapBridge();
          // The setters treat null as the empty string.
          check(this, String(get.call(this)), value === null ? "" : String(value));
        } catch { /* the warning never breaks a write */ }
        set.call(this, value);
      },
    });
  }
  scope[installedKey] = true;
  return true;
}

function describe(element: object): string {
  const field = element as { tagName?: unknown; id?: unknown; getAttribute?: (name: string) => unknown };
  const tag = typeof field.tagName === "string" && field.tagName.toLowerCase() === "textarea" ? "textarea" : "input";
  const name = typeof field.getAttribute === "function" ? field.getAttribute("name") : undefined;
  for (const [attribute, value] of [["id", field.id], ["name", name]] as const) {
    if (typeof value !== "string") continue;
    const clean = value.replace(/[^\w.:-]/gu, "").slice(0, maximumNameLength);
    if (clean) return `<${tag} ${attribute}="${clean}">`;
  }
  return `<${tag}>`;
}

function report(scope: StaleInputHost, element: object): void {
  const message = `A script set the focused ${describe(element)} ${staleInputMessage}`;
  scope.console?.warn(message);
  const listeners = (scope[viewsDiagnosticsKey] ??= new Set()) as Set<(diagnostic: unknown) => void>;
  for (const listener of [...listeners]) {
    try { listener({ kind: "error", code: "stale-input", message }); }
    catch { /* a listener's failure does not stop the others */ }
  }
}
