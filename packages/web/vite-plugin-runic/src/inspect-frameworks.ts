// Runic Inspect: an element's framework component, from each framework's own development metadata. Nothing here is
// an API of the frameworks; each lookup is best effort and gives up quietly.
//
// - React 19: the element's fiber (`__reactFiber$…`), its owning components, and its `_debugStack` (the stack where
//   the JSX element was created), resolved to the original file through the served module's source map.
// - Vue 3: `__vueParentComponent` and the component's `__file` (file only).
// - Svelte 5: `__svelte_meta.loc`, the element's `{ file, line, column }` in its component.
// - Angular: `ng.getComponent`/`ng.getOwningComponent` and the class's `ɵcmp.debugInfo` (Angular 17.1+).

import type { RunicInspectComponent, RunicInspectLocation } from "./inspect-data.js";

/** What the lookups read from a DOM node; plain objects stand in for elements in tests. */
export type NodeLike = Record<string, unknown> & { readonly parentElement?: NodeLike | null };

const maximumOwners = 8;
const maximumAncestors = 50;

function basename(file: string): string {
  const name = file.split(/[\\/]/u).pop() ?? file;
  const dot = name.lastIndexOf(".");
  return dot > 0 ? name.slice(0, dot) : name;
}

/** `path` as an absolute file path: relative paths are relative to the Vite root. */
export function absolutePath(path: string, root: string | undefined): string {
  if (root === undefined || /^(?:[a-z]:)?[\\/]/iu.test(path)) return path;
  return `${root.replace(/[\\/]+$/u, "")}/${path.replace(/^\.\//u, "")}`;
}

// React -------------------------------------------------------------------------------------------------------------

interface Fiber {
  readonly tag?: number;
  readonly type?: unknown;
  readonly return?: Fiber | null;
  readonly _debugStack?: { readonly stack?: unknown } | null;
}

function reactFiber(node: NodeLike): Fiber | undefined {
  for (const key of Object.keys(node)) {
    if (key.startsWith("__reactFiber$")) return node[key] as Fiber;
  }
  return undefined;
}

function reactName(type: unknown): string | undefined {
  if (typeof type === "function") {
    const named = type as { displayName?: unknown; name?: unknown };
    return typeof named.displayName === "string" ? named.displayName : typeof named.name === "string" && named.name !== "" ? named.name : undefined;
  }
  if (typeof type === "object" && type !== null) {
    const wrapped = type as { displayName?: unknown; render?: unknown; type?: unknown };
    if (typeof wrapped.displayName === "string") return wrapped.displayName;
    return reactName(wrapped.render) ?? reactName(wrapped.type);
  }
  return undefined;
}

/** The React component of an element and its owners, with the element fiber's `_debugStack`. */
export function reactComponent(node: NodeLike): { component: RunicInspectComponent; stack: string | undefined } | undefined {
  const fiber = reactFiber(node);
  if (fiber === undefined) return undefined;
  const owners: string[] = [];
  for (let current = fiber.return; current && owners.length <= maximumOwners; current = current.return) {
    // Host components (strings) and React's own wrappers (Fragment, Context, Suspense) have no name of their own.
    if (typeof current.type === "string") continue;
    const name = reactName(current.type);
    if (name !== undefined) owners.push(name);
  }
  if (owners.length === 0) return undefined;
  let stack: string | undefined;
  try {
    const value = fiber._debugStack?.stack;
    if (typeof value === "string") stack = value;
  } catch { /* Not readable. */ }
  return { component: { framework: "react", name: owners[0]!, owners: owners.slice(1) }, stack };
}

/** A frame of an `Error.stack` in the V8 (`at f (url:1:2)`) or JavaScriptCore/Gecko (`f@url:1:2`) form. */
export interface StackFrame {
  readonly name: string;
  readonly url: string;
  /** 1-based. */
  readonly line: number;
  /** 1-based. */
  readonly column: number;
}

