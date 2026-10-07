/** Generated item codecs and stable keys for an incremental state collection. */
export interface BridgeCollectionDefinition {
  readonly decode: (wire: unknown) => unknown;
  readonly key: (item: unknown) => string;
}

/** Keeps collection codecs typed in generated clients. */
export function defineCollection<T>(decode: (wire: unknown) => T, key: (item: T) => string): BridgeCollectionDefinition {
  return { decode, key: item => key(item as T) };
}

/**
 * The keyed collections of one generated client, passed to `connectView` as
 * `collections`. A View without keyed collections does not bundle this code.
 */
export interface BridgeCollections {
  /** Throws when a decoded state has a missing collection or invalid keys. */
  validate(state: unknown): void;
  /** Applies a collection change frame to a decoded state. Throws for an unusable frame. */
  apply(state: unknown, changes: unknown): unknown;
  /** Applies a frame that `apply` accepted to the wire state. */
  applyWire(wire: unknown, changes: readonly unknown[]): Record<string, unknown>;
}

/** Binds the collection codecs of a generated client to the code that applies their changes. */
export function defineCollections(definitions: Readonly<Record<string, BridgeCollectionDefinition>>): BridgeCollections {
  return {
    validate: state => validateCollections(state, definitions),
    apply: (state, changes) => applyCollectionDelta(state, changes, definitions),
    applyWire: applyWireDelta,
  };
}

function object(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error("Invalid collection change.");
  return value as Record<string, unknown>;
}

function index(value: unknown): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) throw new Error("Invalid collection index.");
  return value;
}

function keys(items: readonly unknown[], definition: BridgeCollectionDefinition): string[] {
  const result = items.map(definition.key);
  if (result.some(key => typeof key !== "string" || key.length === 0) || new Set(result).size !== result.length)
    throw new Error("Collection keys must be nonempty and unique.");
  return result;
}

export function validateCollections(state: unknown, definitions: Readonly<Record<string, BridgeCollectionDefinition>>): void {
  if (Object.keys(definitions).length === 0) return;
  const values = object(state);
  for (const [field, definition] of Object.entries(definitions)) {
    if (!Array.isArray(values[field])) throw new Error("Invalid collection state.");
    keys(values[field], definition);
  }
}

/** Applies a complete frame transactionally, preserving every unchanged item reference. */
export function applyCollectionDelta(state: unknown, changes: unknown,
  definitions: Readonly<Record<string, BridgeCollectionDefinition>>): unknown {
  if (!Array.isArray(changes) || changes.length === 0 || changes.length > 4096) throw new Error("Invalid collection frame.");
  const next = { ...object(state) };
  const edited = new Set<string>();
  for (const raw of changes) {
    const change = object(raw);
    const field = change.field;
    if (typeof field !== "string" || !Object.hasOwn(definitions, field)) throw new Error("Unknown collection field.");
    const definition = definitions[field]!;
    if (!Array.isArray(next[field])) throw new Error("Invalid collection state.");
    if (!edited.has(field)) { next[field] = [...next[field]]; edited.add(field); }
    const items = next[field] as unknown[];
    const at = index(change.index);
    if (at > items.length || !Array.isArray(change.keys) || !Array.isArray(change.items)) throw new Error("Invalid collection change.");
    const oldKeys = change.keys;
    if (oldKeys.length === 0 || oldKeys.some(key => typeof key !== "string" || key.length === 0)) throw new Error("Invalid collection keys.");
    const values = change.items.map(definition.decode);
    const count = oldKeys.length;
    const verify = (start: number) => {
      if (start + count > items.length || oldKeys.some((key, offset) => definition.key(items[start + offset]) !== key))
        throw new Error("The collection baseline no longer matches.");
    };
    switch (change.kind) {
      case "add":
        if (values.length !== count || values.some((value, offset) => definition.key(value) !== oldKeys[offset])) throw new Error("Invalid added rows.");
        items.splice(at, 0, ...values);
        break;
      case "remove":
        if (values.length !== 0) throw new Error("Invalid removed rows.");
        verify(at); items.splice(at, count);
        break;
      case "replace":
        if (values.length !== count || index(change.oldIndex) !== at) throw new Error("Invalid replaced rows.");
        verify(at); items.splice(at, count, ...values);
        break;
      case "move": {
        if (values.length !== 0) throw new Error("Invalid moved rows.");
        const from = index(change.oldIndex);
        verify(from);
        const moved = items.splice(from, count);
        if (at > items.length) throw new Error("Invalid move destination.");
        items.splice(at, 0, ...moved);
        break;
      }
      default: throw new Error("Unknown collection operation.");
    }
  }
  for (const field of edited) keys(next[field] as readonly unknown[], definitions[field]!);
  return next;
}

/**
 * Applies a frame that `applyCollectionDelta` accepted to the wire state, so the
 * wire stays comparable with a later full state at the same revision.
 */
export function applyWireDelta(wire: unknown, changes: readonly unknown[]): Record<string, unknown> {
  const next = { ...(wire as Record<string, unknown>) };
  const edited = new Set<string>();
  for (const change of changes as readonly { field: string; kind: string; index: number; oldIndex: number; keys: unknown[]; items: unknown[] }[]) {
    if (!edited.has(change.field)) { next[change.field] = [...(next[change.field] as unknown[])]; edited.add(change.field); }
    const rows = next[change.field] as unknown[];
    const count = change.keys.length;
    if (change.kind === "add") rows.splice(change.index, 0, ...change.items);
    else if (change.kind === "remove") rows.splice(change.index, count);
    else if (change.kind === "replace") rows.splice(change.index, count, ...change.items);
    else rows.splice(change.index, 0, ...rows.splice(change.oldIndex, count));
  }
  return next;
}
