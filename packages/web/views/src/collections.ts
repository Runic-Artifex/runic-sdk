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
      keys(value, found, field);
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
      const rows = owned ? current as unknown[] : [...current];
      applyIndexed(rows, change, found);
      return rows;
    },
  };
}

function keys(items: readonly unknown[], definition: BridgeCollectionDefinition, field: string): string[] {
  const result = items.map(item => {
    if (item === null || item === undefined) throw new Error(`The collection ${field} has a missing row.`);
    return definition.key(item);
  });
  if (result.some(key => typeof key !== "string" || key.length === 0) || new Set(result).size !== result.length)
    throw new Error(`The keys of ${field} must be nonempty and unique.`);
  return result;
}

const index = (value: unknown): number => {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) throw new Error("Invalid collection index.");
  return value;
};

/** Applies one `add`, `remove`, `replace` or `move` change to `rows` in place; unchanged rows keep their identity. */
function applyIndexed(rows: unknown[], change: Readonly<Record<string, unknown>>, definition: BridgeCollectionDefinition): void {
  const at = index(change["index"]);
  const oldKeys = change["keys"] as readonly string[];
  const values = (change["items"] as readonly unknown[]).map(definition.decode);
  const count = oldKeys.length;
  const verify = (start: number) => {
    if (start + count > rows.length || oldKeys.some((key, offset) => definition.key(rows[start + offset]) !== key))
      throw new Error("The collection no longer has the rows this change names.");
  };
  switch (change["kind"]) {
    case "add":
      if (at > rows.length) throw new Error("The insertion point is outside the collection.");
      if (values.some((value, offset) => definition.key(value) !== oldKeys[offset])) throw new Error("The added rows do not have the given keys.");
      rows.splice(at, 0, ...values);
      break;
    case "remove":
      verify(at);
      rows.splice(at, count);
      break;
    case "replace":
      verify(at);
      rows.splice(at, count, ...values);
      break;
    case "move": {
      const from = index(change["oldIndex"]);
      verify(from);
      const moved = rows.splice(from, count);
      if (at > rows.length) throw new Error("The move destination is outside the collection.");
      rows.splice(at, 0, ...moved);
      break;
    }
    default: throw new Error(`Unknown collection change ${String(change["kind"])}.`);
  }
}
