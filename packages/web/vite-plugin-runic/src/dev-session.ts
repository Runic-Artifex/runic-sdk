import type { IncomingMessage, ServerResponse } from "node:http";
import type { ProxyOptions } from "vite";
import type { RunicDevelopmentState, RunicTimelineEntry } from "./timeline.js";
import { connectionEvent, diagnosticEvent, diagnosticsPath, maximumClientMessageBytes, viewEvent } from "./channel.js";
import { createDevelopmentTimeline, initialDevelopmentState, timelineLimit, withoutFailures } from "./timeline.js";

// `dotnet runic dev` gives the development server process a random session value and accepts the server as ready
// only when /__runic/state answers with it, so another process that takes the port first is not mistaken for this
// server.
export const devSessionVariable = "RUNIC_APPLICATION_DEV_SESSION";
export const devSessionHeader = "X-Runic-Dev-Session";
export const statePath = "/__runic/state";

/** The session value `dotnet runic dev` set, when it has the shape the tool writes: 32 to 128 hex digits. */
export function devSessionValue(value: string | undefined): string | undefined {
  return value !== undefined && /^[0-9a-f]{32,128}$/.test(value) ? value : undefined;
}

/** Answers `/__runic/state` with the path-free state, and with the `dotnet runic dev` session when there is one. */
export function answerState(response: ServerResponse, state: RunicDevelopmentState, session: string | undefined): void {
  response.statusCode = 200;
  response.setHeader("Content-Type", "application/json; charset=utf-8");
  response.setHeader("Cache-Control", "no-store");
  if (session !== undefined) response.setHeader(devSessionHeader, session);
  response.end(JSON.stringify(withoutFailures(state)));
}

function answerStatus(response: ServerResponse, status: number, headers: Record<string, string> = {}): void {
  if (response.writableEnded) return;
  response.statusCode = status;
  response.setHeader("Cache-Control", "no-store");
  for (const [name, value] of Object.entries(headers)) response.setHeader(name, value);
  response.end();
}

/**
 * Whether a request comes from a page of this server: a browser sends `Origin` with every POST, and a cross-site
 * page that posts JSON needs a CORS preflight, which the endpoint never grants.
 */
function sameOrigin(request: IncomingMessage): boolean {
  const site = request.headers["sec-fetch-site"];
  if (site !== undefined && site !== "same-origin") return false;
  const origin = request.headers.origin;
  const host = request.headers.host;
  if (typeof origin !== "string" || typeof host !== "string") return false;
  try {
    const url = new URL(origin);
    return (url.protocol === "http:" || url.protocol === "https:") && url.host === host.toLowerCase();
  } catch {
    return false;
  }
}

/**
 * Answers a development client's POST to `/__runic/diagnostics`: `{ "event", "data" }` from the same origin, as JSON
 * of at most `maximumClientMessageBytes`. Calls `deliver` with an accepted message and answers 204.
 */
export async function answerClientMessage(
  request: IncomingMessage,
  response: ServerResponse,
  deliver: (event: string, data: unknown) => void,
): Promise<void> {
  if (request.method !== "POST") {
    request.resume();
    return answerStatus(response, 405, { Allow: "POST" });
  }
  if (!sameOrigin(request)) {
    request.resume();
    return answerStatus(response, 403);
  }
  const type = request.headers["content-type"];
  if (typeof type !== "string" || !/^application\/json\s*(?:;|$)/iu.test(type)) {
    request.resume();
    return answerStatus(response, 415);
  }
  const declared = Number(request.headers["content-length"]);
  if (Number.isFinite(declared) && declared > maximumClientMessageBytes) {
    request.resume();
    return answerStatus(response, 413);
  }
  const body = await readBody(request);
  if (body === undefined) return answerStatus(response, 413);
  let message: unknown;
  try { message = JSON.parse(body); }
  catch { return answerStatus(response, 400); }
  if (typeof message !== "object" || message === null || Array.isArray(message)) return answerStatus(response, 400);
  const { event, data } = message as { event?: unknown; data?: unknown };
  if (event !== diagnosticEvent && event !== connectionEvent && event !== viewEvent) return answerStatus(response, 400);
  deliver(event, data);
  answerStatus(response, 204);
}

