// Runic Inspect before the first click (#976): which of the analyzed event bindings (handlers.ts, served at
// `/__runic/handlers`) belong to an element, found through the framework's development metadata.
//
// - React: the element's `_debugStack` (where its JSX was created), mapped to the original file and line; then the
//   components around it, for `<Button onClick={…}>` whose `<button>` is in another file.
// - Svelte: `__svelte_meta.loc`, the element's file, line and column; then the components around it.
// - Vue: the element's listeners (`_vei`) with their compiled handlers, in the files of its component and the
//   components around it (a listener on `<MyButton @click>` falls through to its root element).
// - Angular: `ng.getListeners(element)` with the compiled template listeners, in the file of the component.
//
// Nothing here is an API of the frameworks; each lookup is best effort and gives up quietly.

import type { RunicHandlerAnalysis, RunicHandlerBinding, RunicHandlerCall } from "./handlers.js";
import { absolutePath, applicationFrame, parseStack, resolveFrame, type AngularDebug, type NodeLike, type StackFrame } from "./inspect-frameworks.js";

/** Where an element's bindings are: a source position (React, Svelte) or its listeners in components' files (Vue, Angular). */
export type HandlerProbe =
  | { readonly kind: "location"; readonly file: string; readonly line: number; readonly column: number; readonly tag: string;
      readonly events?: readonly string[] }
  | { readonly kind: "listeners"; readonly framework: "vue" | "angular"; readonly files: readonly string[]; readonly tag: string;
      readonly listeners: readonly {
        readonly event: string; readonly text: string; readonly value?: unknown;
        /**
         * Where the element is among the elements of its component with the same tag and a listener for the event,
         * in document order (Angular, whose listeners do not show their template code).
         */
        readonly position?: { readonly index: number; readonly count: number };
      }[];
      /** A value of the component of `file`, by a template path such as `increment` or `step.commit`. */
      readonly resolve?: (file: string, path: string) => unknown };

/** What the analysis says about an element. */
export interface SourceBindings {
  readonly bindings: readonly RunicHandlerBinding[];
  readonly calls: readonly RunicHandlerCall[];
  readonly external: readonly { readonly name: string; readonly source: string }[];
  /** Files whose analysis was not available, with the server's note. */
  readonly unavailable: readonly string[];
}

const maximumComponents = 3;

function reactFiberOf(node: NodeLike): Record<string, unknown> | undefined {
  for (const key of Object.keys(node)) if (key.startsWith("__reactFiber$")) return node[key] as Record<string, unknown>;
  return undefined;
}

function reactEvents(node: NodeLike): string[] {
  for (const key of Object.keys(node)) {
    if (!key.startsWith("__reactProps$")) continue;
    const props = node[key] as Record<string, unknown> | null;
    return props === null || typeof props !== "object" ? [] : Object.keys(props).filter((name) => /^on[A-Z]/u.test(name)
      && typeof props[name] === "function").map((name) => name.slice(2).replace(/Capture$/u, "").toLowerCase());
  }
  return [];
}

function reactTag(type: unknown): string {
  if (typeof type === "string") return type;
  if (typeof type === "function") return (type as { displayName?: string; name?: string }).displayName ?? (type as { name?: string }).name ?? "";
  if (typeof type === "object" && type !== null) {
    const wrapped = type as { displayName?: unknown; render?: unknown; type?: unknown };
    return typeof wrapped.displayName === "string" ? wrapped.displayName : reactTag(wrapped.render ?? wrapped.type);
  }
  return "";
}

function debugFrame(fiber: Record<string, unknown>): StackFrame | undefined {
  try {
    const stack = (fiber["_debugStack"] as { stack?: unknown } | null | undefined)?.stack;
    return typeof stack === "string" ? applicationFrame(parseStack(stack)) : undefined;
  } catch {
    return undefined;
  }
}

