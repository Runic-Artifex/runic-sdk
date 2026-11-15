// Browser spans of Bridge calls, exported over OTLP by the development server.
//
// While a reader of the `@runic-artifex/views` Bridge-call channel gives each call a W3C trace context, the call's
// request carries it to .NET (trace-context/1), whose `runic.bridge.*` span becomes the child of the page's span. The
// development client sends each settled call that has one to this server as a `runic:span` message; the server builds
// the page's span from it and forwards it as OTLP/JSON (OTLP/HTTP, `application/json`) to the endpoint the standard
// OpenTelemetry variables name. The page needs no OpenTelemetry SDK, sends nothing cross-origin, and the production
// build contains none of it: the client is development only, and this module runs in the development server.
import { readFileSync } from "node:fs";
import { join } from "node:path";

export { spanEvent } from "./channel.js";

/** Where and how the development server exports the page's spans of Bridge calls. */
export interface RunicOtlpOptions {
  /**
   * The OTLP/HTTP traces URL, such as `http://localhost:4318/v1/traces`. Defaults to
   * `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`, or else `OTEL_EXPORTER_OTLP_ENDPOINT` with `/v1/traces` appended.
   */
  readonly url?: string;
  /** Headers of each export, such as an API key. Defaults to `OTEL_EXPORTER_OTLP_TRACES_HEADERS` or `OTEL_EXPORTER_OTLP_HEADERS`. */
  readonly headers?: Readonly<Record<string, string>>;
  /**
   * The `service.name` of the page's spans. Defaults to `OTEL_SERVICE_NAME` with `-page` appended, or else the Vite root's
   * package name with `-page` appended, so the page and the .NET host are separate services of one trace.
   */
  readonly serviceName?: string;
}

/** The resolved export: the traces URL, its headers and the page's service name. */
export interface ResolvedOtlp {
  readonly url: string;
  readonly headers: Readonly<Record<string, string>>;
  readonly serviceName: string;
}

type Environment = Readonly<Record<string, string | undefined>>;

/**
 * The export the option and the environment ask for, or `undefined` for none: `false`, `OTEL_SDK_DISABLED=true`,
 * `OTEL_TRACES_EXPORTER=none`, or no endpoint at all.
 */
export function resolveOtlp(option: boolean | RunicOtlpOptions | undefined, environment: Environment, root: string): ResolvedOtlp | undefined {
  if (option === false) return undefined;
  if (option !== undefined && option !== true && (typeof option !== "object" || option === null)) {
    throw new TypeError("otlp must be true, false or { url?, headers?, serviceName? }.");
  }
  const configured = typeof option === "object" ? option : {};
  if (option === undefined || option === true) {
    if (environment["OTEL_SDK_DISABLED"]?.trim().toLowerCase() === "true") return undefined;
    if (environment["OTEL_TRACES_EXPORTER"]?.split(",").map(name => name.trim().toLowerCase()).includes("none")) return undefined;
  }
  const url = configured.url ?? tracesUrl(environment);
  if (url === undefined) {
    if (option === true) throw new TypeError("otlp: true needs otlp.url or OTEL_EXPORTER_OTLP_ENDPOINT.");
    return undefined;
  }
  let parsed: URL;
  try { parsed = new URL(url); }
  catch { throw new TypeError(`otlp.url is not a URL: ${url}`); }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") throw new TypeError(`otlp.url must be http or https: ${url}`);
  const headers = configured.headers ?? parseHeaders(environment["OTEL_EXPORTER_OTLP_TRACES_HEADERS"] ?? environment["OTEL_EXPORTER_OTLP_HEADERS"]);
  for (const [name, value] of Object.entries(headers)) {
    if (typeof value !== "string") throw new TypeError(`otlp.headers.${name} must be a string.`);
  }
  const serviceName = configured.serviceName ?? `${environment["OTEL_SERVICE_NAME"]?.trim() || packageName(root) || "runic"}-page`;
  return { url: parsed.href, headers, serviceName };
}

// The OpenTelemetry exporter rules: the signal's own variable is the URL; the general one is a base for /v1/traces.
function tracesUrl(environment: Environment): string | undefined {
  const traces = environment["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"]?.trim();
  if (traces) return traces;
  const base = environment["OTEL_EXPORTER_OTLP_ENDPOINT"]?.trim();
  if (!base) return undefined;
  return `${base.replace(/\/+$/u, "")}/v1/traces`;
}

