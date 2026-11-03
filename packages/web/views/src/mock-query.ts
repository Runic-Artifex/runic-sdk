// The mock's row source of a query collection (query-collections/1): wire rows that it filters, sorts and serves by
// range, as a .NET RunicQueryableRowSource does.
import { normalizeQuery, type QueryCollectionQuery, type RunicQuery } from "./query.js";

/** The rows of a mock query collection, in wire form, and how the mock serves them. */
export interface MockQuerySource {
  /** The rows in the source's own order, encoded as the client's wire rows. */
  readonly rows: readonly unknown[];
  /** The wire key of a row. */
  readonly key: (row: unknown) => string;
  /**
   * Milliseconds each `query-rows` and `query-index` request takes, like a database: virtual time with `manual`
   * scheduling. Defaults to 0.
   */
  readonly latency?: number;
  /** Custom filters by name: whether a row passes with the filter's `value`. A query with another custom filter is rejected. */
  readonly custom?: Readonly<Record<string, (row: unknown, value: unknown) => boolean>>;
  /** The query the collection starts with. */
  readonly query?: RunicQuery;
}

/** One `query-rows` request the mock served. */
export interface MockQueryLoad {
  readonly start: number;
  readonly count: number;
  /** The collection's version when it was served. */
  readonly version: number;
}

/** A query collection of a mock View, from `view.query(member)`. */
export interface MockQueryCollection {
  readonly query: QueryCollectionQuery;
  readonly version: number;
  /** The rows that match the query. */
  readonly total: number;
  /** The rows in the source's order. */
  readonly rows: readonly unknown[];
  /** Every `query-rows` request served, oldest first. */
  readonly loads: readonly MockQueryLoad[];
  /** The rows in the query's order. */
  view(): readonly unknown[];
  /** Replaces the rows and raises the version, as .NET's `Refresh()` after rows were added or removed. */
  setRows(rows: readonly unknown[]): void;
  /** Raises the version, as .NET's `Invalidate(key)` after rows changed in place. */
  invalidate(): void;
}

const isRecord = (value: unknown): value is Readonly<Record<string, unknown>> =>
  value !== null && typeof value === "object" && !Array.isArray(value);
const fieldOf = (row: unknown, field: string) => isRecord(row) ? row[field] : undefined;

// Numbers numerically, int64 decimal strings as integers, other strings by UTF-16 code units, booleans false first;
// null and missing values first.
function compareValues(left: unknown, right: unknown): number {
  if (left === right) return 0;
  if (left === null || left === undefined) return -1;
  if (right === null || right === undefined) return 1;
  if (typeof left === "number" && typeof right === "number") return left - right;
  if (typeof left === "boolean" && typeof right === "boolean") return left ? 1 : -1;
  const integer = /^-?\d+$/u;
  if (typeof left === "string" && typeof right === "string" && integer.test(left) && integer.test(right)) {
    const difference = BigInt(left) - BigInt(right);
    return difference < 0n ? -1 : difference > 0n ? 1 : 0;
  }
  const a = String(left);
  const b = String(right);
  return a < b ? -1 : a > b ? 1 : 0;
}

const json = (value: unknown) => JSON.stringify(value);

/**
 * The rows of `source` that match `query`, in its order, then by key. Throws a `RangeError` for a custom filter the
 * source does not define.
 */
export function evaluateMockQuery(rows: readonly unknown[], query: RunicQuery, source: Pick<MockQuerySource, "key" | "custom">): unknown[] {
  const normalized = normalizeQuery(query);
  let result = rows.filter(row => normalized.filters.every(filter => {
    const value = fieldOf(row, filter.field);
    switch (filter.op) {
      case "equals": return json(value) === json(filter.value);
      case "contains": return typeof value === "string" && value.toLowerCase().includes(filter.value.toLowerCase());
      case "range":
        return value !== null && value !== undefined
          && (filter.min === undefined || compareValues(value, filter.min) >= 0)
          && (filter.max === undefined || compareValues(value, filter.max) <= 0);
      case "custom": {
        const custom = source.custom?.[filter.field];
        if (!custom) throw new RangeError(`The mock query collection has no custom filter '${filter.field}'.`);
        return custom(row, filter.value);
      }
    }
  }));
  const keys = new Map(result.map(row => [row, source.key(row)]));
  result = result.sort((left, right) => {
    for (const sort of normalized.sort) {
      const order = compareValues(fieldOf(left, sort.field), fieldOf(right, sort.field));
      if (order !== 0) return sort.descending ? -order : order;
    }
    return compareValues(keys.get(left), keys.get(right));
  });
  return result;
}
