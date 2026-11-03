/// <reference lib="esnext.disposable" preserve="true" />
// Query collections (query-collections/1, specs/application/README.md, "Query collections"): a large list whose rows stay
// in .NET. The route's state carries the collection's query, total and version; each presentation reads the rows of its
// own range with `query-rows`, so two pages that scroll one list never move each other's rows.
import { BridgeError } from "./errors.js";
import { disposable } from "./lifetime.js";
import { replyError, type Message, type Reply } from "./protocol.js";
import { errorMessage } from "./runtime.js";

/** One sort key of a {@link RunicQuery}. */
export interface RunicSortKey {
  /** The field, normally a row member's wire name. */
  readonly field: string;
  /** Whether larger values come first. */
  readonly descending?: boolean;
}

/**
 * One filter of a {@link RunicQuery}. Values are encoded as the field's wire type (an `int64` as a decimal string).
 * `range` bounds are inclusive and either may be absent. A `custom` filter is one the .NET row source defines, by name.
 */
export type RunicFilter =
  | { readonly field: string; readonly op: "equals"; readonly value: unknown }
  | { readonly field: string; readonly op: "contains"; readonly value: string }
  | { readonly field: string; readonly op: "range"; readonly min?: unknown; readonly max?: unknown }
  | { readonly field: string; readonly op: "custom"; readonly value?: unknown };

/** The sort and filter of a query collection. Filters all apply. */
export interface RunicQuery {
  readonly sort?: readonly RunicSortKey[];
  readonly filters?: readonly RunicFilter[];
}

/** A query as the producer publishes it: every member present. */
export interface QueryCollectionQuery {
  readonly sort: readonly { readonly field: string; readonly descending: boolean }[];
  readonly filters: readonly RunicFilter[];
}

/** The state member of a query collection: its query, the number of matching rows, and its version. */
export interface QueryCollectionState {
  readonly query: QueryCollectionQuery;
  /** The rows that match the query, or `null` until .NET counted them. */
  readonly total: number | null;
  /** Increases whenever loaded rows may be out of date: the page loads its range again. */
  readonly version: number;
}

/** One row of a query collection's range: loaded, or a placeholder. */
export interface QueryRow<TRow> {
  /** The row's position in the query's order. */
  readonly index: number;
  /** `loaded` rows have `key` and `row`; `loading` and `error` rows are placeholders. */
  readonly status: "loaded" | "loading" | "error";
  readonly key: string | undefined;
  readonly row: TRow | undefined;
  /** A loaded row of an earlier version, shown until its new version arrives. */
  readonly stale: boolean;
}

/** What a query collection shows for its range. A new object on every change. */
export interface QueryCollectionSnapshot<TRow> {
  readonly query: QueryCollectionQuery;
  /** The rows that match the query, or `null` until it is known. */
  readonly total: number | null;
  readonly version: number;
  /** The range set with `setRange`, limited to `total`. */
  readonly start: number;
  readonly count: number;
  /** One entry per position of the range, from `start`: the row, or a placeholder. */
  readonly rows: readonly QueryRow<TRow>[];
  /** Whether rows of the range are being loaded. */
  readonly loading: boolean;
  /** The failure of the latest load of the range, until a load succeeds or `retry()`. */
  readonly error: BridgeError | undefined;
}

/**
 * The rows of one query collection for one presentation. Generated clients expose one per collection as
 * `client.queries.{name}`; framework bindings follow it with `useQueryCollection`.
 */
