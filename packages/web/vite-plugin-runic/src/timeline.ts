// The development state that `runic()` and `runicDevSessionProxy()` keep in the development server process: the
// contract metadata, the page's connection state, the state inspector's Views and a bounded timeline of sanitized
// diagnostics.
import type {
  RunicDiagnosticDetail,
  RunicDiagnosticEntry,
  RunicDiagnosticFailure,
  RunicDiagnosticSource,
  RunicViewStateValue,
} from "./diagnostics.js";
import { sanitizeDiagnosticSummary, sanitizeViewState } from "./diagnostics.js";

const defaultTimelineLimit = 200;
const maximumTimelineLimit = 500;
// The state inspector keeps the Views that changed most recently, over every page of the server.
const maximumInspectedViews = 32;

export interface RunicDevelopmentState {
  readonly contract: Readonly<{
    identity: string;
    version: string;
    fingerprint: string;
  }>;
  readonly vite: Readonly<{
    command: string;
    mode: string;
    root: string;
  }>;
  /**
   * The page's connection to its .NET host, as `@runic-artifex/views` reports it
   * (`BridgeConnectionState`), or `unknown` until a View connects.
   */
  readonly connection: Readonly<{
    state: "connecting" | "connected" | "reconnecting" | "lost" | "closed" | "unknown";
  }>;
  /**
   * The state inspector: each open View's current state, as the development client last reported it, the most
   * recently changed first. Command replies that the producer answered `superseded` are timeline entries
   * (`kind: "command"`, `detail.code: "superseded"`).
   */
  readonly views: readonly RunicViewSnapshot[];
  readonly timeline: readonly RunicTimelineEntry[];
}

/** One View in the state inspector. */
export interface RunicViewSnapshot {
  /** The page the View is open in: a random identifier per page load, as several windows share one server. */
  readonly page: string;
  /** The View's route, such as `editor`. */
  readonly route: string;
  /** The revision of `state`, and its presentation epoch on a connection with `presentation-epoch/1`. */
  readonly revision?: number;
  readonly epoch?: number;
  /** How many presentations of the page show the route. */
  readonly presentations: number;
  readonly updatedAt: string;
  /** The state the page shows, bounded and redacted like timeline details. */
  readonly state: RunicViewStateValue;
}

export type RunicTimelineEntry = Readonly<Required<Pick<RunicDiagnosticEntry, "id" | "timestamp" | "kind" | "label">> & {
  source?: RunicDiagnosticSource;
  detail: RunicDiagnosticDetail;
  failure?: RunicDiagnosticFailure;
}>;

export interface DevelopmentTimeline {
  readonly state: RunicDevelopmentState;
  /** Replaces the state without notifying, for configuration such as the resolved Vite command. */
  configure(update: (current: RunicDevelopmentState) => RunicDevelopmentState): void;
  /** Sanitizes and appends an entry; returns it, or `undefined` for a candidate that is not a diagnostic. */
  appendDiagnostic(candidate: unknown, defaultSource?: RunicDiagnosticSource): RunicTimelineEntry | undefined;
  /** Applies a `{ state }` connection report; returns whether the state changed. */
  applyConnection(candidate: unknown): boolean;
  /**
   * Applies a state inspector report: `{ page, route, revision?, epoch?, presentations, state }` for a View's state,
   * `{ page, route, closed: true }` for a closed View, `{ page, closed: true }` for a page that went away. Returns
   * whether the report was valid.
   */
  applyView(candidate: unknown): boolean;
}

export function createDevelopmentTimeline(
  initial: RunicDevelopmentState,
  maxTimelineEntries: number,
  changed: (state: RunicDevelopmentState) => void,
): DevelopmentTimeline {
  let state = initial;
  let nextSequence = 1;
  return {
    get state() { return state; },
    configure(update) { state = update(state); },
    appendDiagnostic(candidate, defaultSource) {
      const summary = sanitizeDiagnosticSummary(candidate, defaultSource);
      if (!summary) return undefined;
      const entry: RunicTimelineEntry = {
        ...summary,
        id: `diagnostic-${nextSequence}`,
        timestamp: new Date().toISOString(),
      };
      nextSequence += 1;
      state = { ...state, timeline: [...state.timeline, entry].slice(-maxTimelineEntries) };
      changed(state);
      return entry;
    },
    applyConnection(candidate) {
      const next = connectionState(isRecord(candidate) ? candidate.state : undefined);
      if (next === state.connection.state) return false;
      state = { ...state, connection: { state: next } };
      changed(state);
      return true;
    },
    applyView(candidate) {
      if (!isRecord(candidate) || typeof candidate.page !== "string" || !pagePattern.test(candidate.page)) return false;
      const page = candidate.page;
      const route = typeof candidate.route === "string" && routePattern.test(candidate.route) ? candidate.route : undefined;
      if (candidate.closed === true) {
        const views = state.views.filter((view) => view.page !== page || (route !== undefined && view.route !== route));
        if (views.length !== state.views.length) {
          state = { ...state, views };
          changed(state);
        }
        return true;
      }
      if (route === undefined || !Object.hasOwn(candidate, "state")) return false;
      const revision = counter(candidate.revision);
      const epoch = counter(candidate.epoch);
      const snapshot: RunicViewSnapshot = {
        page,
        route,
        ...(revision === undefined ? {} : { revision }),
        ...(epoch === undefined ? {} : { epoch }),
        presentations: counter(candidate.presentations) ?? 0,
        updatedAt: new Date().toISOString(),
        state: sanitizeViewState(candidate.state),
      };
      const others = state.views.filter((view) => view.page !== page || view.route !== route);
      state = { ...state, views: [snapshot, ...others].slice(0, maximumInspectedViews) };
      changed(state);
      return true;
    },
  };
}

// The development client's page identifier, and a route name (specs/application, "Route names").
const pagePattern = /^[a-z0-9]{1,32}$/u;
const routePattern = /^[A-Za-z0-9_.-]{1,128}$/u;

function counter(value: unknown): number | undefined {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : undefined;
}

export function initialDevelopmentState(
  contract: Readonly<{ identity?: string; version?: string; fingerprint?: string }> | undefined,
  vite: RunicDevelopmentState["vite"] = { command: "serve", mode: "development", root: "" },
): RunicDevelopmentState {
  return {
    contract: {
      identity: cleanString(contract?.identity),
      version: cleanString(contract?.version),
      fingerprint: cleanString(contract?.fingerprint),
    },
    vite,
    connection: { state: "unknown" },
    views: [],
    timeline: [],
  };
}

// Failures keep file paths and stacks for local display only (the dock, the development server's terminal). The
// state endpoint and the copied state stay path-free.
export function withoutFailures(current: RunicDevelopmentState): RunicDevelopmentState {
  return {
    ...current,
    timeline: current.timeline.map(({ failure: _failure, ...entry }) => entry),
  };
}

export function timelineLimit(value: number | undefined): number {
  if (typeof value !== "number" || !Number.isFinite(value)) return defaultTimelineLimit;
  return Math.min(maximumTimelineLimit, Math.max(1, Math.floor(value)));
}

function connectionState(value: unknown): RunicDevelopmentState["connection"]["state"] {
  return value === "connecting" || value === "connected" || value === "reconnecting" || value === "lost" || value === "closed"
    ? value
    : "unknown";
}

function cleanString(value: unknown): string {
  return typeof value === "string" ? value.slice(0, 512) : "";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