export function parseStack(stack: string): StackFrame[] {
  const frames: StackFrame[] = [];
  for (const raw of stack.split("\n")) {
    const line = raw.trim();
    const v8 = /^at (?:(.*?) \()?(?:async )?(.+?):(\d+):(\d+)\)?$/u.exec(line);
    const other = v8 === null ? /^(.*?)@(.+?):(\d+):(\d+)$/u.exec(line) : null;
    const match = v8 ?? other;
    if (match === null) continue;
    frames.push({ name: match[1] ?? "", url: match[2]!, line: Number(match[3]), column: Number(match[4]) });
  }
  return frames;
}

/** The first frame of application code: not React's, not a pre-bundled dependency, not a Vite client module. */
export function applicationFrame(frames: readonly StackFrame[]): StackFrame | undefined {
  return frames.find((frame) => /^https?:/u.test(frame.url)
    && !/\/node_modules\/|\/\.vite\/deps\/|\/@vite\/|\/@id\/|\/@react-refresh/u.test(frame.url));
}

// Source maps: the inline map Vite adds to each served module, decoded far enough to map one position.

const base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const base64Values = new Map([...base64].map((character, index) => [character, index]));

/** Decodes source map `mappings`: per generated line, segments of `[column, source, line, column]` (0-based, absolute). */
export function decodeMappings(mappings: string): number[][][] {
  const lines: number[][][] = [];
  let source = 0, originalLine = 0, originalColumn = 0;
  for (const text of mappings.split(";")) {
    const segments: number[][] = [];
    let column = 0;
    for (const segmentText of text.split(",")) {
      if (segmentText.length === 0) continue;
      const values: number[] = [];
      let value = 0, shift = 0;
      for (const character of segmentText) {
        const digit = base64Values.get(character);
        if (digit === undefined) return lines;
        value += (digit & 31) << shift;
        if (digit & 32) { shift += 5; continue; }
        values.push(value & 1 ? -(value >>> 1) : value >>> 1);
        value = 0; shift = 0;
      }
      column += values[0] ?? 0;
      if (values.length >= 4) {
        source += values[1]!; originalLine += values[2]!; originalColumn += values[3]!;
        segments.push([column, source, originalLine, originalColumn]);
      }
    }
    lines.push(segments);
  }
  return lines;
}

export interface SourceMapLike {
  readonly sources: readonly (string | null)[];
  readonly sourceRoot?: string;
  readonly mappings: string;
}

/** The original `{ source, line, column }` (1-based) of a generated 1-based position, or undefined. */
export function originalPosition(map: SourceMapLike, line: number, column: number): { source: string; line: number; column: number } | undefined {
  const segments = decodeMappings(map.mappings)[line - 1];
  if (segments === undefined || segments.length === 0) return undefined;
  let best: number[] | undefined;
  for (const segment of segments) {
    if (segment[0]! <= column - 1) best = segment;
    else break;
  }
  best ??= segments[0];
  const source = map.sources[best![1]!];
  if (typeof source !== "string") return undefined;
  const root = map.sourceRoot ?? "";
  return { source: root === "" || /^(?:[a-z]+:|\/)/iu.test(source) ? source : `${root.replace(/\/?$/u, "/")}${source}`, line: best![2]! + 1, column: best![3]! + 1 };
}

// Assembled, so that this file's own text never contains a source map comment that js-debug and other tools, which
// take the last one they find anywhere in a file, would read as this file's source map (#1050).
const inlineSourceMapComment = new RegExp(String.raw`//[#@] ${"sourceMappingURL"}=data:application/json[^,]*;base64,([A-Za-z0-9+/=]+)\s*$`, "u");

/** The inline source map of a served module's code: the base64 JSON `data:` URL of its trailing source map comment. */
export function inlineSourceMap(code: string): SourceMapLike | undefined {
  const match = inlineSourceMapComment.exec(code);
  if (match === null) return undefined;
  try {
    const binary = atob(match[1]!);
    const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
    const map = JSON.parse(new TextDecoder().decode(bytes)) as SourceMapLike;
    return typeof map.mappings === "string" && Array.isArray(map.sources) ? map : undefined;
  } catch {
    return undefined;
  }
}

/**
 * The file of a source map's `source` for a module served at `url`: an absolute path as it is (Vite's maps name the
 * file), a path relative to the module resolved as a URL path below the Vite root, `/@fs/` stripped.
 */
