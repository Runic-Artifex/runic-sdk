// The development source map (#951): the Bridge generator's `sources.json` in the project's `obj`, which maps every
// Bridge member to its C# symbol and file:line:column. `runic()` serves it at `/__runic/sources` while serving, and
// opens a location from it in the editor at `/__runic/open`. Neither exists in `vite build` or `vite preview`, and
// nothing of the map reaches a bundle.

import type { IncomingMessage, ServerResponse } from "node:http";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { isLoopbackRequest, readLimitedBody, sameOrigin } from "./dev-session.js";

export const sourcesPath = "/__runic/sources";
export const openPath = "/__runic/open";
/** The `sources.json` path that `dotnet runic dev` gives the development server. */
export const sourceMapVariable = "RUNIC_APPLICATION_DEV_SOURCE_MAP";
const projectVariable = "RUNIC_APPLICATION_DEV_PROJECT";
export const sourceMapFormat = "runic-bridge-sources/1";
const sourceMapFileName = "sources.json";
// A larger file is not a map the generator wrote.
const maximumMapBytes = 16 * 1024 * 1024;
const maximumOpenRequestBytes = 4096;

/**
 * A source location: the absolute path of a local file and a 1-based line and column, as in a compiler diagnostic.
 * The generated clients' declaration maps are made from the same locations.
 */
export interface RunicSourceLocation {
  readonly file: string;
  readonly line: number;
  readonly column: number;
}

/** A ViewModel with a generated Bridge. */
export interface RunicSourceViewModel {
  /** The ViewModel's full CLR type name, the part of a generated client's contract before the colon. */
  readonly id: string;
  /** The public name: `Counter` for `CounterViewModel`. */
  readonly name: string;
  /** The route `connect<Name>()` connects: `counter`. A View presented as content connects another route. */
  readonly route: string;
  /** The generated client's contract, `{type}:{fingerprint}`. */
  readonly contract: string;
  /** The `kind` of its page references: the route, with a View contract appended for each contract variant. */
  readonly pageKinds: readonly string[];
  readonly symbol: string;
  readonly location?: RunicSourceLocation;
}

/**
 * What a member is for the page:
 *
 * - `state`: a state member, read through the C# getter;
 * - `setter`: a state member the page sets (`set…` and `write…`), through the C# setter;
 * - `command`: a command; `symbol` and `location` are its handler, such as the `[RelayCommand]` method;
 * - `commandState`: a state member a command adds, such as `canSave`, located at the command's handler;
 * - `interaction` and `request`: an interaction or a View request the page handles.
 */
export type RunicSourceMemberKind = "state" | "setter" | "command" | "commandState" | "interaction" | "request";

export interface RunicSourceMember {
  /** `{viewModel}/{kind}/{member}`. */
  readonly id: string;
  readonly viewModel: string;
  /** The member as the generated client and the Bridge frames name it: `count`, `increment`. */
  readonly member: string;
  readonly kind: RunicSourceMemberKind;
  /** The C# member that runs: `Ns.CounterViewModel.Increment()`, `Ns.CounterViewModel.Step.set`. */
  readonly symbol: string;
  /** For a command whose handler is a method: the command property. */
  readonly property?: string;
  /** Absent when the build had no PDB or no source file for the member. */
  readonly location?: RunicSourceLocation;
}

/** `/__runic/sources`: the `sources.json` the Bridge generator wrote, version `runic-bridge-sources/1`. */
export interface RunicSourceMap {
  readonly format: typeof sourceMapFormat;
  readonly viewModels: readonly RunicSourceViewModel[];
  readonly members: readonly RunicSourceMember[];
}

const memberKinds: ReadonlySet<string> = new Set(["state", "setter", "command", "commandState", "interaction", "request"]);

/**
 * Reads the map where it is: the path `runic({ sourceMap })` names (a `sources.json` or a directory to search), else
 * the one `dotnet runic dev` passes when it exists, else the newest `runic-bridge/sources.json` under `obj` of the project directory
 * `dotnet runic dev` passes, of the Vite root or of its parent. A map is read again only when the file changed.
 */
export function createSourceMapReader(option: string | undefined, root: () => string): () => RunicSourceMap | undefined {
  let cached: { path: string; modified: number; size: number; map: RunicSourceMap | undefined } | undefined;
  return () => {
    const path = locateSourceMap(option, root());
    if (path === undefined) return undefined;
    let stat;
    try { stat = statSync(path); }
    catch { return undefined; }
    if (cached?.path === path && cached.modified === stat.mtimeMs && cached.size === stat.size) return cached.map;
    let map: RunicSourceMap | undefined;
    try { map = stat.size <= maximumMapBytes ? parseSourceMap(JSON.parse(readFileSync(path, "utf8"))) : undefined; }
    catch { map = undefined; }
    cached = { path, modified: stat.mtimeMs, size: stat.size, map };
    return map;
  };
}