/** The element's React probes: its own JSX, then up to three components around it. */
export async function reactProbes(
  node: NodeLike, root: string | undefined, fetchText: (url: string) => Promise<string | undefined>,
): Promise<HandlerProbe[]> {
  const fiber = reactFiberOf(node);
  if (fiber === undefined) return [];
  const events = reactEvents(node);
  const probes: HandlerProbe[] = [];
  let components = 0;
  for (let current: Record<string, unknown> | null | undefined = fiber; current && components <= maximumComponents;
    current = current["return"] as Record<string, unknown> | null | undefined) {
    const host = current === fiber;
    if (!host && typeof current["type"] === "string") continue;
    if (!host && (typeof current["type"] !== "function" && typeof current["type"] !== "object")) continue;
    if (!host) components++;
    const frame = debugFrame(current);
    if (frame === undefined) continue;
    try {
      const location = await resolveFrame(frame, root, fetchText);
      probes.push({ kind: "location", file: location.file, line: location.line, column: location.column, tag: reactTag(current["type"]), events });
    } catch { /* next */ }
  }
  return probes;
}

interface SvelteMeta {
  readonly loc?: { readonly file?: unknown; readonly line?: unknown; readonly column?: unknown };
  readonly parent?: SvelteParent | null;
}
interface SvelteParent {
  readonly type?: unknown; readonly file?: unknown; readonly line?: unknown; readonly column?: unknown;
  readonly componentTag?: unknown; readonly parent?: SvelteParent | null;
}

/** The element's Svelte probes: its own position, then the component tags around it. */
export function svelteProbes(node: NodeLike, root: string | undefined): HandlerProbe[] {
  const meta = node["__svelte_meta"] as SvelteMeta | undefined;
  const loc = meta?.loc;
  if (typeof loc?.file !== "string" || typeof loc.line !== "number") return [];
  const tag = typeof node["localName"] === "string" ? node["localName"] : typeof node["tagName"] === "string" ? (node["tagName"] as string).toLowerCase() : "";
  const probes: HandlerProbe[] = [{
    kind: "location", file: absolutePath(loc.file, root), line: loc.line,
    column: (typeof loc.column === "number" ? loc.column : 0) + 1, tag,
  }];
  let components = 0;
  for (let parent = meta!.parent; parent && components < maximumComponents; parent = parent.parent) {
    if (parent.type !== "component" || typeof parent.file !== "string" || typeof parent.line !== "number") continue;
    components++;
    probes.push({
      kind: "location", file: absolutePath(parent.file, root), line: parent.line,
      column: (typeof parent.column === "number" ? parent.column : 0) + 1,
      tag: typeof parent.componentTag === "string" ? parent.componentTag : "",
    });
  }
  return probes;
}

interface VueInstance {
  readonly type?: { readonly __file?: unknown };
  readonly parent?: VueInstance | null;
  readonly setupState?: Record<string, unknown>;
  readonly proxy?: Record<string, unknown> | null;
}

function functionText(value: unknown): string {
  try { return typeof value === "function" ? Function.prototype.toString.call(value) : ""; }
  catch { return ""; }
}

