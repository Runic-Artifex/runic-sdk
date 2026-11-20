// The development source map (#951): the Bridge generator's `sources.json` in the project's `obj`, which maps every
// Bridge member to its C# symbol and file:line:column. `runic()` serves it at `/__runic/sources` while serving, and
// opens a location from it, a frontend file Vite serves or a Runic Translations message's source (translations-source.ts)
// in the editor at `/__runic/open`. Neither exists in `vite build` or `vite preview`, and
// nothing of the map reaches a bundle.

import type { IncomingMessage, ServerResponse } from "node:http";
import { readdirSync, readFileSync, statSync } from "node:fs";
import path, { dirname, isAbsolute, join, resolve, type PlatformPath } from "node:path";
import launchEditor from "launch-editor";
import { isLoopbackRequest, mayRead, readLimitedBody, sameOrigin } from "./requests.js";
import type { AccessList } from "./access.js";
import { isLoopbackAddress } from "./access.js";
import { isLocalUser, type LocalUserCheck } from "./local-user.js";
import type { RunicClientResponse } from "./channel.js";
import { openPath, sourcesPath } from "./channel.js";
import { parseMessageReference, type MessageReference } from "./translations-source.js";

export { openPath, sourcesPath };
/** The `sources.json` path that `dotnet runic dev` gives the development server. */
export const sourceMapVariable = "RUNIC_APPLICATION_DEV_SOURCE_MAP";
const projectVariable = "RUNIC_APPLICATION_DEV_PROJECT";
export const sourceMapFormat = "runic.views.bridge-sources/1";
const sourceMapFileName = "sources.json";
// A larger file is not a map the generator wrote.
const maximumMapBytes = 16 * 1024 * 1024;
const maximumOpenRequestBytes = 4096;
/** Why `/__runic/open` refuses a client of another machine. */
export const openLoopbackOnly = "Opening in the editor works only from a page on the development machine (localhost): " +
  "/__runic/open launches the editor there. runic({ allow }) and RUNIC_DEV_ALLOW admit other machines to Runic Inspect's data, not to this action.";
/** Why `/__runic/open` refuses a loopback client that another user of this machine runs (local-user.ts). */
export const openOtherUser = "Opening in the editor works only for the user who runs the development server: " +
  "this request came from a program of another account on this machine.";

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

/** `/__runic/sources`: the `sources.json` the Bridge generator wrote, version `runic.views.bridge-sources/1`. */
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

