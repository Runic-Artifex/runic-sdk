// client-errors/1 (specs/application/README.md, "Page errors"): an opt-in reporter that sends the page's uncaught
// errors, unhandled rejections and the errors the Views runtime catches to the .NET host, which logs them. Reports
// are bounded, rate-limited and redacted before they leave the page.
import { BridgeError } from "./errors.js";
import { clientErrorsFeature } from "./protocol.js";
import { currentRuntime } from "./runtime.js";
import { clientErrorObserverKey as observerKey, hostCallbacks, type RuntimeErrorObserver } from "./transport.js";


/** Where a reported error came from. */
export type ClientErrorSource = "error" | "unhandled-rejection" | "runtime";

/** One error as it is sent to the host, after trimming and redaction. */
export interface ClientErrorReport {
  /** `error` for an uncaught exception, `unhandled-rejection` for a rejected promise nothing handled, `runtime` for an error the Views runtime caught, such as one a subscriber threw. */
  readonly source: ClientErrorSource;
  /** The error's message, at most `maxMessageLength` characters. */
  readonly message: string;
  /** The error's type, such as `TypeError`, when it has one. */
  readonly name?: string;
  /** The stack, at most `maxStackLines` lines and `maxStackLength` characters, when it has one. */
  readonly stack?: string;
  /** The page's URL without its query and fragment. */
  readonly url?: string;
  /** The route of a View whose push or subscriber failed, for a `runtime` error. */
  readonly route?: string;
}

/** Options of {@link installErrorReporting}. */
export interface ErrorReportingOptions {
  /** The most reports sent per minute; later ones are counted and the count is sent with the next report. Default 10. */
  readonly maxReportsPerMinute?: number;
  /** The most characters of a message. Default 1024. */
  readonly maxMessageLength?: number;
  /** The most characters of a stack. Default 4096. */
  readonly maxStackLength?: number;
  /** The most lines of a stack. Default 30. */
  readonly maxStackLines?: number;
  /** Whether errors the Views runtime catches are reported. Default `true`. */
  readonly runtimeErrors?: boolean;
  /**
   * Called with each report after the built-in trimming and redaction, to remove more, such as user data in
   * messages. Return the report to send, or `undefined` to drop it.
   */
  readonly redact?: (report: ClientErrorReport) => ClientErrorReport | undefined;
}


