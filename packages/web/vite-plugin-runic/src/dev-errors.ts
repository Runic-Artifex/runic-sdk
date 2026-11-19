// Frontend errors as IDE error lines under `dotnet runic dev` (#1160). Vite shows a module that fails to compile in its
// page overlay and as coloured text in the terminal, where the location is in a boxed header; no IDE can read that. While
// `dotnet runic dev` serves (RUNIC_APPLICATION_DEV_PROJECT), each error Vite sends to the page is also printed as one line
// in MSBuild's canonical form, which VS Code's $msCompile, the runic-app template's tasks and Visual Studio's Error List
// read like a build error:
//
//   /path/to/App/Frontend/src/App.tsx(2,23): error RUNICP008: [vite:oxc] [PARSE_ERROR] Expected corresponding JSX closing tag for 'p'.
//
// and, once the modules that failed compile again, one line that clears them:
//
//   [runic] Frontend errors cleared
//
// `dotnet runic dev` prints both after its "[vite] " label (tools/dotnet-runic/README.md, IDE contract). Nothing is parsed
// out of the terminal text: the lines come from the error objects Vite sends to the page (err.id, err.loc, err.plugin).

import path from "node:path";

/** The variable `dotnet runic dev` sets for the development server: the project file. */
export const devProjectVariable = "RUNIC_APPLICATION_DEV_PROJECT";
/** The diagnostic ID of a frontend module that does not compile (docs/diagnostics.md). */
export const frontendErrorCode = "RUNICP008";
/** The line that clears the frontend errors printed before it. */
export const frontendErrorsClearedLine = "[runic] Frontend errors cleared";

/** The error object of Vite's `{ type: "error", err }` payload (prepareError). */
export interface ViteErrorPayload {
  readonly message?: string;
  readonly id?: string;
  readonly plugin?: string;
  readonly pluginCode?: string;
  readonly loc?: { readonly file?: string; readonly line?: number; readonly column?: number };
}

// One line of a message: the IDE shows the message, not Vite's code frame or stack.
const maximumMessageLength = 400;
// Oxc's and Rolldown's diagnostics in a message (Vite's "Transform failed with N errors"): "[CODE] message", then the
// location header "╭─[ path:line:column ]", 1-based.
const diagnosticPattern = /^\s*\[([A-Z][A-Z0-9_]*)\]\s+(.+?)\s*\n\s*╭─\[\s*(.+?):(\d+):(\d+)\s*\]/gmu;

/** The file a Vite error is about, an absolute path without a query; undefined for a virtual module or none. */
export function errorFile(err: ViteErrorPayload): string | undefined {
  for (const candidate of [err.id, err.loc?.file]) {
    if (typeof candidate !== "string" || candidate.length === 0 || candidate.startsWith("\0")) continue;
    const file = candidate.replace(/[?#].*$/su, "");
    if (path.isAbsolute(file) || path.win32.isAbsolute(file)) return path.normalize(file);
  }
  return undefined;
}

/**
 * The MSBuild-form lines for a Vite error, one per diagnostic in it. Positions are 1-based, as in MSBuild. Oxc's
 * diagnostics carry theirs in the message; otherwise `loc` (line 1-based, column 0-based, as Rollup's) when it lies in
 * the module's code. It does not always: under Bun, Vite takes an error's own `line` and `column`, a place in Vite's
 * code, for the module's. Without a file the origin is the project, as for `dotnet runic dev`'s own failures.
 */
export function frontendErrorLines(err: ViteErrorPayload, project: string): string[] {
  const file = errorFile(err);
  const prefix = err.plugin ? `[${err.plugin}] ` : "";
  const message = err.message ?? "";
  const diagnostics = file === undefined ? [] : [...message.matchAll(diagnosticPattern)];
  if (diagnostics.length > 0) {
    return diagnostics.map(([, code, text, , line, column]) =>
      errorLine(`${file}(${line},${column})`, `${prefix}[${code}] ${text}`));
  }
  const firstLine = message.split(/\r?\n/u).map((text) => text.trim()).find((text) => text.length > 0) ?? "The module does not compile.";
  if (file === undefined) return [errorLine(`${project} `, prefix + firstLine)];
  // Vite's message for a plugin's error can start with the file and position again; the line has them already.
  const withoutLocation = firstLine.replace(new RegExp(`^${escapeRegExp(file)}(?::\\d+){0,2}:?\\s*`, "u"), "") || firstLine;
  const position = plausiblePosition(err);
  return [errorLine(position === undefined ? file : `${file}(${position})`, prefix + withoutLocation)];
}

function plausiblePosition(err: ViteErrorPayload): string | undefined {
  const line = err.loc?.line, column = err.loc?.column;
  if (!Number.isInteger(line) || line! < 1) return undefined;
  if (typeof err.pluginCode === "string") {
    const lines = err.pluginCode.split(/\r?\n/u);
    if (line! > lines.length || Number.isInteger(column) && column! > lines[line! - 1]!.length) return undefined;
  }
  return Number.isInteger(column) && column! >= 0 ? `${line},${column! + 1}` : `${line}`;
}

function errorLine(origin: string, message: string): string {
  const text = message.length > maximumMessageLength ? `${message.slice(0, maximumMessageLength - 1)}…` : message;
  return `${origin}: error ${frontendErrorCode}: ${text}`;
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

interface HotChannel {
  send(...arguments_: unknown[]): unknown;
}

interface DevServer {
  readonly environments?: Record<string, {
    readonly hot?: HotChannel;
    readonly transformRequest?: (url: string) => Promise<unknown>;
    readonly moduleGraph?: { getModuleById(id: string): { readonly url?: string } | undefined };
  }>;
}

/**
 * Prints the client environment's errors as error lines while `dotnet runic dev` serves; nothing otherwise. The errors
 * clear once Vite sends an update or a full reload and each module that failed transforms again, so an update that
 * fails once more clears nothing. `write` is the output, standard output by default.
 */
export function printDevErrors(server: DevServer, write: (line: string) => void = (line) => process.stdout.write(`${line}\n`),
  project = process.env[devProjectVariable]): void {
  if (!project) return;
  const client = server.environments?.client;
  const hot = client?.hot;
  if (!hot || typeof hot.send !== "function") return;
  const printed = new Set<string>();
  const failed = new Set<string>();
  let checking: Promise<void> | undefined;
  const clearIfCompiles = async (): Promise<void> => {
    for (const id of [...failed]) {
      const url = client.moduleGraph?.getModuleById(id)?.url ?? id;
      try {
        await client.transformRequest?.(url);
      } catch {
        return;
      }
    }
    failed.clear();
    printed.clear();
    write(frontendErrorsClearedLine);
  };
  const send = hot.send.bind(hot);
  hot.send = (...arguments_: unknown[]) => {
    const payload = arguments_[0];
    if (payload && typeof payload === "object") {
      const { type, err } = payload as { type?: string; err?: ViteErrorPayload };
      if (type === "error" && err) {
        // Each request of a module that fails sends its error again; one line per error until they clear.
        for (const line of frontendErrorLines(err, project)) {
          if (printed.has(line)) continue;
          printed.add(line);
          write(line);
        }
        if (typeof err.id === "string") failed.add(err.id.replace(/[?#].*$/s, ""));
      } else if ((type === "update" || type === "full-reload") && printed.size > 0 && checking === undefined) {
        checking = clearIfCompiles().finally(() => { checking = undefined; });
      }
    }
    return send(...arguments_);
  };
}
