import { error, type RequestEvent } from "@sveltejs/kit";
import type { RunicLocaleRouting } from "../translations/routing.js";
import { localeFromLocals } from "../translations/server.js";

/** The only cookie SvelteKit may forward to the C# hosted-service session endpoint. */
export const RUNIC_HOSTED_SESSION_COOKIE = "__Host-runic-session";
/** The C# endpoint returning the sanitized hosted-service session projection. */
export const RUNIC_HOSTED_SESSION_PATH = "/runic/service/session";

/** The bounded identity facts C# permits SvelteKit to render. */
export interface RunicHostedSession {
  readonly subject: string;
  readonly displayName: string | null;
  readonly roles: readonly string[];
}

/** Serializable SSR input that must match the browser's bootstrap marker. */
export interface RunicHostedBootstrap<Locale extends string = string> {
  readonly schema: "runic.hosted-bootstrap/1";
  readonly locale: Locale;
  readonly session: RunicHostedSession;
  readonly fingerprint: string;
}

/** Receives only the opaque host cookie, never bearer or service credentials. */
export type RunicHostedSessionLoader = (
  request: Readonly<{ path: typeof RUNIC_HOSTED_SESSION_PATH; cookie: string }>,
) => Promise<Response>;

export interface RunicHostedSsrLoadOptions {
  readonly loadSession: RunicHostedSessionLoader;
  readonly localsKey?: string;
}

/**
 * Creates a server-only SvelteKit load function for the D008 hosted profile.
 * It forwards exactly one opaque host cookie to C# and returns only sanitized
 * session facts plus the request-scoped Runic locale.
 */
export function createRunicHostedSsrLoad<Locale extends string>(
  routing: RunicLocaleRouting<Locale>,
  options: RunicHostedSsrLoadOptions,
): (event: RequestEvent) => Promise<Readonly<{ bootstrap: RunicHostedBootstrap<Locale> }>> {
  if (!options || typeof options.loadSession !== "function") {
    throw new TypeError("createRunicHostedSsrLoad requires a C# hosted-session loader.");
  }
  const localsKey = options.localsKey ?? "locale";
  return async (event) => {
    const locale = localeFromLocals(event.locals, routing, localsKey);
    const cookie = opaqueSessionCookie(event.request.headers.get("cookie"));
    if (!cookie) error(401, "A single Runic hosted session cookie is required.");
    let response: Response;
    try {
      response = await options.loadSession({ path: RUNIC_HOSTED_SESSION_PATH, cookie });
    } catch {
      error(502, "The C# hosted-session endpoint is unavailable.");
    }
    if (response.status === 401 || response.status === 403) error(401, "The hosted session is not authorized.");
    if (!response.ok) error(502, "The C# hosted-session endpoint rejected its response.");
    let body: unknown;
    try {
      body = await response.json();
    } catch {
      error(502, "The C# hosted-session endpoint returned invalid JSON.");
    }
    let session: RunicHostedSession;
    try {
      session = parseSession(body);
    } catch {
      error(502, "The C# hosted-session projection is malformed.");
    }
    return Object.freeze({ bootstrap: createRunicHostedBootstrap(locale, session) });
  };
}

/** Creates the immutable, deterministic browser-bootstrap identity for one authorized request. */
export function createRunicHostedBootstrap<Locale extends string>(
  locale: Locale,
  session: RunicHostedSession,
): RunicHostedBootstrap<Locale> {
  if (typeof locale !== "string" || locale.length === 0) throw new TypeError("The hosted bootstrap locale is required.");
  const normalized = parseSession(session);
  const value = JSON.stringify(["runic.hosted-bootstrap/1", locale, normalized.subject, normalized.displayName, normalized.roles]);
  return Object.freeze({
    schema: "runic.hosted-bootstrap/1",
    locale,
    session: normalized,
    fingerprint: `runic-hosted-${fnv1a(value)}`,
  });
}

function opaqueSessionCookie(header: string | null): string | undefined {
  if (!header) return undefined;
  const matches = header.split(";").map((part) => part.trim())
    .filter((part) => part.startsWith(`${RUNIC_HOSTED_SESSION_COOKIE}=`));
  return matches.length === 1 && matches[0]!.length > RUNIC_HOSTED_SESSION_COOKIE.length + 1
    ? matches[0]
    : undefined;
}

function parseSession(value: unknown): RunicHostedSession {
  if (!isRecord(value) || Object.keys(value).some((key) => key !== "subject" && key !== "displayName" && key !== "roles")) {
    throw new TypeError("The C# hosted-session projection is malformed.");
  }
  const { subject, displayName, roles } = value;
  if (typeof subject !== "string" || subject.trim().length === 0 || subject.length > 128 ||
      (displayName !== null && typeof displayName !== "string") ||
      (typeof displayName === "string" && displayName.length > 128) ||
      !Array.isArray(roles) || roles.length > 16 ||
      roles.some((role) => typeof role !== "string" || role.trim().length === 0 || role.length > 64)) {
    throw new TypeError("The C# hosted-session projection is malformed.");
  }
  const sortedRoles = [...roles].sort(compareOrdinal);
  if (new Set(sortedRoles).size !== sortedRoles.length || sortedRoles.some((role, index) => role !== roles[index])) {
    throw new TypeError("The C# hosted-session roles are not canonical.");
  }
  return Object.freeze({ subject, displayName, roles: Object.freeze(sortedRoles) });
}

/** Matches the UTF-16 code-unit ordering used by C# StringComparer.Ordinal. */
function compareOrdinal(left: string, right: string): number {
  if (left === right) return 0;
  return left < right ? -1 : 1;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function fnv1a(value: string): string {
  let hash = 0x811c9dc5;
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16).padStart(8, "0");
}
