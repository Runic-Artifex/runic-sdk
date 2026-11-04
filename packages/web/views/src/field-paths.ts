// field-paths/1 (specs/application/README.md, "Field paths"): generated descriptors of fields below a View member, such
// as `InvoiceFields.address.street` and `InvoiceFields.lines.at(key).quantity`. Generated code calls `fieldPath` and
// `rowFields`; applications use the descriptors with `useField`, `injectField` and `createFieldController`.
import { BridgeError } from "./errors.js";
import type { BridgeFieldWriteRequest, FieldBaseline, FieldWriteOptions, FieldWriteReceipt } from "./connection.js";
import { randomId } from "./clock.js";
import type { BridgeField, BridgeFieldConstraints, BridgeFieldKind } from "./field.js";
import type { BridgeValidationState } from "./validation.js";

const noMessages: readonly string[] = Object.freeze([]);

/** A segment of a validation path: a member name or a list index. */
type Segment = string | number;

function startsWith(path: readonly Segment[], prefix: readonly Segment[]): boolean {
  if (path.length < prefix.length) return false;
  for (let index = 0; index < prefix.length; index++) if (path[index] !== prefix[index]) return false;
  return true;
}

const errorCache = new WeakMap<object, Map<string, readonly string[]>>();

/**
 * The validation messages of the value at a validation path and below it, in `validation` order, such as
 * `["lines", 2, "quantity"]` for a cell of the third row (generated code calls it for a field path's `errors`).
 * Returns the same array for the same inputs, so a framework can compare it by identity.
 */
export function fieldErrorsAt(validation: BridgeValidationState | undefined, path: readonly Segment[]): readonly string[] {
  if (validation === undefined || path.length === 0) return noMessages;
  const key = JSON.stringify(path);
  let byPath = errorCache.get(validation);
  const cached = byPath?.get(key);
  if (cached !== undefined) return cached;
  const messages = validation.errors.filter(error => startsWith(error.path, path)).map(error => error.message);
  const result = messages.length === 0 ? noMessages : Object.freeze(messages);
  if (byPath === undefined) errorCache.set(validation, byPath = new Map());
  byPath.set(key, result);
  return result;
}

/**
 * Whether the validation of the value at a validation path, or of a value below it, is in progress: a
 * `validation.pending` path starts with `path` (`validation-pending/1`).
 */
export function fieldValidatingAt(validation: BridgeValidationState | undefined, path: readonly Segment[]): boolean {
  const pending = validation?.pending;
  return pending !== undefined && path.length > 0 && pending.some(item => startsWith(item, path));
}

const indexCache = new WeakMap<readonly unknown[], Map<string, number>>();

/** The index of the row with `key` in `rows`, or -1. */
export function rowIndex<TRow>(rows: readonly TRow[] | undefined, keyOf: (row: TRow) => string, key: string): number {
  if (rows === undefined || rows === null) return -1;
  let indexes = indexCache.get(rows);
  if (indexes === undefined) {
    indexes = new Map();
    rows.forEach((row, index) => indexes!.set(keyOf(row), index));
    indexCache.set(rows, indexes);
  }
  return indexes.get(key) ?? -1;
}

/** What a generated client with field paths offers their descriptors. */
export interface FieldPathClient {
  /** A checked write of a field path below `member` (`field-paths/1`); the values are encoded as their wire types. */
  writePath<T>(member: string, path: readonly string[], write: BridgeFieldWriteRequest, decode: (wire: unknown) => T): Promise<FieldWriteReceipt<T>>;
  /** The version a field path write is based on: the revision of the state the client shows. */
  fieldPathVersion(): number;
}

/** How generated code describes one field path. */
export interface FieldPathSpec<TState, TContainer, TValue> {
  /** The View member the path is below: a settable object member, or a keyed collection. */
  readonly member: string;
  /** The member names below the member, or below the row. */
  readonly path: readonly string[];
  /** A cell's row: its key, how to find the rows in the state and how to read a row's key. */
  readonly row?: {
    readonly key: string;
    readonly rows: (state: TState) => readonly TContainer[] | undefined;
    readonly keyOf: (row: TContainer) => string;
  };
  /** Reads the member's object from the state; for a member's field path. */
  readonly container?: (state: TState) => TContainer | null | undefined;
  /** Reads the value at the path from the member's object or the row. */
  readonly read: (container: TContainer) => TValue;
  readonly encode: (value: TValue) => unknown;
  readonly decode: (wire: unknown) => TValue;
  readonly kind: BridgeFieldKind;
  readonly nullable?: boolean;
  readonly integer?: boolean;
  readonly offset?: boolean;
  readonly constraints?: BridgeFieldConstraints;
  readonly choices?: readonly TValue[];
  /** The View's validation, when it has validation. */
  readonly validation?: (state: TState) => BridgeValidationState | undefined;
}

