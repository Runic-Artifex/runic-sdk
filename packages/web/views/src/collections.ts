// Keyed collections and append-only text fields (specs/application/state-changes.md). A generated client passes
// `defineCollections(…)` to `connectView` only when its View has them, so other Views do not bundle this code.
import { applyText } from "./text.js";

/** Generated row codec and key of a keyed collection, or an append-only text field (`defineText`). */
export interface BridgeCollectionDefinition {
  /** Decodes one row; throws for an invalid one. */
  readonly decode: (wire: unknown) => unknown;
  /** The row's key: the string, the normal form of a `uuid`, or the decimal form of an `int32` key field. */
  readonly key: (item: unknown) => string;
  /** An append-only text field (`defineText`) rather than a keyed collection. */
  readonly text?: { readonly nullable: boolean };
}

/** Keeps collection codecs typed in generated clients. */
export function defineCollection<T>(decode: (wire: unknown) => T, key: (item: T) => string): BridgeCollectionDefinition {
  return { decode, key: item => key(item as T) };
}

/**
 * An append-only text field, such as a C# `[RunicAppendOnlyText]` string: its frames drop
 * characters from its start and append text, instead of resending it.
 */
export function defineText(nullable = false): BridgeCollectionDefinition {
  return { decode: wire => wire, key: () => { throw new Error("A text field has no keys."); }, text: { nullable } };
}

/**
 * The keyed collections and text fields of one generated client, passed to `connectView` as `collections`. Its
 * members are internal to the runtime.
 */
export interface BridgeCollections {
  /** Whether `field` is a keyed collection or a text field. */
  kind(field: string): "collection" | "text" | undefined;
  /** Throws when a decoded value of `field` is not a valid collection (rows with nonempty, unique keys) or text. */
  validate(field: string, value: unknown): void;
  /**
   * Applies one indexed or text change to `current`, the field's value as the previous change of the frame left it.
   * `owned` says that `current` is already a copy the frame made, which this may change in place.
   */
  apply(field: string, current: unknown, change: Readonly<Record<string, unknown>>, owned: boolean): unknown;
}

/** Binds the collection codecs of a generated client to the code that applies their changes. */
export function defineCollections(definitions: Readonly<Record<string, BridgeCollectionDefinition>>): BridgeCollections {
  const definition = (field: string) => Object.hasOwn(definitions, field) ? definitions[field] : undefined;
  // The keys of each collection value the client holds, all nonempty and unique. A frame's changes keep the index
  // of the value they change, so a frame checks only the rows it names instead of every key. A value whose index
  // was lost, such as the previous value of a frame that failed, gets a new one when it is used again.
  const indexes = new WeakMap<readonly unknown[], Set<string>>();
  const indexOf = (rows: readonly unknown[], found: BridgeCollectionDefinition, field: string): Set<string> => {
    const known = indexes.get(rows);
    if (known !== undefined && known.size === rows.length) return known;
    const built = new Set<string>();
    for (const row of rows) addKey(built, keyOf(row, found, field), field);
    indexes.set(rows, built);
    return built;
  };
  return {
    kind: field => {
      const found = definition(field);
      return found === undefined ? undefined : found.text ? "text" : "collection";
    },
    validate(field, value) {
      const found = definition(field);
      if (!found) return;
      if (found.text) {
        if (typeof value !== "string" && !(found.text.nullable && value === null)) throw new Error(`The text field ${field} is not a string.`);
        return;
      }
      if (!Array.isArray(value)) throw new Error(`The collection ${field} is not an array.`);
      indexOf(value, found, field);
    },
    apply(field, current, change, owned) {
      const found = definition(field);
      if (!found) throw new Error(`${field} is not a keyed collection or text field.`);
      if (found.text) {
        if (change["kind"] !== "text") throw new Error(`The text field ${field} received a ${String(change["kind"])} change.`);
        return applyText(current, change);
      }
      if (change["kind"] === "text") throw new Error(`The collection ${field} received a text change.`);
      if (!Array.isArray(current)) throw new Error(`The collection ${field} is not an array.`);
      // The index moves to the changed value; the value it leaves gets a new one if it is used again.
      const keys = indexOf(current, found, field);
      indexes.delete(current);
      const rows = owned ? current as unknown[] : current.slice();
      try { applyIndexed(rows, change, found, field, keys); }
      catch (error) { indexes.delete(rows); throw error; }
      indexes.set(rows, keys);
      return rows;
    },
  };
}

function keyOf(row: unknown, definition: BridgeCollectionDefinition, field: string): string {
  if (row === null || row === undefined) throw new Error(`The collection ${field} has a missing row.`);
  return definition.key(row);
}

function addKey(keys: Set<string>, key: string, field: string): void {
  if (typeof key !== "string" || key.length === 0 || keys.has(key)) throw new Error(`The keys of ${field} must be nonempty and unique.`);
  keys.add(key);
}

const index = (value: unknown): number => {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) throw new Error("Invalid collection index.");
  return value;
};

// Rows passed as the arguments of one call. Engines limit the number of arguments: V8 throws a RangeError for about
// 120,000, so larger inserts go in parts of this size.
const chunk = 8_192;

/** Inserts `values` at `at` in place, in parts small enough for every engine's argument limit. */
export function insertRows(rows: unknown[], at: number, values: readonly unknown[]): void {
  if (values.length <= chunk) { rows.splice(at, 0, ...values); return; }
  const tail = rows.splice(at);
  for (let start = 0; start < values.length; start += chunk) rows.push(...values.slice(start, start + chunk));
  for (let start = 0; start < tail.length; start += chunk) rows.push(...tail.slice(start, start + chunk));
}

/**
 * Applies one `add`, `remove`, `replace` or `move` change to `rows` in place and updates `keys`, the index of `rows`;
 * unchanged rows keep their identity. Throws for a change that does not fit, which can leave `rows` and `keys` changed.
 */
function applyIndexed(rows: unknown[], change: Readonly<Record<string, unknown>>, definition: BridgeCollectionDefinition,
  field: string, keys: Set<string>): void {
  const at = index(change["index"]);
  const oldKeys = change["keys"] as readonly string[];
  const values = (change["items"] as readonly unknown[]).map(definition.decode);
  const count = oldKeys.length;
  const verify = (start: number) => {
    if (start + count > rows.length || oldKeys.some((key, offset) => definition.key(rows[start + offset]) !== key))
      throw new Error("The collection no longer has the rows this change names.");
  };
  const addAll = () => { for (const value of values) addKey(keys, keyOf(value, definition, field), field); };
  switch (change["kind"]) {
    case "add":
      if (at > rows.length) throw new Error("The insertion point is outside the collection.");
      if (values.length !== count || values.some((value, offset) => keyOf(value, definition, field) !== oldKeys[offset]))
        throw new Error("The added rows do not have the given keys.");
      addAll();
      insertRows(rows, at, values);
      break;
    case "remove":
      verify(at);
      for (const key of oldKeys) keys.delete(key);
      rows.splice(at, count);
      break;
    case "replace":
      verify(at);
      for (const key of oldKeys) keys.delete(key);
      addAll();
      if (values.length <= chunk) rows.splice(at, count, ...values);
      else { rows.splice(at, count); insertRows(rows, at, values); }
      break;
    case "move": {
      const from = index(change["oldIndex"]);
      verify(from);
      const moved = rows.splice(from, count);
      if (at > rows.length) throw new Error("The move destination is outside the collection.");
      insertRows(rows, at, moved);
      break;
    }
    default: throw new Error(`Unknown collection change ${String(change["kind"])}.`);
  }
}