/** The map with its well-formed entries, or undefined when it is not a `runic.views.bridge-sources/1` map. */
export function parseSourceMap(value: unknown): RunicSourceMap | undefined {
  const format = typeof value === "object" && value !== null ? (value as { format?: unknown }).format : undefined;
  if (format !== sourceMapFormat) return undefined;
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

/** Answers `GET /__runic/sources` with the map, or 404 without one. */
export async function answerSources(
  request: IncomingMessage, response: ServerResponse, read: () => RunicSourceMap | undefined, access?: AccessList,
): Promise<void> {
  request.resume();
  if (request.method !== "GET" && request.method !== "HEAD") return answer(response, 405, undefined, { Allow: "GET, HEAD" });
  if (!(await mayRead(request, access))) return answer(response, 403);
  const map = read();
  if (map === undefined) return answer(response, 404);
  answer(response, 200, map);
}

/**
 * The location `{ "id" }` of a map entry names, or `{ "file", "line", "column"? }` in a file the map lists, such as a
 * frame of a .NET stack in a ViewModel's file; otherwise undefined. A `file` must be exactly the path of a location in
 * the map, and `line` and `column` positive integers (`column` defaults to 1).
 */
export function findOpenLocation(map: RunicSourceMap, message: unknown): RunicSourceLocation | undefined {
  if (typeof message !== "object" || message === null || Array.isArray(message)) return undefined;
  const { id, file, line, column = 1 } = message as Record<string, unknown>;
  const entries: readonly (RunicSourceMember | RunicSourceViewModel)[] = [...map.viewModels, ...map.members];
  if (typeof id === "string") return entries.find((entry) => entry.id === id)?.location;
  if (typeof file !== "string" || !Number.isSafeInteger(line) || (line as number) < 1
      || !Number.isSafeInteger(column) || (column as number) < 1) return undefined;
  const listed = entries.find((entry) => entry.location?.file === file)?.location;
  return listed === undefined ? undefined : { file: listed.file, line: line as number, column: column as number };
}

/** Whether `file` is exactly the path of a location in the map, so that `/__runic/open` opens it. */
export function listsFile(map: RunicSourceMap | undefined, file: string): boolean {
  return map !== undefined && [...map.viewModels, ...map.members].some((entry) => entry.location?.file === file);
}

/**
 * What decides, besides the source map, which locations may be opened (`resolveOpenLocation`).
 */
export interface RunicOpenResolution {
  /** Whether Vite serves a frontend file to the page (index.ts: `isFileLoadingAllowed`). Without it, no frontend file. */
  readonly frontendFile?: (file: string) => boolean;
  /** Where a Runic Translations message is (translations-source.ts). Without it, a message is refused. */
  readonly findMessage?: (message: MessageReference) => Promise<RunicSourceLocation | undefined>;
}

/**
 * What `/__runic/open`, its HMR route and the dock's open action open for a request's `{ id }`, `{ file, line, column? }`
 * or `{ message: { catalog, key, locale } }`, else undefined (`invalid` for a malformed message reference): a location
 * of the current map (`findOpenLocation`), a location in a frontend file that `frontendFile` accepts (the files Vite
 * itself would serve to the page, which Runic Inspect links to), or where `findMessage` finds a Translations message;
 * in every case only while the file exists (D066.36). The one decision of what may be opened; the endpoint checks the
 * request's origin first, and the dock's RPC is limited to clients Vite DevTools trusts.
 */
export async function resolveOpenLocation(
  read: () => RunicSourceMap | undefined,
  message: unknown,
  resolution: RunicOpenResolution = {},
): Promise<RunicSourceLocation | "invalid" | undefined> {
  let location: RunicSourceLocation | undefined;
  const translation = typeof message === "object" && message !== null && "message" in message
    ? (message as { message: unknown }).message : undefined;
  if (translation !== undefined) {
    const reference = parseMessageReference(translation);
    if (reference === undefined) return "invalid";
    location = await resolution.findMessage?.(reference);
  } else {
    const map = read();
    location = (map === undefined ? undefined : findOpenLocation(map, message))
      ?? frontendLocation(message, resolution.frontendFile ?? (() => false));
  }
  return location !== undefined && isFile(location.file) ? location : undefined;
}

/**
 * `{ file, line, column? }` of a frontend file: an absolute path in canonical form (`canonicalFrontendFile`) that
 * `accepts` admits in Vite's form, opened in the platform's form. `paths` is `node:path`, `path.win32` in tests.
 */
export function frontendLocation(
  message: unknown, accepts: (file: string) => boolean, paths: PlatformPath = path,
): RunicSourceLocation | undefined {
  if (typeof message !== "object" || message === null || Array.isArray(message)) return undefined;
  const { id, file, line, column = 1 } = message as Record<string, unknown>;
  if (id !== undefined || typeof file !== "string" || !Number.isSafeInteger(line) || (line as number) < 1
      || !Number.isSafeInteger(column) || (column as number) < 1) {
    return undefined;
  }
  const canonical = canonicalFrontendFile(file, paths);
  return canonical !== undefined && accepts(canonical.vite)
    ? { file: canonical.file, line: line as number, column: column as number } : undefined;
}

/**
 * A frontend file as Runic Inspect names it, in the two forms `/__runic/open` needs: `vite`, the form Vite names files
 * in and `isFileLoadingAllowed` compares (`/` separators; on Windows `C:/p/src/App.vue`, which Vite compares without
 * regard to case there), and `file`, the platform's form to test and open (`C:\p\src\App.vue`). Either separator is
 * accepted on Windows. Undefined unless the path is absolute and has nothing that could lead outside what Vite's check
 * judges: no `.` or `..` segment, no empty segment, no NUL; on Windows only a drive path (`C:\` or `C:/`), so no UNC
 * path (`\\server\share`, which Windows would reach over the network), no device path (`\\?\`, `\\.\`), no rooted
 * path without a drive (`\p`, `/C:/p`), no other `:` (alternate data streams) and no segment that Windows trims
 * (a trailing `.` or space).
 */
export function canonicalFrontendFile(raw: string, paths: PlatformPath = path): { file: string; vite: string } | undefined {
  if (raw.length === 0 || raw.length > maximumOpenRequestBytes || raw.includes("\0")) return undefined;
  const windows = paths.sep === "\\";
  const vite = windows ? raw.replaceAll("\\", "/") : raw;
  if (windows ? !/^[a-z]:\//iu.test(vite) || vite.includes(":", 2) : !vite.startsWith("/")) return undefined;
  const segments = vite.slice(windows ? 3 : 1).split("/");
  if (segments.some((segment) => segment === "" || segment === "." || segment === ".."
      || (windows && /[. ]$/u.test(segment)))) {
    return undefined;
  }
  return { file: windows ? paths.normalize(vite) : vite, vite };
}

/**
 * Opens a location in the editor with launch-editor, the package behind Vite's `/__open-in-editor`, so with the same
 * editor resolution: `LAUNCH_EDITOR`, else a running editor such as VS Code or a JetBrains IDE, else `VISUAL`/`EDITOR`.
 * `failed` receives launch-editor's message when no editor could be started.
 */
export function launchSourceEditor(location: RunicSourceLocation, failed: (message: string) => void): void {
  launchEditor(`${location.file}:${location.line}:${location.column}`, (_file, message) => {
    failed(message ?? `No editor could open ${location.file}.`);
  });
}

/**
 * Answers a development client's request over Vite's HMR connection (channel.ts, `requestEvent`) as the HTTP endpoints
 * answer, under the same access rules, judged per request from the connection's `peer` address: `sources`, `handlers`
 * and `message` for this machine or one the access list admits (`runic({ allow })`, `RUNIC_DEV_ALLOW`), `open` for
 * this machine's user only (a loopback peer that `localUser` confirms), as `/__runic/open` (403 otherwise):
 * `sources` with the map (200) or 404; `handlers` with Runic Inspect's handler analysis of a `file` (200), 404 or 400;
 * `message` with the Translations message API's reply for a `target` `{ catalog, key }` (200), 404 or 400;
 * `open` with the same decision as `/__runic/open` (`resolveOpenLocation`), 200 after `launch`, else 404 (400 for a
 * malformed message reference) and nothing launched. A malformed request without an id gets no answer, one with an id
 * but another kind gets 400.
 */
export async function answerClientRequest(message: unknown, context: {
  readonly read: () => RunicSourceMap | undefined;
  readonly launch: (location: RunicSourceLocation) => void;
  readonly resolution?: RunicOpenResolution;
  readonly analyze?: (file: string) => Promise<unknown>;
  readonly message?: (message: MessageReference) => Promise<unknown>;
  /** The HMR connection's client address, and who besides this machine may read (loopback only without it). */
  readonly peer: string | undefined;
  readonly access?: AccessList;
  /** Whether the connection's client is a program of this server's user (local-user.ts); refused without it. */
  readonly localUser?: () => Promise<boolean>;
}): Promise<RunicClientResponse | undefined> {
  if (typeof message !== "object" || message === null || Array.isArray(message)) return undefined;
  const { id, kind, target, file } = message as Record<string, unknown>;
  if (typeof id !== "number" || !Number.isSafeInteger(id) || id < 0) return undefined;
  if (kind !== "sources" && kind !== "handlers" && kind !== "message" && kind !== "open") return { id, status: 400 };
  const admitted = kind === "open" ? isLoopbackAddress(context.peer) && context.localUser !== undefined && await context.localUser()
    : context.access === undefined ? isLoopbackAddress(context.peer) : await context.access.allows(context.peer);
  if (!admitted) return { id, status: 403 };
  if (kind === "sources") {
    const map = context.read();
    return map === undefined ? { id, status: 404 } : { id, status: 200, body: map };
  }
  if (kind === "handlers") {
    if (context.analyze === undefined) return { id, status: 404 };
    if (typeof file !== "string" || file.length === 0 || file.length > 4096) return { id, status: 400 };
    const analysis = await context.analyze(file);
    return analysis === undefined ? { id, status: 404 } : { id, status: 200, body: analysis };
  }
  if (kind === "message") {
    if (context.message === undefined) return { id, status: 404 };
    const reference = parseMessageReference(typeof target === "object" && target !== null ? { locale: "", ...target } : target);
    if (reference === undefined) return { id, status: 400 };
    const reply = await context.message(reference);
    return reply === undefined ? { id, status: 404 } : { id, status: 200, body: reply };
  }
  const location = await resolveOpenLocation(context.read, target, context.resolution);
  if (location === "invalid") return { id, status: 400 };
  if (location === undefined) return { id, status: 404 };
  context.launch(location);
  return { id, status: 200 };
}

/**
 * Answers `POST /__runic/open`: `{ "id" }` of a map entry, `{ "file", "line", "column" }` in a file the map lists or a
 * frontend file Vite serves, or `{ "message": { catalog, key, locale } }` of a Runic Translations message
 * (`resolveOpenLocation`), as JSON from a page of this server on this machine, from a program of this server's user
 * (`localUser`, local-user.ts). Anything else is refused and opens nothing. An accepted location goes to `launch` (launch-editor, as the dock's open action) and is answered 200.
 */
export async function answerOpen(
  request: IncomingMessage,
  response: ServerResponse,
  read: () => RunicSourceMap | undefined,
  launch: (location: RunicSourceLocation) => void,
  resolution: RunicOpenResolution = {},
  localUser: LocalUserCheck = isLocalUser,
): Promise<void> {
  if (request.method !== "POST") {
    request.resume();
    return answer(response, 405, undefined, { Allow: "POST" });
  }
  if (!sameOrigin(request)) {
    request.resume();
    return answer(response, 403);
  }
  // Loopback only, whatever the allow list says: the editor opens on this machine, not on the client's.
  if (!isLoopbackRequest(request)) {
    request.resume();
    return answer(response, 403, { error: "loopback-only", detail: openLoopbackOnly });
  }
  // Another account of this machine is loopback too, and a program sends any Origin it likes.
  if (!(await localUser(request.socket))) {
    request.resume();
    return answer(response, 403, { error: "other-user", detail: openOtherUser });
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
  const location = await resolveOpenLocation(read, message, resolution);
  if (location === "invalid") return answer(response, 400);
  if (location === undefined) return answer(response, 404);
  launch(location);
  answer(response, 200);
}