export interface QueryCollection<TRow> {
  readonly current: QueryCollectionSnapshot<TRow>;
  /** Calls `listener` after each change of `current`, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /**
   * Shows `count` rows from `start` (at most 1,000). Rows it has not loaded become placeholders and are requested from
   * .NET: one request at a time, for the latest range, so fast scrolling sends few requests.
   */
  setRange(start: number, count: number): void;
  /**
   * Changes the collection's sort and filter, which is ViewModel state that every presentation shares. Resolves once
   * the state includes it; rejects with a `BridgeError` (`rejected`, reason `invalid-input`) for a query .NET rejects.
   */
  setQuery(query: RunicQuery): Promise<void>;
  /** The position of the row with `key` in the current query's order, or `undefined` when no row has it. */
  indexOf(key: string): Promise<number | undefined>;
  /** The row at any position: loaded, or a placeholder. */
  row(index: number): QueryRow<TRow>;
  /** Loads the range again after a failure. */
  retry(): void;
  /** Stops loading; the generated client disposes its collections with itself. */
  dispose(): void;
}

/**
 * How a generated client decodes a query collection's rows, and the factory of its collections, which the generated
 * client passes in so a View without query collections does not bundle them. Generated code is its only intended caller.
 */
export interface QueryCollectionDefinition<TRow> {
  readonly decode: (wire: unknown) => TRow;
  readonly create: (scope: QueryCollectionScope, definition: QueryCollectionDefinition<TRow>) => QueryCollection<TRow> & Disposable;
}

/** The connection a query collection uses. Internal to the runtime. */
export interface QueryCollectionScope {
  readonly route: string;
  readonly member: string;
  /** The member's current decoded state, or `undefined` while the presentation has none. */
  state(): QueryCollectionState | undefined;
  /** Calls `listener` after each change of the presentation's state. */
  onState(listener: () => void): () => void;
  /** Sends a request of the route, after checking the connection. */
  request(message: Message): Promise<Reply>;
  /** Sends `query-set` and resolves when the route reached the reply's revision. */
  setQuery(query: Message): Promise<void>;
}

/** The most rows one range has. */
export const maximumQueryRange = 1_000;

/** Describes a query collection's rows for `connectView`. Generated modules call it; application code does not. */
export function defineQueryCollection<TRow>(decode: (wire: unknown) => TRow): QueryCollectionDefinition<TRow> {
  return { decode, create: createQueryCollection };
}

const isRecord = (value: unknown): value is Readonly<Record<string, unknown>> =>
  value !== null && typeof value === "object" && !Array.isArray(value);
const isSafe = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= 0;

/** Decodes a query collection's state member. Generated modules call it. */
export function decodeQueryCollectionState(wire: unknown): QueryCollectionState {
  if (!isRecord(wire) || !isRecord(wire["query"]) || !isSafe(wire["version"]) || !(wire["total"] === null || isSafe(wire["total"])))
    throw new TypeError("Expected a query collection state.");
  return { query: normalizeQuery(wire["query"] as RunicQuery), total: wire["total"] as number | null, version: wire["version"] };
}

/** The canonical wire form of a query: `sort` and `filters` present, members in the producer's order. */
export function normalizeQuery(query: RunicQuery): QueryCollectionQuery {
  if (!isRecord(query)) throw new TypeError("A query is an object.");
  const sort = query.sort ?? [];
  const filters = query.filters ?? [];
  if (!Array.isArray(sort) || sort.length > 8) throw new RangeError("A query has at most 8 sort keys.");
  if (!Array.isArray(filters) || filters.length > 32) throw new RangeError("A query has at most 32 filters.");
  const field = (value: unknown) => {
    if (typeof value !== "string" || value.length < 1 || value.length > 128) throw new RangeError("A query field has 1 to 128 characters.");
    return value;
  };
  return {
    sort: sort.map(key => ({ field: field(key.field), descending: key.descending === true })),
    filters: filters.map((filter): RunicFilter => {
      const name = field(filter.field);
      switch (filter.op) {
        case "equals": return { field: name, op: "equals", value: filter.value };
        case "contains":
          if (typeof filter.value !== "string") throw new TypeError("A contains filter's value is a string.");
          return { field: name, op: "contains", value: filter.value };
        case "range": return {
          field: name, op: "range",
          ...(filter.min === undefined || filter.min === null ? {} : { min: filter.min }),
          ...(filter.max === undefined || filter.max === null ? {} : { max: filter.max }),
        };
        case "custom": return { field: name, op: "custom", ...(filter.value === undefined ? {} : { value: filter.value }) };
        default: throw new TypeError("A filter's op is equals, contains, range or custom.");
      }
    }),
  };
}