/** The element's Vue probe: its listeners, in the files of its component and the components around it. */
export function vueProbes(node: NodeLike, root: string | undefined): HandlerProbe[] {
  const instance = node["__vueParentComponent"] as VueInstance | undefined;
  if (typeof instance !== "object" || instance === null) return [];
  const listeners: { event: string; text: string; value?: unknown }[] = [];
  const add = (key: string, value: unknown): void => {
    // Vue keys are `onClick`, `onClickOnce`, `onUpdate:modelValue`.
    if (!/^on[A-Z]/u.test(key)) return;
    const event = key.slice(2).replace(/(?:Once|Passive|Capture)+$/u, "").toLowerCase();
    for (const handler of Array.isArray(value) ? value : [value]) {
      if (typeof handler === "function" && !listeners.some((listener) => listener.value === handler)) {
        listeners.push({ event, text: functionText(handler), value: handler });
      }
    }
  };
  // The element's event invokers: `el[Symbol("_vei")]` in Vue 3.4+, `el._vei` before.
  const veiKey = Object.getOwnPropertySymbols(node).find((symbol) => symbol.description === "_vei");
  const invokers = (veiKey === undefined ? node["_vei"] : (node as Record<symbol, unknown>)[veiKey]) as
    Record<string, { value?: unknown } | undefined> | undefined;
  if (typeof invokers === "object" && invokers !== null) for (const [key, invoker] of Object.entries(invokers)) add(key, invoker?.value);
  // The element's VNode props in development, with the compiled handlers.
  const props = (node["__vnode"] as { props?: Record<string, unknown> | null } | undefined)?.props;
  if (typeof props === "object" && props !== null) for (const [key, value] of Object.entries(props)) add(key, value);
  if (listeners.length === 0) return [];
  const instances = new Map<string, VueInstance>();
  for (let current: VueInstance | null | undefined = instance; current && instances.size < maximumComponents; current = current.parent) {
    const file = current.type?.__file;
    if (typeof file === "string" && !instances.has(absolutePath(file, root))) instances.set(absolutePath(file, root), current);
  }
  if (instances.size === 0) return [];
  const tag = typeof node["localName"] === "string" ? node["localName"] : "";
  return [{
    kind: "listeners", framework: "vue", files: [...instances.keys()], tag, listeners,
    resolve: (file, path) => {
      const owner = instances.get(file);
      let value: unknown = owner?.setupState ?? owner?.proxy;
      for (const part of path.split(".")) {
        if (typeof value !== "object" || value === null) return undefined;
        try { value = (value as Record<string, unknown>)[part]; } catch { return undefined; }
      }
      return value;
    },
  }];
}

interface AngularListener { readonly name?: unknown; readonly callback?: unknown; readonly type?: unknown }
interface AngularDebugWithListeners extends AngularDebug { getListeners?(element: unknown): unknown }

function angularFile(instance: unknown, root: string | undefined): string | undefined {
  const type = typeof instance === "object" && instance !== null
    ? (instance as { constructor?: { ɵcmp?: { debugInfo?: { filePath?: unknown } } } }).constructor : undefined;
  const file = type?.ɵcmp?.debugInfo?.filePath;
  return typeof file === "string" ? absolutePath(file, root) : undefined;
}

/**
 * The element's Angular probe: `ng.getListeners`, in the file of its component and the components around it. Angular
 * 17+ gives the listener's wrapper, not the template's code, so each listener carries the element's position among
 * its component's elements with the same tag and event.
 */
export function angularProbes(node: NodeLike, ng: AngularDebugWithListeners | undefined, root: string | undefined): HandlerProbe[] {
  if (ng === undefined || typeof ng.getListeners !== "function" || typeof ng.getOwningComponent !== "function") return [];
  const listeners: { event: string; text: string; position?: { index: number; count: number } }[] = [];
  const files: string[] = [];
  const tag = typeof node["localName"] === "string" ? node["localName"] : "";
  const events = (element: unknown): string[] => {
    try {
      return ((ng.getListeners!(element) as AngularListener[] | undefined) ?? [])
        .filter((listener) => typeof listener.name === "string" && listener.type !== "output").map((listener) => (listener.name as string).toLowerCase());
    } catch {
      return [];
    }
  };
  try {
    const owner = ng.getOwningComponent(node);
    const host = owner === undefined || owner === null ? undefined : ng.getHostElement?.(owner) as
      { querySelectorAll?(selector: string): ArrayLike<unknown> } | undefined;
    const peers = typeof host?.querySelectorAll === "function" && /^[a-z][\w-]*$/u.test(tag)
      ? Array.from(host.querySelectorAll(tag)).filter((element) => ng.getOwningComponent!(element) === owner) : [];
    for (const listener of (ng.getListeners(node) as AngularListener[] | undefined) ?? []) {
      if (typeof listener.name !== "string" || listener.type === "output") continue;
      const event = listener.name.toLowerCase();
      const same = peers.filter((element) => events(element).includes(event));
      const index = same.indexOf(node);
      listeners.push({ event, text: functionText(listener.callback), ...(index === -1 ? {} : { position: { index, count: same.length } }) });
    }
    let instance = owner;
    for (let depth = 0; instance && depth < maximumComponents; depth++) {
      const file = angularFile(instance, root);
      if (file !== undefined && !files.includes(file)) files.push(file);
      const parent = (ng.getHostElement?.(instance) as NodeLike | undefined)?.parentElement;
      instance = parent ? ng.getOwningComponent(parent) : undefined;
    }
  } catch { /* best effort */ }
  if (listeners.length === 0 || files.length === 0) return [];
  return [{ kind: "listeners", framework: "angular", files, tag, listeners }];
}

