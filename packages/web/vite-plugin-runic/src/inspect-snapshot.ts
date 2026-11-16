// Runic Inspect's page snapshot: the page's Views with their states and C# ViewModels, and its recent Bridge calls with
// their timing, outcome and C# handler. Runic Inspect keeps the records from the same development channels as the
// overview (`runic.views.inspector`, `runic.views.calls`); `snapshot()` on its API assembles them with the development
// source map, so the Runic DevTools extension's panel (tools/dotnet-runic/devtools-extension) and agents read one JSON
// value. No DOM access here, so the assembly is testable on its own.

import type { RunicSourceMap } from "./sources.js";
import {
  indexSourceMap, memberKinds, previewValue, runicLocation, viewModelForRoute, type RunicInspectCall, type RunicInspectLocation,
} from "./inspect-data.js";

/** A View the page shows: a route with its latest state, as the state inspector channel reported it. */
export interface RunicInspectView {
  readonly route: string;
  readonly revision?: number;
  readonly epoch?: number;
  /** How many presentations of the page show the route. */
  readonly presentations?: number;
  /** The View contract, `{C# view model}:{fingerprint}`, from the route's Bridge calls or the source map. */
  readonly contract?: string;
  /** The C# ViewModel, from the development source map. */
  readonly viewModel?: {
    readonly id: string;
    readonly name: string;
    readonly symbol: string;
    readonly location?: RunicInspectLocation;
  };
  /** The exposed state, shortened (at most 3 levels, 20 items and 30 members each). */
  readonly state: unknown;
}

/** A recent Bridge call of the page, newest first in a snapshot. Times are `performance.now()` milliseconds. */
export interface RunicInspectRecentCall extends RunicInspectCall {
  readonly contract?: string;
  /**
   * The C# handler as the .NET host named and timed it (handler-timing/1, Development only), in milliseconds.
   */
  readonly handler?: { readonly name: string; readonly duration: number };
  /** The C# ViewModel and member from the development source map, and where the member is. */
  readonly viewModel?: string;
  readonly symbol?: string;
  readonly location?: RunicInspectLocation;
}

/** What `snapshot()` returns. */
export interface RunicInspectSnapshot {
  readonly version: 1;
  /** The page's open Views, by route. */
  readonly views: readonly RunicInspectView[];
  /** The page's recent Bridge calls, newest first; a call in progress has no `duration` or `outcome` yet. */
  readonly calls: readonly RunicInspectRecentCall[];
  /** What the snapshot cannot tell, and why. */
  readonly notes: readonly string[];
}

/** What Runic Inspect keeps of a route from the state inspector channel. */
export interface ViewRecord {
  readonly state: unknown;
  readonly revision?: number | undefined;
  readonly epoch?: number | undefined;
  readonly presentations?: number | undefined;
}

/** A call as the call log keeps it. */
export interface CallRecord {
  id: number;
  kind: string;
  request: string;
  route: string;
  contract?: string;
  member: string;
  start: number;
  duration?: number;
  outcome?: string;
  reason?: string;
  revision?: number;
  handler?: { name: string; duration: number };
}

/** The page's most recent Bridge calls from the call channel's events, newest first. */
export class CallLog {
  readonly #maximum: number;
  readonly #calls: CallRecord[] = [];
  readonly #pending = new Map<number, CallRecord>();

  constructor(maximum = 50) {
    this.#maximum = maximum;
  }

  /** Records a `start` or `end` event of the call channel; ignores anything else. */
  record(event: unknown): void {
    if (typeof event !== "object" || event === null) return;
    const value = event as Record<string, unknown>;
    if (typeof value["id"] !== "number") return;
    if (value["phase"] === "start") {
      if (typeof value["route"] !== "string" || typeof value["member"] !== "string") return;
      const call: CallRecord = {
        id: value["id"], kind: String(value["kind"]), request: String(value["request"]), route: value["route"],
        member: value["member"], start: typeof value["start"] === "number" ? value["start"] : 0,
        ...typeof value["contract"] === "string" ? { contract: value["contract"] } : {},
      };
      this.#calls.unshift(call);
      if (this.#calls.length > this.#maximum) {
        for (const dropped of this.#calls.splice(this.#maximum)) this.#pending.delete(dropped.id);
      }
      this.#pending.set(call.id, call);
    } else if (value["phase"] === "end") {
      const call = this.#pending.get(value["id"]);
      if (call === undefined) return;
      this.#pending.delete(call.id);
      if (typeof value["duration"] === "number") call.duration = value["duration"];
      if (typeof value["outcome"] === "string") call.outcome = value["outcome"];
      if (typeof value["reason"] === "string") call.reason = value["reason"];
      if (typeof value["revision"] === "number") call.revision = value["revision"];
      const handler = value["handler"] as { name?: unknown; duration?: unknown; start?: unknown; end?: unknown } | undefined;
      if (typeof handler === "object" && handler !== null && typeof handler.name === "string") {
        const duration = typeof handler.duration === "number" ? handler.duration
          : typeof handler.start === "number" && typeof handler.end === "number" ? handler.end - handler.start : undefined;
        if (duration !== undefined) call.handler = { name: handler.name, duration };
      }
    }
  }