// The body as UTF-8, or undefined once it exceeds the limit; the rest is drained so that the answer reaches the client.
function readBody(request: IncomingMessage): Promise<string | undefined> {
  return new Promise((resolve) => {
    const chunks: Buffer[] = [];
    let size = 0;
    let exceeded = false;
    request.on("data", (chunk: Buffer) => {
      if (exceeded) return;
      size += chunk.byteLength;
      if (size > maximumClientMessageBytes) {
        exceeded = true;
        chunks.length = 0;
        return;
      }
      chunks.push(chunk);
    });
    request.on("end", () => resolve(exceeded ? undefined : Buffer.concat(chunks).toString("utf8")));
    request.on("error", () => resolve(undefined));
  });
}

export interface RunicDevSessionProxyOptions {
  /** How many timeline entries `/__runic/state` keeps: 200 by default, rounded down and limited to 1 to 500. */
  readonly maxTimelineEntries?: number;
  /**
   * `false` stops printing the development client's failures (an `error` entry with its exception type, message and
   * stack) to the development server's terminal. Defaults to `true`.
   */
  readonly printFailures?: boolean;
}

/**
 * Vite proxy entries for a development server that takes a Vite proxy configuration but no Vite plugins: the
 * Angular CLI's `proxyConfig`. Spread the result into the proxy configuration. The entries never proxy; they answer:
 *
 * - `/__runic/state` with the path-free development state, and under `dotnet runic dev` with its session;
 * - `/__runic/diagnostics`, which the development client (`@runic-artifex/vite-plugin-runic/client`) posts the
 *   page's Views diagnostics, connection state and View states to. The timeline is bounded, and failures go to the
 *   terminal.
 */
export function runicDevSessionProxy(options: RunicDevSessionProxyOptions = {}): Record<string, ProxyOptions> {
  if (options.printFailures !== undefined && typeof options.printFailures !== "boolean") {
    throw new TypeError("printFailures must be true or false.");
  }
  const session = devSessionValue(process.env[devSessionVariable]);
  const printFailures = options.printFailures !== false;
  const timeline = createDevelopmentTimeline(
    initialDevelopmentState(undefined, { command: "serve", mode: "development", root: process.cwd() }),
    timelineLimit(options.maxTimelineEntries),
    () => undefined,
  );
  const deliver = (event: string, data: unknown): void => {
    if (event === connectionEvent) {
      timeline.applyConnection(data);
      return;
    }
    if (event === viewEvent) {
      timeline.applyView(data);
      return;
    }
    const entry = timeline.appendDiagnostic(data);
    if (entry?.failure && printFailures) console.warn(formatFailure(entry));
  };
  return {
    "^/__runic/state(?:[/?#]|$)": {
      // The entries answer in bypass and return the URL; Vite then sees the ended response and stops.
      bypass(request, response) {
        if (response === undefined) return false;
        request.resume();
        answerState(response, timeline.state, session);
        return request.url ?? statePath;
      },
    },
    [`^${diagnosticsPath}(?:[/?#]|$)`]: {
      async bypass(request, response) {
        if (response === undefined) return false;
        await answerClientMessage(request, response, deliver);
        return request.url ?? diagnosticsPath;
      },
    },
  };
}

/** A failure for the terminal: the entry's source and label, then the exception type, message and stack. */
export function formatFailure(entry: RunicTimelineEntry): string {
  const failure = entry.failure;
  const lines = [`[runic] ${entry.source ?? "runic"} ${entry.label}`];
  if (failure) {
    const heading = `${failure.type}: ${failure.message}`;
    lines.push(`  ${heading}`);
    // A .NET stack often starts with the same line.
    const stack = failure.stack?.split("\n") ?? [];
    if (stack[0]?.trim() === heading) stack.shift();
    lines.push(...stack.map((line) => `    ${line.trim()}`));
  }
  return lines.join("\n");
}