/** All probes of an element, by whichever framework rendered it. */
export async function findProbes(
  node: NodeLike, options: { readonly root?: string | undefined; readonly ng?: AngularDebug | undefined;
    readonly fetchText: (url: string) => Promise<string | undefined> },
): Promise<HandlerProbe[]> {
  const react = await reactProbes(node, options.root, options.fetchText);
  if (react.length > 0) return react;
  const svelte = svelteProbes(node, options.root);
  if (svelte.length > 0) return svelte;
  const vue = vueProbes(node, options.root);
  if (vue.length > 0) return vue;
  return angularProbes(node, options.ng as AngularDebugWithListeners | undefined, options.root);
}

/** The files the probes need the analysis of. */
export function probeFiles(probes: readonly HandlerProbe[]): string[] {
  return [...new Set(probes.flatMap((probe) => probe.kind === "location" ? [probe.file] : probe.files))];
}

// Matching ----------------------------------------------------------------------------------------------------------

/**
 * Handler text as tokens, without what the compilers add: `$setup.`/`_ctx.` (Vue), `ctx.`/`ctx_r1.`/`i0.` and
 * `ɵɵresetView` (Angular), `return`, parentheses and semicolons. Tokens are separated by single spaces, with a space
 * at both ends, so that a handler is found in compiled code only as whole tokens.
 */