// `name=value,name=value`, values percent-encoded (the OpenTelemetry exporter specification).
function parseHeaders(text: string | undefined): Record<string, string> {
  const headers: Record<string, string> = {};
  for (const pair of text?.split(",") ?? []) {
    const separator = pair.indexOf("=");
    if (separator <= 0) continue;
    const name = pair.slice(0, separator).trim();
    let value = pair.slice(separator + 1).trim();
    try { value = decodeURIComponent(value); } catch { /* Kept as written. */ }
    if (name) headers[name] = value;
  }
  return headers;
}

function packageName(root: string): string | undefined {
  try {
    const name = (JSON.parse(readFileSync(join(root, "package.json"), "utf8")) as { name?: unknown }).name;
    return typeof name === "string" && name.length > 0 ? name.replace(/^@[^/]+\//u, "") : undefined;
  } catch {
    return undefined;
  }
}

/** One settled Bridge call with a trace context, as the development client sends it. */
export interface BrowserSpan {
  readonly traceId: string;
  readonly spanId: string;
  readonly name: string;
  /** Unix epoch milliseconds (`performance.timeOrigin + performance.now()`). */
  readonly start: number;
  readonly end: number;
  readonly kind: string;
  readonly request: string;
  readonly route: string;
  readonly contract: string;
  readonly member: string;
  readonly outcome: string;
  readonly reason?: string;
  readonly revision?: number;
  readonly handler?: { readonly name: string; readonly duration: number };
}

const traceparentPattern = /^00-([0-9a-f]{32})-([0-9a-f]{16})-[0-9a-f]{2}$/u;
const maximumText = 256;

const text = (value: unknown): string | undefined =>
  typeof value === "string" && value.length > 0 ? value.slice(0, maximumText) : undefined;
const finite = (value: unknown): number | undefined =>
  typeof value === "number" && Number.isFinite(value) ? value : undefined;

/** The span a `runic:span` message describes, or `undefined` for one that is not well formed. */
export function readBrowserSpan(data: unknown): BrowserSpan | undefined {
  if (typeof data !== "object" || data === null) return undefined;
  const value = data as Record<string, unknown>;
  const ids = typeof value["traceparent"] === "string" ? traceparentPattern.exec(value["traceparent"]) : null;
  if (!ids || /^0+$/u.test(ids[1]!) || /^0+$/u.test(ids[2]!)) return undefined;
  const start = finite(value["start"]);
  const end = finite(value["end"]);
  const name = text(value["name"]);
  if (start === undefined || end === undefined || end < start || name === undefined) return undefined;
  const handlerValue = value["handler"] as Record<string, unknown> | undefined;
  const handlerName = text(handlerValue?.["name"]);
  const handlerDuration = finite(handlerValue?.["duration"]);
  const reason = text(value["reason"]);
  const revision = finite(value["revision"]);
  return {
    traceId: ids[1]!, spanId: ids[2]!, name, start, end,
    kind: text(value["kind"]) ?? "call", request: text(value["request"]) ?? "", route: text(value["route"]) ?? "",
    contract: text(value["contract"]) ?? "", member: text(value["member"]) ?? "", outcome: text(value["outcome"]) ?? "ok",
    ...reason === undefined ? {} : { reason },
    ...revision === undefined ? {} : { revision },
    ...handlerName === undefined || handlerDuration === undefined ? {} : { handler: { name: handlerName, duration: handlerDuration } },
  };
}

type AnyValue = { stringValue: string } | { intValue: string } | { doubleValue: number };
interface KeyValue { readonly key: string; readonly value: AnyValue }

const attribute = (key: string, value: string | number): KeyValue => ({
  key,
  value: typeof value === "string" ? { stringValue: value }
    : Number.isInteger(value) ? { intValue: String(value) } : { doubleValue: value },
});

// Epoch milliseconds as OTLP's fixed64 nanoseconds, which JSON carries as a decimal string.
function nanoseconds(milliseconds: number): string {
  const whole = Math.floor(milliseconds);
  return (BigInt(whole) * 1_000_000n + BigInt(Math.round((milliseconds - whole) * 1_000_000))).toString();
}

/**
 * An OTLP/JSON `ExportTraceServiceRequest` of the page's spans: one resource (the page's service), one scope, and a
 * `SPAN_KIND_CLIENT` span per call with the attributes of the .NET Bridge span (`runic.bridge.*`).
 */
export function otlpTraces(spans: readonly BrowserSpan[], serviceName: string, version: string): object {
  return {
    resourceSpans: [{
      resource: {
        attributes: [
          attribute("service.name", serviceName),
          attribute("telemetry.sdk.name", "@runic-artifex/vite-plugin-runic"),
          attribute("telemetry.sdk.language", "webjs"),
          attribute("telemetry.sdk.version", version),
        ],
      },
      scopeSpans: [{
        scope: { name: "@runic-artifex/views", version },
        spans: spans.map(span => {
          const model = span.contract.split(":", 1)[0]!.split(".").pop() || span.route;
          const attributes = [
            attribute("runic.bridge.kind", span.kind),
            attribute("runic.bridge.request", span.request),
            attribute("runic.bridge.route", span.route),
            attribute("runic.bridge.model", model),
            attribute("runic.bridge.member", span.member),
            attribute("runic.bridge.contract", span.contract),
            attribute("runic.bridge.outcome", span.outcome),
          ];
          if (span.reason !== undefined) attributes.push(attribute("runic.bridge.reason", span.reason));
          if (span.revision !== undefined) attributes.push(attribute("runic.bridge.revision", span.revision));
          if (span.handler !== undefined) {
            attributes.push(attribute("runic.bridge.handler", span.handler.name));
            attributes.push(attribute("runic.bridge.handler.duration_ms", span.handler.duration));
          }
          if (span.outcome === "error") attributes.push(attribute("error.type", span.reason ?? "error"));
          return {
            traceId: span.traceId,
            spanId: span.spanId,
            name: span.name,
            kind: 3,
            startTimeUnixNano: nanoseconds(span.start),
            endTimeUnixNano: nanoseconds(span.end),
            attributes,
            ...span.outcome === "error" ? { status: { code: 2, message: span.reason ?? "error" } } : {},
          };
        }),
      }],
    }],
  };
}

/** Batches the page's spans and posts them to the OTLP endpoint. */
export interface OtlpForwarder {
  /** Queues the span of a `runic:span` message; ignores one that is not well formed. */
  add(data: unknown): void;
  /** Posts the queued spans now. */
  flush(): Promise<void>;
  /** Posts the queued spans and stops. */
  close(): Promise<void>;
}

export interface OtlpForwarderHooks {
  readonly fetch?: (url: string, init: { method: string; headers: Record<string, string>; body: string }) => Promise<{ readonly ok: boolean; readonly status: number }>;
  readonly warn?: (message: string) => void;
  readonly version?: string;
  /** How long a span waits for others before the batch is posted: 500 ms by default. */
  readonly delay?: number;
}

// A batch is posted when it is this large or after the delay; a page that calls faster than the endpoint answers loses
// the oldest spans beyond the queue limit.
const batchSize = 64;
const queueLimit = 2_048;

export function createOtlpForwarder(otlp: ResolvedOtlp, hooks: OtlpForwarderHooks = {}): OtlpForwarder {
  const post = hooks.fetch ?? (globalThis.fetch as unknown as NonNullable<OtlpForwarderHooks["fetch"]>);
  const warn = hooks.warn ?? ((message: string) => console.warn(message));
  const delay = hooks.delay ?? 500;
  const version = hooks.version ?? "0";
  const queue: BrowserSpan[] = [];
  let timer: ReturnType<typeof setTimeout> | undefined;
  let posting: Promise<void> = Promise.resolve();
  // A failing endpoint is reported once, until an export succeeds again.
  let failing = false;
  let closed = false;

  const send = async (spans: BrowserSpan[]): Promise<void> => {
    let failure: string | undefined;
    try {
      const response = await post(otlp.url, {
        method: "POST",
        headers: { ...otlp.headers, "Content-Type": "application/json" },
        body: JSON.stringify(otlpTraces(spans, otlp.serviceName, version)),
      });
      if (!response.ok) failure = `HTTP ${response.status}`;
    } catch (error) {
      failure = error instanceof Error ? error.message : String(error);
    }
    if (failure === undefined) { failing = false; return; }
    if (failing) return;
    failing = true;
    warn(`[runic] Exporting ${spans.length} page span(s) to ${otlp.url} failed (${failure}). The development server sends OTLP/HTTP JSON: ` +
      "point OTEL_EXPORTER_OTLP_ENDPOINT at an OTLP/HTTP receiver (port 4318, or the Aspire dashboard's HTTP port), not an OTLP/gRPC one.");
  };

  const flush = (): Promise<void> => {
    if (timer !== undefined) { clearTimeout(timer); timer = undefined; }
    if (queue.length === 0) return posting;
    const spans = queue.splice(0, queue.length);
    posting = posting.then(() => send(spans));
    return posting;
  };

  return {
    add(data) {
      if (closed) return;
      const span = readBrowserSpan(data);
      if (span === undefined) return;
      queue.push(span);
      if (queue.length > queueLimit) queue.splice(0, queue.length - queueLimit);
      if (queue.length >= batchSize) void flush();
      else timer ??= setTimeout(() => { timer = undefined; void flush(); }, delay);
    },
    flush,
    async close() {
      closed = true;
      await flush();
    },
  };
}