function locateSourceMap(option: string | undefined, root: string): string | undefined {
  if (option !== undefined) {
    const path = resolve(root, option);
    if (isFile(path)) return path;
    return newestUnder(join(path, "obj")) ?? newestUnder(path);
  }
  // dotnet runic dev evaluates the path without the build's own properties, so a missing file falls back to a search.
  const passed = process.env[sourceMapVariable];
  if (passed !== undefined && isAbsolute(passed) && isFile(passed)) return passed;
  const project = process.env[projectVariable];
  const directories = project !== undefined && isAbsolute(project) ? [dirname(project)] : [root, dirname(root)];
  for (const directory of directories) {
    const found = newestUnder(join(directory, "obj"));
    if (found !== undefined) return found;
  }
  return undefined;
}

// The newest runic-bridge/sources.json at most three directories below `directory`, as in a project's
// obj/<Configuration>/<TargetFramework>[/<RuntimeIdentifier>]/runic-bridge.
function newestUnder(directory: string): string | undefined {
  let newest: { path: string; modified: number } | undefined;
  const visit = (current: string, depth: number): void => {
    let entries;
    try { entries = readdirSync(current, { withFileTypes: true }); }
    catch { return; }
    for (const entry of entries) {
      if (!entry.isDirectory()) continue;
      const child = join(current, entry.name);
      if (entry.name === "runic-bridge") {
        const candidate = join(child, sourceMapFileName);
        try {
          const modified = statSync(candidate).mtimeMs;
          if (newest === undefined || modified > newest.modified) newest = { path: candidate, modified };
        } catch { /* no map here */ }
      } else if (depth < 3 && entry.name !== "node_modules" && !entry.name.startsWith(".")) {
        visit(child, depth + 1);
      }
    }
  };
  visit(directory, 0);
  return newest?.path;
}

function isFile(path: string): boolean {
  try { return statSync(path).isFile(); }
  catch { return false; }
}

/** The map with its well-formed entries, or undefined when it is not a `runic-bridge-sources/1` map. */
export function parseSourceMap(value: unknown): RunicSourceMap | undefined {
  if (typeof value !== "object" || value === null || (value as { format?: unknown }).format !== sourceMapFormat) return undefined;
  const { viewModels, members } = value as { viewModels?: unknown; members?: unknown };
  const strings = (entry: Record<string, unknown>, ...names: string[]): boolean =>
    names.every((name) => typeof entry[name] === "string");
  return {
    format: sourceMapFormat,
    viewModels: (Array.isArray(viewModels) ? viewModels : []).flatMap((entry: Record<string, unknown>) =>
      typeof entry === "object" && entry !== null && strings(entry, "id", "name", "route", "contract", "symbol")
        && Array.isArray(entry.pageKinds) && entry.pageKinds.every((kind) => typeof kind === "string")
        ? [withLocation({
          id: entry.id, name: entry.name, route: entry.route, contract: entry.contract,
          pageKinds: entry.pageKinds, symbol: entry.symbol,
        } as RunicSourceViewModel, entry.location)]
        : []),
    members: (Array.isArray(members) ? members : []).flatMap((entry: Record<string, unknown>) =>
      typeof entry === "object" && entry !== null && strings(entry, "id", "viewModel", "member", "kind", "symbol")
        && memberKinds.has(entry.kind as string) && (entry.property === undefined || typeof entry.property === "string")
        ? [withLocation({
          id: entry.id, viewModel: entry.viewModel, member: entry.member, kind: entry.kind, symbol: entry.symbol,
          ...(entry.property === undefined ? {} : { property: entry.property }),
        } as RunicSourceMember, entry.location)]
        : []),
  };
}

function withLocation<T extends object>(entry: T, value: unknown): T {
  const location = sourceLocation(value);
  return location === undefined ? entry : { ...entry, location };
}

function sourceLocation(value: unknown): RunicSourceLocation | undefined {
  if (typeof value !== "object" || value === null) return undefined;
  const { file, line, column } = value as Record<string, unknown>;
  return typeof file === "string" && isAbsolute(file) && !file.includes("\0")
    && Number.isSafeInteger(line) && (line as number) > 0 && Number.isSafeInteger(column) && (column as number) > 0
    ? { file, line: line as number, column: column as number }
    : undefined;
}

