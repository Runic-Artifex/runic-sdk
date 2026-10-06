export type RunicTraceKind =
  | "command"
  | "receipt"
  | "event"
  | "operation"
  | "connection"
  | "error";

export type RunicDiagnosticSource =
  | "views"
  | "assets"
  | "translations";

export type RunicDiagnosticDetailValue = string | number | boolean | null;
export type RunicDiagnosticDetail = Readonly<Record<string, RunicDiagnosticDetailValue>>;

/**
 * The local failure behind an `error` entry: an exception type, message and
 * stack. Views sends it only when .NET runs in development or for a browser
 * error. It keeps file paths, which a stack needs to be actionable, but is
 * bounded and has credentials redacted.
 */
export interface RunicDiagnosticFailure {
  readonly type: string;
  readonly message: string;
  readonly stack?: string;
}

export interface RunicDiagnosticEntry {
  readonly source: RunicDiagnosticSource;
  readonly id?: string;
  readonly timestamp?: string;
  readonly kind: RunicTraceKind;
  readonly label: string;
  readonly detail?: RunicDiagnosticDetail;
  /** Accepted only for `kind: "error"`. */
  readonly failure?: RunicDiagnosticFailure;
}

export interface RunicDiagnosticSummary {
  readonly source: RunicDiagnosticSource;
  readonly kind: RunicTraceKind;
  readonly label: string;
  readonly detail: RunicDiagnosticDetail;
  readonly failure?: RunicDiagnosticFailure;
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
const maximumFailureTypeLength = 200;
const maximumFailureMessageLength = 2_000;
const maximumFailureStackLength = 8_000;
const maximumFailureStackLines = 80;
const redacted = "[redacted]";
const sensitiveKey = /token|secret|capability|passw(?:or)?d|pwd|access[-_]?key|account[-_]?key|path|file(?:name)?|directory|cwd|frame|stack|authorization|cookie|credential|query|uri|url|api[-_]?key/iu;
// A credential name may carry a prefix (`access_token`, `client_secret`) and a
// closing JSON quote before its separator (`{"password":"x"}`).
const credentialName = String.raw`[a-z0-9_.-]*(?:token|secret|passw(?:or)?d|pwd|authorization|cookie|credential|api[-_]?key|access[-_]?key|account[-_]?key)`;
const credentialValue = String.raw`(?:bearer\s+)?(?:"[^"]*"|'[^']*'|[^\s"',;&}]+)`;
const urlPassword = String.raw`[a-z][a-z0-9+.-]*:\/\/[^\s:/@]+:[^\s@/]+@`;
const sensitiveContent = new RegExp(String.raw`bearer\s+\S+|${credentialName}["']?\s*[:=]\s*\S|${urlPassword}`, "iu");
const pathLikeContent = /(?:\b[a-z][a-z0-9+.-]*:(?:\/\/|[\\/])|(?:^|[\s"'(=:])(?:~?\/|\.{1,2}[\\/]|[A-Za-z]:[\\/]|\\\\|[^\s/\\]+[\\/][^\s/\\]+))/iu;
const controlCharacters = /[\u0000-\u001F\u007F]/u;
const failureControlCharacters = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/gu;
// Failure text keeps its structure and replaces only the secret value.
const sensitiveAssignment = new RegExp(String.raw`(${credentialName}["']?\s*[:=]\s*)(${credentialValue})`, "giu");
const sensitiveUrlPassword = new RegExp(String.raw`([a-z][a-z0-9+.-]*:\/\/[^\s:/@]+:)[^\s@/]+@`, "giu");
const bearerCredential = /(bearer)\s+(?!\[redacted\])\S+/giu;

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
  const failure = kind === "error" ? sanitizeFailure(candidate.failure) : undefined;
  // The failure has its own bounds; the summary budget covers the rest.
  const fitted = fitSummary(summary);
  return failure ? { ...fitted, failure } : fitted;
}

function sanitizeFailure(candidate: unknown): RunicDiagnosticFailure | undefined {
  if (!isRecord(candidate)) return undefined;
  const type = failureText(candidate.type, maximumFailureTypeLength, false);
  const message = failureText(candidate.message, maximumFailureMessageLength, false);
  if (!type || message === undefined) return undefined;
  const stack = failureText(candidate.stack, maximumFailureStackLength, true);
  return stack ? { type, message, stack } : { type, message };
}

// Truncates instead of dropping: the start of a stack is the useful part.
function failureText(value: unknown, maximumLength: number, multiline: boolean): string | undefined {
  if (typeof value !== "string") return undefined;
  let text = value.replace(failureControlCharacters, "");
  if (!multiline) text = text.replace(/[\r\n\t]+/gu, " ");
  else text = text.split(/\r?\n/u).slice(0, maximumFailureStackLines).join("\n");
  if (text.length > maximumLength) text = `${text.slice(0, maximumLength - 1)}…`;
  return text
    .replace(sensitiveAssignment, (_match, name: string, value: string) =>
      `${name}${value.startsWith('"') ? `"${redacted}"` : value.startsWith("'") ? `'${redacted}'` : redacted}`)
    .replace(sensitiveUrlPassword, `$1${redacted}@`)
    .replace(bearerCredential, `$1 ${redacted}`);
}

export function diagnosticSource(value: unknown): RunicDiagnosticSource | undefined {
  return value === "views" || value === "assets" || value === "translations"
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