export function normalizeHandler(text: string): string {
  const stripped = text.replace(/(?<![\w$])(?:_ctx|\$setup|\$props|\$data|\$options|__props|ctx(?:_r\d+)?|i\d+)\./gu, "")
    .replace(/(?<![\w$])(?:ɵɵ)?resetView(?![\w$])/gu, "");
  const tokens = stripped.match(/[\w$\u0080-\uffff]+|"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'|\S/gu) ?? [];
  return ` ${tokens.filter((token) => token !== "(" && token !== ")" && token !== ";" && token !== "return")
    .map((token) => /^'.*'$/su.test(token) ? `"${token.slice(1, -1)}"` : token).join(" ")} `;
}

function containsHandler(compiled: string, expression: string): boolean {
  return expression.trim().length > 0 && compiled.includes(expression);
}

function locationMatches(probe: Extract<HandlerProbe, { kind: "location" }>, bindings: readonly RunicHandlerBinding[]): RunicHandlerBinding[] {
  const onLine = bindings.filter((binding) => binding.file === probe.file && binding.line === probe.line);
  const exact = onLine.filter((binding) => binding.column === probe.column);
  if (exact.length > 0) return exact;
  const tagged = onLine.filter((binding) => binding.tag === probe.tag || binding.tag.toLowerCase() === probe.tag.toLowerCase());
  const candidates = tagged.length > 0 ? tagged : onLine;
  if (candidates.length === 0) return [];
  // The nearest element on the line.
  const columns = [...new Set(candidates.map((binding) => binding.column))]
    .sort((a, b) => Math.abs(a - probe.column) - Math.abs(b - probe.column));
  return candidates.filter((binding) => binding.column === columns[0]);
}

/**
 * An analysis with the path it was asked for: Angular names a component's file relative to the project (under the
 * Angular CLI the client has no root), the analysis by its absolute path.
 */
export type RequestedAnalysis = RunicHandlerAnalysis & { readonly requested?: string };

function listenerMatches(probe: Extract<HandlerProbe, { kind: "listeners" }>, analyses: readonly RequestedAnalysis[]): RunicHandlerBinding[] {
  const matched = new Set<RunicHandlerBinding>();
  // An Angular component's bindings are in its template file, which the analysis of the component's script names.
  const owners = new Map<RunicHandlerBinding, string>();
  for (const analysis of analyses) {
    const file = probe.files.find((candidate) => candidate === analysis.file || candidate === analysis.requested);
    if (file !== undefined) for (const binding of analysis.bindings) owners.set(binding, file);
  }
  for (const listener of probe.listeners) {
    const candidates = [...owners.keys()].filter((binding) => binding.event === listener.event);
    if (candidates.length === 0) continue;
    const compiled = normalizeHandler(listener.text);
    const byText = candidates.filter((binding) => containsHandler(compiled, normalizeHandler(binding.expression)));
    const byValue = probe.resolve === undefined || listener.value === undefined ? [] : candidates.filter((binding) =>
      /^[\w$]+(?:\.[\w$]+)*$/u.test(binding.expression.trim()) && probe.resolve!(owners.get(binding)!, binding.expression.trim()) === listener.value);
    let found = byText.length > 0 ? byText : byValue;
    if (found.length === 0) {
      // Not recognizable in the compiled code: the element's binding of the event in its own component, when there
      // is one, by its position when the template has as many as the page shows, or all when they reach the same calls.
      const own = candidates.filter((binding) => binding.tag.toLowerCase() === probe.tag && owners.get(binding) === probe.files[0]);
      const signature = (binding: RunicHandlerBinding): string => binding.calls.map((entry) => entry.member).sort().join(",");
      if (own.length === 1) found = own;
      else if (own.length > 1 && listener.position !== undefined && listener.position.count === own.length) found = [own[listener.position.index]!];
      else if (own.length > 1 && own.every((binding) => signature(binding) === signature(own[0]!))) found = own;
    }
    for (const binding of found) matched.add(binding);
  }
  return [...matched];
}

/**
 * The bindings of an element: of its own position or listeners first; components around it only while nothing with
 * calls matched (`<Button onClick={…}>` around a `<button onClick={props.onClick}>`).
 */
export function matchBindings(probes: readonly HandlerProbe[], analyses: readonly RequestedAnalysis[]): SourceBindings {
  const all = analyses.flatMap((analysis) => analysis.bindings);
  const bindings: RunicHandlerBinding[] = [];
  for (const probe of probes) {
    let found = probe.kind === "location" ? locationMatches(probe, all) : listenerMatches(probe, analyses);
    if (probe.kind === "location" && probe !== probes[0] && probe.events !== undefined && probe.events.length > 0) {
      found = found.filter((binding) => probe.events!.includes(binding.event));
    }
    bindings.push(...found.filter((binding) => !bindings.includes(binding)));
    if (bindings.some((binding) => binding.calls.length > 0)) break;
  }
  const calls = new Map<string, RunicHandlerCall>();
  const external = new Map<string, string>();
  for (const binding of bindings) {
    for (const call of binding.calls) if (!calls.has(call.member)) calls.set(call.member, call);
    for (const entry of binding.external) external.set(entry.name, entry.source);
  }
  return {
    bindings, calls: [...calls.values()], external: [...external].map(([name, source]) => ({ name, source })),
    unavailable: analyses.filter((analysis) => analysis.note !== undefined).map((analysis) => `${analysis.file}: ${analysis.note}`),
  };
}