function answer(response: ServerResponse, status: number, body?: unknown, headers: Record<string, string> = {}): void {
  if (response.writableEnded) return;
  response.statusCode = status;
  response.setHeader("Cache-Control", "no-store");
  for (const [name, value] of Object.entries(headers)) response.setHeader(name, value);
  if (body === undefined) {
    response.end();
    return;
  }
  response.setHeader("Content-Type", "application/json; charset=utf-8");
  response.end(JSON.stringify(body));
}

/**
 * Whether a GET may read the map: a request of this machine, and not a page of another origin. A browser marks a
 * cross-origin fetch with `Sec-Fetch-Site` and `Origin`; neither is required, so that a tool such as `curl` reads it.
 */
function mayRead(request: IncomingMessage): boolean {
  const site = request.headers["sec-fetch-site"];
  if (site !== undefined && site !== "same-origin" && site !== "none") return false;
  const origin = request.headers.origin;
  if (origin !== undefined && !sameOrigin(request)) return false;
  return isLoopbackRequest(request);
}

/** Answers `GET /__runic/sources` with the map, or 404 without one. */
export function answerSources(request: IncomingMessage, response: ServerResponse, read: () => RunicSourceMap | undefined): void {
  request.resume();
  if (request.method !== "GET" && request.method !== "HEAD") return answer(response, 405, undefined, { Allow: "GET, HEAD" });
  if (!mayRead(request)) return answer(response, 403);
  const map = read();
  if (map === undefined) return answer(response, 404);
  answer(response, 200, map);
}

/**
 * The location `{ "id" }` or `{ "file", "line", "column" }` names, when the map lists it; otherwise undefined.
 * A `file` must be exactly the path of a location in the map.
 */
export function findOpenLocation(map: RunicSourceMap, message: unknown): RunicSourceLocation | undefined {
  if (typeof message !== "object" || message === null || Array.isArray(message)) return undefined;
  const { id, file, line, column } = message as Record<string, unknown>;
  const entries: readonly (RunicSourceMember | RunicSourceViewModel)[] = [...map.viewModels, ...map.members];
  if (typeof id === "string") return entries.find((entry) => entry.id === id)?.location;
  if (typeof file !== "string" || typeof line !== "number" || typeof column !== "number") return undefined;
  return entries.find((entry) => entry.location !== undefined && entry.location.file === file
    && entry.location.line === line && entry.location.column === column)?.location;
}

/**
 * Answers `POST /__runic/open`: `{ "id" }` of a map entry, or a `{ "file", "line", "column" }` the map lists, as
 * JSON from a page of this server on this machine. Anything else is refused and opens nothing. An accepted location
 * goes to Vite's own editor launcher (launch-editor: `LAUNCH_EDITOR`, then the running or configured editor, such as
 * VS Code or Rider) through `/__open-in-editor`, which answers 200.
 */
export async function answerOpen(
  request: IncomingMessage,
  response: ServerResponse,
  read: () => RunicSourceMap | undefined,
  launch: (location: RunicSourceLocation) => void,
): Promise<void> {
  if (request.method !== "POST") {
    request.resume();
    return answer(response, 405, undefined, { Allow: "POST" });
  }
  if (!sameOrigin(request) || !isLoopbackRequest(request)) {
    request.resume();
    return answer(response, 403);
  }
  const type = request.headers["content-type"];
  if (typeof type !== "string" || !/^application\/json\s*(?:;|$)/iu.test(type)) {
    request.resume();
    return answer(response, 415);
  }
  const body = await readLimitedBody(request, maximumOpenRequestBytes);
  if (body === undefined) return answer(response, 413);
  let message: unknown;
  try { message = JSON.parse(body); }
  catch { return answer(response, 400); }
  const map = read();
  const location = map === undefined ? undefined : findOpenLocation(map, message);
  if (location === undefined || !isFile(location.file)) return answer(response, 404);
  launch(location);
}

/** The `/__open-in-editor` request of Vite's editor launcher for a location, below the server's base. */
export function editorRequestUrl(base: string, location: RunicSourceLocation): string {
  let path = /^https?:/iu.test(base) ? new URL(base).pathname : base;
  if (!path.endsWith("/")) path += "/";
  return `${path}__open-in-editor?file=${encodeURIComponent(`${location.file}:${location.line}:${location.column}`)}`;
}