const minute = 60_000;
const maximumQueued = 10;
const retryInterval = 500;
// A URL's query and fragment can carry tokens; reports keep the scheme, host and path, and a stack frame's line and
// column after them.
const urlWithQuery = /\b((?:https?|file|wss?|app|runic|tauri):\/\/[^\s?#"'<>()]*)[?#][^\s"'<>()]*?((?::\d+){1,2})?(?=[\s"'<>()]|$)/gi;

/** Removes the query and fragment of every URL in `text`. */
export function redactUrls(text: string): string {
  return text.replace(urlWithQuery, "$1$2");
}

/**
 * Reports the page's uncaught errors, unhandled promise rejections and the errors the Views runtime catches to the
 * .NET host, which logs them through `ILogger` in the category `Runic.Application.Views.Page` (event 1150).
 *
 * Reports are bounded and redacted before they leave the page: the message and stack are trimmed, URLs lose their
 * query and fragment, and `options.redact` can remove more. At most `maxReportsPerMinute` reports go out per minute,
 * and repeats of one error within the minute only count. Reports wait for the connection and go out once it uses
 * `client-errors/1`; a host of an earlier release does not, and they are dropped. Errors still reach the console
 * and `onBridgeDiagnostic` as before.
 *
 * @returns A function that removes the reporter. Installing again replaces the previous reporter.
 */
export function installErrorReporting(options: ErrorReportingOptions = {}): () => void {
  const host = hostCallbacks();
  const target = globalThis as unknown as Partial<Pick<EventTarget, "addEventListener" | "removeEventListener">>;
  const maxPerMinute = positive(options.maxReportsPerMinute, 10);
  const maxMessage = positive(options.maxMessageLength, 1024);
  const maxStack = positive(options.maxStackLength, 4096);
  const maxLines = positive(options.maxStackLines, 30);
  let windowStart = Date.now();
  let sent = 0;
  let dropped = 0;
  let seen = new Set<string>();
  const queue: ClientErrorReport[] = [];
  let timer: ReturnType<typeof setTimeout> | undefined;
  let flushing = false;
  let removed = false;
  // Errors the runtime caught and handed to reportError, which a browser raises again as an `error` event.
  const reported = new WeakSet<object>();

  function report(source: ClientErrorSource, error: unknown, route?: string, fallbackMessage?: string): void {
    if (removed) return;
    if (error instanceof BridgeError && error.kind === "released") return;
    const now = Date.now();
    if (now - windowStart >= minute) { windowStart = now; sent = 0; seen = new Set(); }
    let built: ClientErrorReport | undefined = build(source, error, route, fallbackMessage);
    if (options.redact) {
      try { built = options.redact(built); } catch { built = undefined; }
      if (built === undefined) return;
    }
    const identity = `${built.source}\u0000${built.name ?? ""}\u0000${built.message}\u0000${built.stack?.split("\n", 2)[1] ?? ""}`;
    if (seen.has(identity) || sent >= maxPerMinute) { dropped++; return; }
    seen.add(identity);
    sent++;
    if (queue.length >= maximumQueued) { queue.shift(); dropped++; }
    queue.push(built);
    void flush();
  }

  function build(source: ClientErrorSource, error: unknown, route?: string, fallbackMessage?: string): ClientErrorReport {
    const isError = typeof error === "object" && error !== null && typeof (error as Error).message === "string";
    const message = trim(redactUrls(isError ? (error as Error).message : error === undefined ? fallbackMessage ?? "" : describe(error)), maxMessage);
    const name = isError && typeof (error as Error).name === "string" ? trim((error as Error).name, 128) : undefined;
    const rawStack = isError && typeof (error as Error).stack === "string" ? (error as Error).stack! : undefined;
    const stack = rawStack === undefined ? undefined : trim(redactUrls(rawStack.split("\n").slice(0, maxLines).join("\n")), maxStack);
    const url = pageUrl();
    return {
      source, message,
      ...(name ? { name } : {}),
      ...(stack ? { stack } : {}),
      ...(url ? { url } : {}),
      ...(route === undefined ? {} : { route }),
    };
  }

  async function flush(): Promise<void> {
    if (flushing || removed) return;
    flushing = true;
    try {
      while (queue.length > 0 && !removed) {
        const runtime = currentRuntime();
        if (!runtime?.connected) { retry(); return; }
        // The features are known once the connection's hello succeeded. A runtime of an earlier release, without
        // ready, does not list the feature either.
        try { await runtime.ready?.(); } catch { retry(); return; }
        if (runtime.features?.has(clientErrorsFeature) !== true) {
          // A host without the feature: nothing is sent, and nothing waits.
          queue.length = 0;
          dropped = 0;
          return;
        }
        const next = queue.shift()!;
        const count = dropped;
        dropped = 0;
        try { await runtime.request({ kind: "client-error", ...next, ...(count > 0 ? { dropped: count } : {}) }); }
        catch { dropped += count; /* The report is lost with the connection; the count goes with the next one. */ }
      }
    } finally { flushing = false; }
  }

  function retry(): void {
    if (timer !== undefined || removed) return;
    timer = setTimeout(() => { timer = undefined; void flush(); }, retryInterval);
  }

  const onError = (event: Event) => {
    const errorEvent = event as ErrorEvent;
    const error = errorEvent.error as unknown;
    if (typeof error === "object" && error !== null && reported.has(error)) return;
    report("error", error, undefined, typeof errorEvent.message === "string" ? errorEvent.message : "Script error.");
  };
  const onRejection = (event: Event) => {
    report("unhandled-rejection", (event as PromiseRejectionEvent).reason, undefined, "A promise was rejected without a reason.");
  };
  const observer: RuntimeErrorObserver = (error, route) => {
    if (typeof error === "object" && error !== null) reported.add(error);
    report("runtime", error, route);
  };

  const previous = host[uninstallKey] as (() => void) | undefined;
  previous?.();
  target.addEventListener?.("error", onError);
  target.addEventListener?.("unhandledrejection", onRejection);
  if (options.runtimeErrors !== false) host[observerKey] = observer;
  const uninstall = () => {
    if (removed) return;
    removed = true;
    if (timer !== undefined) clearTimeout(timer);
    target.removeEventListener?.("error", onError);
    target.removeEventListener?.("unhandledrejection", onRejection);
    if (host[observerKey] === observer) delete host[observerKey];
    if (host[uninstallKey] === uninstall) delete host[uninstallKey];
  };
  host[uninstallKey] = uninstall;
  return uninstall;
}

const uninstallKey = Symbol.for("runic.views.client-error-reporter");

function positive(value: number | undefined, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) && value >= 1 ? Math.floor(value) : fallback;
}

// Cuts text to at most maximum UTF-16 units, the last one "…", without leaving half a surrogate pair before it.
function trim(text: string, maximum: number): string {
  if (text.length <= maximum) return text;
  let end = maximum - 1;
  const last = text.charCodeAt(end - 1);
  if (last >= 0xd800 && last <= 0xdbff) end--;
  return `${text.slice(0, end)}…`;
}

function describe(value: unknown): string {
  if (typeof value === "string") return value;
  try { return JSON.stringify(value) ?? String(value); } catch { return String(value); }
}

function pageUrl(): string | undefined {
  const location = (globalThis as { location?: { origin?: string; pathname?: string; href?: string } }).location;
  if (!location) return undefined;
  if (typeof location.origin === "string" && location.origin !== "null" && typeof location.pathname === "string")
    return location.origin + location.pathname;
  return typeof location.href === "string" ? location.href.replace(/[?#].*$/s, "") : undefined;
}
