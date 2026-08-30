export type RunicTraceKind =
  | "command"
  | "receipt"
  | "event"
  | "operation"
  | "connection"
  | "error";

export type RunicDiagnosticSource =
  | "application-bridge"
  | "assets"
  | "translations";

export type RunicDiagnosticDetailValue = string | number | boolean | null;
export type RunicDiagnosticDetail = Readonly<Record<string, RunicDiagnosticDetailValue>>;

export interface RunicDiagnosticEntry {
  readonly source: RunicDiagnosticSource;
  readonly id?: string;
  readonly timestamp?: string;
  readonly kind: RunicTraceKind;
  readonly label: string;
  readonly detail?: RunicDiagnosticDetail;
}

export interface RunicTraceEntry {
  readonly id?: string;
  readonly timestamp?: string;
  readonly kind: RunicTraceKind;
  readonly label: string;
  /** Legacy bridge input; it is reduced to DiagnosticDetail before transport. */
  readonly detail?: Readonly<Record<string, unknown>>;
}

export interface RunicDiagnosticSummary {
  readonly source: RunicDiagnosticSource;
  readonly kind: RunicTraceKind;
  readonly label: string;
  readonly detail: RunicDiagnosticDetail;
}

const allowedKinds = new Set<RunicTraceKind>([
  "command", "receipt", "event", "operation", "connection", "error",
]);
const maximumDetailEntries = 12;
const maximumDetailCandidates = 24;
const maximumDetailKeyLength = 40;
const maximumDetailStringLength = 96;
const maximumLabelLength = 160;
// Leaves room for the server-owned id and timestamp in the public entry.
const maximumSerializedSummaryLength = 1_800;
const redacted = "[redacted]";
const sensitiveKey = /token|secret|capability|password|path|file(?:name)?|directory|cwd|frame|stack|authorization|cookie|credential|query|uri|url|api[-_]?key/iu;
const sensitiveContent = /(?:bearer\s+\S+|(?:token|secret|password|authorization|cookie|credential|api[-_]?key)\s*[:=]\s*\S+)/iu;
const pathLikeContent = /(?:\b[a-z][a-z0-9+.-]*:(?:\/\/|[\\/])|(?:^|[\s"'(=:])(?:~?\/|\.{1,2}[\\/]|[A-Za-z]:[\\/]|\\\\|[^\s/\\]+[\\/][^\s/\\]+))/iu;
const controlCharacters = /[\u0000-\u001F\u007F]/u;

/**
 * Converts an arbitrary diagnostic candidate into the only shape that may be
 * transported or displayed. The caller still owns identifier and timestamp.
 */
export function sanitizeDiagnosticSummary(
  candidate: unknown,
  forcedSource?: RunicDiagnosticSource,
): RunicDiagnosticSummary | undefined {
  if (!isRecord(candidate)) return undefined;
  const source = forcedSource ?? diagnosticSource(candidate.source);
  const kind = typeof candidate.kind === "string" && allowedKinds.has(candidate.kind as RunicTraceKind)
    ? candidate.kind as RunicTraceKind
    : undefined;
  const label = sanitizeText(candidate.label, maximumLabelLength);
  if (!source || !kind || !label) return undefined;

  const summary: RunicDiagnosticSummary = {
    source,
    kind,
    label,
    detail: sanitizeDetail(candidate.detail),
  };
  return fitSummary(summary);
}

export function diagnosticSource(value: unknown): RunicDiagnosticSource | undefined {
  return value === "application-bridge" || value === "assets" || value === "translations"
    ? value
    : undefined;
}

function sanitizeDetail(candidate: unknown): RunicDiagnosticDetail {
  if (!isRecord(candidate)) return {};
  const output: Record<string, RunicDiagnosticDetailValue> = {};
  let count = 0;
  let inspected = 0;
  for (const key in candidate) {
    if (!Object.prototype.hasOwnProperty.call(candidate, key)) continue;
    if (inspected >= maximumDetailCandidates) break;
    inspected += 1;
    if (count >= maximumDetailEntries) break;
    const cleanKey = sanitizeKey(key);
    if (!cleanKey || sensitiveKey.test(cleanKey)) continue;
    const value = candidate[key];
    if (typeof value === "string") {
      output[cleanKey] = sanitizeText(value, maximumDetailStringLength) ?? redacted;
    } else if (typeof value === "number" && Number.isFinite(value)) {
      output[cleanKey] = value;
    } else if (typeof value === "boolean" || value === null) {
      output[cleanKey] = value;
    } else {
      continue;
    }
    count += 1;
  }
  return output;
}

function fitSummary(summary: RunicDiagnosticSummary): RunicDiagnosticSummary {
  const detail = { ...summary.detail };
  while (JSON.stringify({ ...summary, detail }).length > maximumSerializedSummaryLength) {
    const key = Object.keys(detail).at(-1);
    if (key === undefined) return { ...summary, detail: {} };
    delete detail[key];
  }
  return { ...summary, detail };
}

function sanitizeKey(value: string): string | undefined {
  if (value.length === 0 || value.length > maximumDetailKeyLength || controlCharacters.test(value)) return undefined;
  if (value === "__proto__" || value === "constructor" || value === "prototype") return undefined;
  return pathLikeContent.test(value) ? undefined : value;
}

function sanitizeText(value: unknown, maximumLength: number): string | undefined {
  if (typeof value !== "string" || value.length === 0 || value.length > maximumLength) return undefined;
  if (controlCharacters.test(value)) return undefined;
  return sensitiveContent.test(value) || pathLikeContent.test(value) ? redacted : value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