export function sourceFile(source: string, url: string, root: string | undefined): string {
  if (root !== undefined && source.startsWith(root)) return source;
  if (/^[a-z]:[\\/]/iu.test(source)) return source;
  if (source.startsWith("/") && root === undefined) return source;
  let path: string;
  try { path = decodeURIComponent(new URL(source, url).pathname); }
  catch { return source; }
  if (path.startsWith("/@fs/")) return path.slice(4);
  // A rooted source outside the Vite root (a monorepo package) is a file path already.
  if (source.startsWith("/") && !source.startsWith("/src/")) return source;
  return absolutePath(path.replace(/^\//u, ""), root);
}

/** Resolves a frame of a served module to its original location through the module's inline source map. */
export async function resolveFrame(
  frame: StackFrame, root: string | undefined, fetchText: (url: string) => Promise<string | undefined>,
): Promise<RunicInspectLocation> {
  const fallback = (): RunicInspectLocation => {
    let path = frame.url;
    try { path = decodeURIComponent(new URL(frame.url).pathname); } catch { /* keep the URL */ }
    return { file: path.startsWith("/@fs/") ? path.slice(4) : absolutePath(path.replace(/^\//u, ""), root),
      line: frame.line, column: frame.column, editor: "vite" };
  };
  const code = await fetchText(frame.url);
  const map = code === undefined ? undefined : inlineSourceMap(code);
  const position = map === undefined ? undefined : originalPosition(map, frame.line, frame.column);
  if (position === undefined) return fallback();
  return { file: sourceFile(position.source, frame.url, root), line: position.line, column: position.column, editor: "vite" };
}

// Vue ---------------------------------------------------------------------------------------------------------------

interface VueInstance {
  readonly type?: { readonly name?: unknown; readonly __name?: unknown; readonly __file?: unknown };
  readonly parent?: VueInstance | null;
}

function vueName(instance: VueInstance): string | undefined {
  const type = instance.type;
  if (type === undefined) return undefined;
  if (typeof type.name === "string" && type.name !== "") return type.name;
  if (typeof type.__name === "string" && type.__name !== "") return type.__name;
  return typeof type.__file === "string" ? basename(type.__file) : undefined;
}

export function vueComponent(node: NodeLike): RunicInspectComponent | undefined {
  const instance = node["__vueParentComponent"] as VueInstance | undefined;
  if (typeof instance !== "object" || instance === null) return undefined;
  const name = vueName(instance) ?? "Anonymous";
  const owners: string[] = [];
  for (let parent = instance.parent; parent && owners.length < maximumOwners; parent = parent.parent) {
    const owner = vueName(parent);
    if (owner !== undefined) owners.push(owner);
  }
  const file = instance.type?.__file;
  return {
    framework: "vue", name, owners,
    ...(typeof file === "string" ? { location: { file, line: 1, column: 1, editor: "vite" as const } } : {}),
  };
}

// Svelte ------------------------------------------------------------------------------------------------------------

interface SvelteMeta {
  readonly loc?: { readonly file?: unknown; readonly line?: unknown; readonly column?: unknown };
  readonly parent?: SvelteParent | null;
}

interface SvelteParent {
  readonly type?: unknown;
  readonly file?: unknown;
  readonly componentTag?: unknown;
  readonly parent?: SvelteParent | null;
}

export function svelteComponent(node: NodeLike, root: string | undefined): RunicInspectComponent | undefined {
  const meta = node["__svelte_meta"] as SvelteMeta | undefined;
  const loc = meta?.loc;
  if (typeof loc?.file !== "string") return undefined;
  const owners: string[] = [];
  for (let parent = meta!.parent; parent && owners.length < maximumOwners; parent = parent.parent) {
    if (parent.type === "component" && typeof parent.file === "string") owners.push(basename(parent.file));
  }
  const line = typeof loc.line === "number" ? loc.line : 1;
  // Svelte's column is 0-based.
  const column = typeof loc.column === "number" ? loc.column + 1 : 1;
  return {
    framework: "svelte", name: basename(loc.file), owners: owners.filter((owner) => owner !== basename(loc.file as string)),
    location: { file: absolutePath(loc.file, root), line, column, editor: "vite" },
  };
}

// Angular -----------------------------------------------------------------------------------------------------------

/** The global `ng` of an Angular application in development mode. */
export interface AngularDebug {
  getComponent?(element: unknown): unknown;
  getOwningComponent?(element: unknown): unknown;
  getHostElement?(component: unknown): unknown;
}

interface AngularClass {
  readonly name?: unknown;
  readonly ɵcmp?: { readonly debugInfo?: { readonly className?: unknown; readonly filePath?: unknown; readonly lineNumber?: unknown } };
}

function angularClass(instance: unknown): AngularClass | undefined {
  return typeof instance === "object" && instance !== null ? (instance as { constructor?: AngularClass }).constructor : undefined;
}

function angularName(type: AngularClass | undefined): string | undefined {
  const className = type?.ɵcmp?.debugInfo?.className;
  if (typeof className === "string") return className;
  return typeof type?.name === "string" && type.name !== "" ? type.name : undefined;
}

export function angularComponent(node: NodeLike, ng: AngularDebug | undefined, root: string | undefined): RunicInspectComponent | undefined {
  if (ng === undefined || typeof ng.getOwningComponent !== "function") return undefined;
  let instance: unknown;
  try { instance = ng.getComponent?.(node) ?? ng.getOwningComponent(node); }
  catch { return undefined; }
  const type = angularClass(instance);
  const name = angularName(type);
  if (name === undefined) return undefined;
  const owners: string[] = [];
  let current = instance;
  try {
    while (owners.length < maximumOwners) {
      const host = ng.getHostElement?.(current) as NodeLike | undefined;
      const parentElement = host?.parentElement;
      if (!parentElement) break;
      current = ng.getOwningComponent(parentElement);
      const owner = angularName(angularClass(current));
      if (owner === undefined) break;
      owners.push(owner);
    }
  } catch { /* Owners are a bonus. */ }
  const debugInfo = type?.ɵcmp?.debugInfo;
  const file = debugInfo?.filePath;
  return {
    framework: "angular", name, owners,
    ...(typeof file === "string" ? {
      location: {
        file: absolutePath(file, root),
        // Angular's line is 0-based.
        line: typeof debugInfo?.lineNumber === "number" ? debugInfo.lineNumber + 1 : 1,
        column: 1, editor: "vite" as const,
      },
    } : {}),
  };
}

// -------------------------------------------------------------------------------------------------------------------

/**
 * The framework component of a node or its nearest ancestor that has one, without a React element's location, and the
 * stack frame that location comes from.
 */
export function findComponentSync(
  start: NodeLike, root: string | undefined, ng: AngularDebug | undefined,
): { readonly component: RunicInspectComponent; readonly frame?: StackFrame } | undefined {
  let node: NodeLike | null | undefined = start;
  for (let depth = 0; node && depth < maximumAncestors; depth++, node = node.parentElement) {
    const react = reactComponent(node);
    if (react !== undefined) {
      const frame = react.stack === undefined ? undefined : applicationFrame(parseStack(react.stack));
      return frame === undefined ? { component: react.component } : { component: react.component, frame };
    }
    const component = svelteComponent(node, root) ?? vueComponent(node) ?? angularComponent(node, ng, root);
    if (component !== undefined) return { component };
  }
  return undefined;
}

/**
 * The framework component of a node or its nearest ancestor that has one. A React element's location needs the
 * module's source map, so `fetchText` reads served modules.
 */
export async function findComponent(
  start: NodeLike, options: { readonly root?: string | undefined; readonly ng?: AngularDebug | undefined;
    readonly fetchText: (url: string) => Promise<string | undefined> },
): Promise<RunicInspectComponent | undefined> {
  const found = findComponentSync(start, options.root, options.ng);
  if (found?.frame === undefined) return found?.component;
  try {
    return { ...found.component, location: await resolveFrame(found.frame, options.root, options.fetchText) };
  } catch {
    return found.component;
  }
}