/**
 * A descriptor of one field path. It has no setter: every write is a checked write (`checkedOnly`), based on the
 * route's revision when the draft started.
 */
export function fieldPath<TClient extends FieldPathClient, TState, TValue, TContainer = never>(
  spec: FieldPathSpec<TState, TContainer, TValue>,
): BridgeField<TClient, TState, TValue> {
  const { member, path, row } = spec;
  const segments = row === undefined ? path : [row.key, ...path];
  const containerOf = (state: TState): TContainer | undefined => {
    if (row === undefined) return spec.container?.(state) ?? undefined;
    const rows = row.rows(state);
    const index = rowIndex(rows, row.keyOf, row.key);
    return index < 0 ? undefined : rows![index];
  };
  const read = (state: TState): TValue => {
    const container = containerOf(state);
    return (container === undefined || container === null ? undefined : spec.read(container)) as TValue;
  };
  const validationPath = (state: TState): readonly Segment[] | undefined => {
    if (row === undefined) return [member, ...path];
    const index = rowIndex(row.rows(state), row.keyOf, row.key);
    return index < 0 ? undefined : [member, index, ...path];
  };
  const checked = (client: TClient, value: TValue, options: FieldWriteOptions<TValue>): Promise<FieldWriteReceipt<TValue>> =>
    client.writePath<TValue>(member, segments, {
      requestId: options.requestId, expectedVersion: options.baseline.version,
      expectedValue: spec.encode(options.baseline.value), value: spec.encode(value),
    }, spec.decode);
  const baseline = (client: TClient): FieldBaseline<TValue> => {
    const state = (client as unknown as { readonly snapshot: TState }).snapshot;
    if (containerOf(state) === undefined)
      throw new BridgeError("failed", row === undefined ? `${member} is null.` : `${member} has no row ${row.key}.`);
    return { value: read(state), version: client.fieldPathVersion() };
  };
  const field: BridgeField<TClient, TState, TValue> = {
    name: [member, ...segments].join("."),
    kind: spec.kind,
    ...spec.nullable === true ? { nullable: true } : {},
    ...spec.integer === true ? { integer: true } : {},
    ...spec.offset === true ? { offset: true } : {},
    ...spec.constraints === undefined ? {} : { constraints: spec.constraints },
    ...spec.choices === undefined ? {} : { choices: spec.choices },
    checkedOnly: true,
    read,
    write: (client, value) => checked(client, value, { requestId: randomId(), baseline: baseline(client) }),
    checked,
    baseline,
    ...spec.validation === undefined ? {} : {
      errors: (state: TState) => {
        const at = validationPath(state);
        return at === undefined ? noMessages : fieldErrorsAt(spec.validation!(state), at);
      },
      validating: (state: TState) => {
        const at = validationPath(state);
        return at !== undefined && fieldValidatingAt(spec.validation!(state), at);
      },
    },
  };
  return Object.freeze(field);
}

/**
 * The cells of a keyed collection's rows, such as `InvoiceFields.lines`: `at(key)` returns the descriptors of one row's
 * cells, the same object for the same key, so a framework binding keeps its field across renders.
 */
export interface BridgeRowFields<TCells> {
  /** The collection's wire name. */
  readonly name: string;
  /** The descriptors of the cells of the row with `key` (a string, or the number of an integer key). */
  at(key: string | number | bigint): TCells;
}

/** Generated code: the cells of a keyed collection, created per row key on first use. */
export function rowFields<TCells extends object>(name: string, create: (key: string) => TCells): BridgeRowFields<TCells> {
  // Held weakly: a row's cells stay the same object while a binding holds them, and removed rows' cells are collected.
  const rows = new Map<string, WeakRef<TCells>>();
  const finalizer = new FinalizationRegistry<{ readonly key: string; readonly reference: WeakRef<TCells> }>(entry => {
    if (rows.get(entry.key) === entry.reference) rows.delete(entry.key);
  });
  return Object.freeze({
    name,
    at(key: string | number | bigint): TCells {
      const text = String(key);
      let cells = rows.get(text)?.deref();
      if (cells !== undefined) return cells;
      cells = Object.freeze(create(text));
      const reference = new WeakRef(cells);
      rows.set(text, reference);
      finalizer.register(cells, { key: text, reference });
      return cells;
    },
  });
}