const queryText = (query: QueryCollectionQuery) => JSON.stringify(query);

interface CachedRow<TRow> {
  readonly key: string;
  readonly row: TRow;
  readonly version: number;
}

const emptyQuery: QueryCollectionQuery = { sort: [], filters: [] };

/**
 * Creates the rows of a query collection for one presentation. `connectView` calls it for `queryCollection(member)`.
 * A `using` declaration disposes it where the engine has `Symbol.dispose`.
 */
export function createQueryCollection<TRow>(scope: QueryCollectionScope, definition: QueryCollectionDefinition<TRow>): QueryCollection<TRow> & Disposable {
  const listeners = new Set<() => void>();
  const cache = new Map<number, CachedRow<TRow>>();
  let start = 0;
  let count = 0;
  let disposed = false;
  // The request in flight, and the failed span of the latest load.
  let inflight: { readonly start: number; readonly end: number } | undefined;
  let failure: { readonly start: number; readonly end: number; readonly error: BridgeError } | undefined;
  // The total of the newest reply, which can be ahead of the state.
  let replied: { readonly total: number; readonly version: number } | undefined;
  let seenQuery: string | undefined;
  let current: QueryCollectionSnapshot<TRow>;

  const state = () => scope.state();
  const totalOf = (published: QueryCollectionState | undefined): number | null => {
    if (replied !== undefined && (published === undefined || replied.version >= published.version)) return replied.total;
    return published?.total ?? null;
  };
  const endOf = (total: number | null) => total === null ? start + count : Math.min(start + count, total);

  function rowAt(index: number, published: QueryCollectionState | undefined): QueryRow<TRow> {
    const cached = cache.get(index);
    const version = published?.version ?? 0;
    if (cached) return { index, status: "loaded", key: cached.key, row: cached.row, stale: cached.version < version };
    const failed = failure !== undefined && index >= failure.start && index < failure.end;
    return { index, status: failed ? "error" : "loading", key: undefined, row: undefined, stale: false };
  }

  function snapshot(): QueryCollectionSnapshot<TRow> {
    const published = state();
    const total = totalOf(published);
    const end = Math.max(start, endOf(total));
    const rows: QueryRow<TRow>[] = [];
    for (let index = start; index < end; index++) rows.push(rowAt(index, published));
    const covered = failure !== undefined && failure.start < end && failure.end > start;
    return {
      query: published?.query ?? emptyQuery, total, version: published?.version ?? 0, start, count: end - start, rows,
      loading: inflight !== undefined || rows.some(row => row.status === "loading" || row.stale),
      error: covered ? failure!.error : undefined,
    };
  }

  function notify(): void {
    if (disposed) return;
    current = snapshot();
    for (const listener of [...listeners]) listener();
  }

  // Keeps the rows near the range: at least 2,000, and four ranges' worth.
  function trim(): void {
    const keep = Math.max(2_000, count * 4);
    if (cache.size <= keep) return;
    const center = start + count / 2;
    const indexes = [...cache.keys()].sort((left, right) => Math.abs(right - center) - Math.abs(left - center));
    for (const index of indexes.slice(0, cache.size - keep)) cache.delete(index);
  }

  function pump(): void {
    if (disposed || inflight !== undefined) return;
    const published = state();
    if (published === undefined) return;
    const end = endOf(totalOf(published));
    let first = -1;
    let last = -1;
    for (let index = start; index < end; index++) {
      const cached = cache.get(index);
      if (cached && cached.version >= published.version) continue;
      // A failed span waits for retry().
      if (!cached && failure !== undefined && index >= failure.start && index < failure.end) continue;
      if (first < 0) first = index;
      last = index;
    }
    if (first < 0) return;
    const span = { start: first, end: Math.min(last + 1, first + maximumQueryRange) };
    inflight = span;
    void load(span);
  }

  async function load(span: { readonly start: number; readonly end: number }): Promise<void> {
    let error: BridgeError | undefined;
    try {
      const reply = await scope.request({ kind: "query-rows", route: scope.route, member: scope.member, start: span.start, count: span.end - span.start });
      if (!reply.ok) error = replyError(reply.error, scope.route, "The rows could not be loaded.");
      else if (!disposed) {
        const version = reply["version"] as number;
        const total = reply["total"] as number | null;
        const first = reply["start"] as number;
        const keys = reply["keys"] as readonly string[];
        const rows = reply["rows"] as readonly unknown[];
        const decoded = rows.map(row => definition.decode(row));
        // Rows of an older version than the newest reply do not replace newer ones.
        for (let offset = 0; offset < decoded.length; offset++) {
          const existing = cache.get(first + offset);
          if (existing && existing.version > version) continue;
          cache.set(first + offset, { key: keys[offset]!, row: decoded[offset]!, version });
        }
        // Positions past the end of the list have no rows.
        if (decoded.length < span.end - span.start)
          for (let index = first + decoded.length; index < span.end; index++) cache.delete(index);
        // A producer that does not know the total yet still ends the list where its rows end.
        const known = total ?? (decoded.length < span.end - span.start ? first + decoded.length : null);
        if (known !== null && (replied === undefined || version >= replied.version)) replied = { total: known, version };
        if (failure !== undefined && failure.start < span.end && failure.end > span.start) failure = undefined;
        trim();
      }
    } catch (cause) {
      error = cause instanceof BridgeError ? cause
        : new BridgeError("failed", `The rows could not be loaded: ${errorMessage(cause)}`, { cause, route: scope.route });
    }
    inflight = undefined;
    if (disposed) return;
    // A released View or a lost connection is not the range's failure: the state of the next connection loads it again.
    const lost = error !== undefined && (error.kind === "released" || error.kind === "disconnected");
    if (error !== undefined && !lost) failure = { ...span, error };
    notify();
    if (!lost) pump();
  }

  const stopState = scope.onState(() => {
    const published = state();
    if (published === undefined) return;
    // A new query moves every row: what was loaded is no row of it.
    const text = queryText(published.query);
    if (seenQuery !== undefined && text !== seenQuery) {
      cache.clear();
      failure = undefined;
      replied = undefined;
    }
    seenQuery = text;
    notify();
    pump();
  });
  seenQuery = state() === undefined ? undefined : queryText(state()!.query);
  current = snapshot();

  return disposable<QueryCollection<TRow>>({
    get current() { return current; },
    subscribe(listener) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    setRange(nextStart, nextCount) {
      if (!Number.isSafeInteger(nextStart) || nextStart < 0 || !Number.isSafeInteger(nextCount) || nextCount < 0)
        throw new RangeError("A range has a non-negative integer start and count.");
      nextCount = Math.min(nextCount, maximumQueryRange);
      if (disposed || nextStart === start && nextCount === count) return;
      start = nextStart;
      count = nextCount;
      trim();
      notify();
      pump();
    },
    async setQuery(query) {
      const normalized = normalizeQuery(query);
      await scope.setQuery(normalized as unknown as Message);
    },
    async indexOf(key) {
      if (typeof key !== "string" || key.length === 0) throw new TypeError("A key is a nonempty string.");
      const reply = await scope.request({ kind: "query-index", route: scope.route, member: scope.member, key });
      if (!reply.ok) throw replyError(reply.error, scope.route, "The row could not be found.");
      const index = reply["index"] as number;
      return index < 0 ? undefined : index;
    },
    row(index) {
      if (!Number.isSafeInteger(index) || index < 0) throw new RangeError("A row index is a non-negative integer.");
      return rowAt(index, state());
    },
    retry() {
      if (disposed || failure === undefined) return;
      failure = undefined;
      notify();
      pump();
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      stopState();
      listeners.clear();
      cache.clear();
    },
  });
}