  /** Copies of the recorded calls, newest first. */
  list(): CallRecord[] {
    return this.#calls.map((call) => ({ ...call, ...call.handler === undefined ? {} : { handler: { ...call.handler } } }));
  }

  clear(): void {
    this.#calls.length = 0;
    this.#pending.clear();
  }
}

/** What inspect.ts read for a snapshot. */
export interface SnapshotInput {
  readonly views: ReadonlyMap<string, ViewRecord>;
  /** Each route's contract, from the Bridge calls seen. */
  readonly contracts: ReadonlyMap<string, string>;
  /** Newest first. */
  readonly calls: readonly CallRecord[];
  readonly sourceMap?: RunicSourceMap | undefined;
  readonly sourceMapStatus?: "missing" | "unavailable" | "forbidden" | undefined;
}

/** Assembles a page snapshot from what Runic Inspect recorded and the development source map. */
export function assembleSnapshot(input: SnapshotInput): RunicInspectSnapshot {
  const index = input.sourceMap === undefined ? undefined : indexSourceMap(input.sourceMap);
  const viewModelOf = (route: string, contract: string | undefined) => index === undefined ? undefined
    : viewModelForRoute(index, route, contract ?? input.contracts.get(route), input.views.get(route)?.state);

  const views: RunicInspectView[] = [...input.views].map(([route, record]) => {
    const viewModel = viewModelOf(route, undefined);
    const contract = input.contracts.get(route) ?? viewModel?.contract;
    const location = viewModel === undefined ? undefined : runicLocation(viewModel);
    return {
      route,
      ...typeof record.revision === "number" ? { revision: record.revision } : {},
      ...typeof record.epoch === "number" ? { epoch: record.epoch } : {},
      ...typeof record.presentations === "number" ? { presentations: record.presentations } : {},
      ...contract === undefined ? {} : { contract },
      ...viewModel === undefined ? {} : {
        viewModel: {
          id: viewModel.id, name: viewModel.name, symbol: viewModel.symbol, ...location === undefined ? {} : { location },
        },
      },
      state: previewValue(record.state) ?? null,
    };
  }).sort((a, b) => a.route.localeCompare(b.route));

  const calls: RunicInspectRecentCall[] = input.calls.map((call) => {
    const viewModel = viewModelOf(call.route, call.contract);
    let entry;
    if (index !== undefined && viewModel !== undefined) {
      for (const kind of memberKinds(call.kind)) {
        entry = index.members.get(`${viewModel.id}/${kind}/${call.member}`);
        if (entry !== undefined) break;
      }
    }
    const location = entry === undefined ? undefined : runicLocation(entry);
    return {
      id: call.id, kind: call.kind, request: call.request, route: call.route, member: call.member, start: call.start,
      ...call.contract === undefined ? {} : { contract: call.contract },
      ...call.duration === undefined ? {} : { duration: call.duration },
      ...call.outcome === undefined ? {} : { outcome: call.outcome },
      ...call.reason === undefined ? {} : { reason: call.reason },
      ...call.revision === undefined ? {} : { revision: call.revision },
      ...call.handler === undefined ? {} : { handler: { name: call.handler.name, duration: call.handler.duration } },
      ...viewModel === undefined ? {} : { viewModel: viewModel.id },
      ...entry === undefined ? {} : { symbol: entry.symbol },
      ...location === undefined ? {} : { location },
    };
  });

  const notes: string[] = [];
  if (input.sourceMapStatus === "forbidden") {
    notes.push("The development server does not allow this machine to read the source map (runic({ allow }), RUNIC_DEV_ALLOW), so C# locations are missing.");
  } else if (input.sourceMapStatus === "missing") {
    notes.push("No development source map: C# ViewModels and handlers are not named. Run the page under `dotnet runic dev` or pass runic({ sourceMap }).");
  } else if (input.sourceMapStatus === "unavailable") {
    notes.push("The development source map did not load, so C# locations are missing.");
  }
  if (views.length === 0) notes.push("No open View on this page yet.");
  if (calls.length > 0 && !calls.some((call) => call.handler !== undefined)) {
    notes.push("C# handler timing comes from a .NET host in Development (handler-timing/1); a mock or production host does not send it.");
  }
  return { version: 1, views, calls, notes };
}
