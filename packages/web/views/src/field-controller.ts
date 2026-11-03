/// <reference lib="esnext.disposable" preserve="true" />
import { clock, randomId } from "./clock.js";
import type { FieldBaseline, FieldWriteReceipt } from "./connection.js";
import type { BridgeField, BridgeFieldConstraints, BridgeFieldKind } from "./field.js";
import { FieldError } from "./field-error.js";
import { kindFormatter, kindParser, type FieldTextParser } from "./field-text.js";
import { disposable } from "./lifetime.js";
import { reportBridgeError } from "./transport.js";

export { FieldError, type FieldErrorKind } from "./field-error.js";

/** What a field shows. */
export interface FieldState<TValue> {
  /** The draft while one exists, otherwise the member's value in the View's state. */
  readonly value: TValue;
  /**
   * What a text input shows: the text typed, while it is a draft that does not parse or still means `value` (so
   * `1.` stays while typing `1.5`), otherwise `value` formatted for its kind.
   */
  readonly text: string;
  /**
   * A draft exists: from the first input until the last write settled and the View's state caught up, or after a
   * failed write until `reset()` or the next successful write. Prefer it to disabling the field on the View's busy
   * state, which the field's own write causes.
   */
  readonly editing: boolean;
  /**
   * Why the latest write or text failed, until the next input, `retry()` or `reset()`: a `BridgeError` of the write,
   * or a {@link FieldError}. `undefined` otherwise.
   */
  readonly error: unknown;
  /** The member's validation messages in the View's state, or of the last checked write's receipt; `[]` when valid. */
  readonly errors: readonly string[];
  /** The producer's field while a checked write's `conflict` keeps the draft; `undefined` otherwise. */
  readonly conflict: FieldBaseline<TValue> | undefined;
  /**
   * The member's validation is in progress in the View's state, such as an asynchronous rule that is still checking
   * (`validating(state)` of the definition, `validation-pending/1`); show "checking…". `false` without one.
   */
  readonly validating: boolean;
}

/** The View a field reads and writes: its latest state and the client to write with. */
export interface FieldSource<TState, TClient> {
  readonly state: TState;
  readonly client: TClient;
}

/**
 * What a field controller needs of a member: a generated {@link BridgeField}, or `read` and `write` with any of the
 * other members. Without `kind` the text form is the value itself, as for `text`.
 */
export type FieldDefinition<TClient, TState, TValue> =
  & Pick<BridgeField<TClient, TState, TValue>, "read" | "write">
  & Partial<Omit<BridgeField<TClient, TState, TValue>, "read" | "write" | "kind">>
  & { readonly kind?: BridgeFieldKind };

/** When a field writes what the user typed. */
export type FieldCommit = "input" | "pause";

// `any` by default, so options written without a value type, as before 0.16, fit every field.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export interface FieldOptions<TValue = any> {
  /**
   * `input` (the default) writes each input, in order. `pause` writes the draft `delay` milliseconds after the last
   * input and at once on `commit()`, which the framework bindings call when the element loses focus; text that was
   * not written yet is written when the field is disposed.
   */
  readonly commit?: FieldCommit;
  /** The pause of `commit: "pause"`, in milliseconds. Defaults to 700. */
  readonly delay?: number;
  /**
   * Writes with the member's checked write instead of its setter: the write is checked against the value and version
   * the field showed when the draft started, so a change by another writer meanwhile is a `conflict` instead of
   * being overwritten. Needs `checked` and `baseline` in the definition, as generated descriptors have. Checked writes
   * are one at a time, so `coalesce` is ignored.
   */
  readonly checked?: boolean;
  /**
   * With `true` (the default) at most one write is in flight, and values typed meanwhile coalesce into the latest
   * one, which is written when the write settles. With `false` every value is written at once; setters keep them in
   * order with their edit numbers.
   */
  readonly coalesce?: boolean;
  /**
   * With `true` (the default) a failed, rejected or conflicting write keeps the draft and sets `error`, so the user
   * can correct it. With `false` the field shows the View's value again and sets `error`.
   */
  readonly keepDraftOnError?: boolean;
  /**
   * The locale of the text of `number`, `decimal` and `bigint` fields, as `Intl.NumberFormat` takes it, such as
   * `"de-DE"` or `navigator.languages`: the text shows the locale's decimal separator and digits (`1,5`), and reads
   * them and its group separators (`1.234,5`). Without it the text is the invariant form (`1.5`), which
   * `<input type="number">` needs. Dates and times always use the ISO forms of `<input type="date">`, `"time"` and
   * `"datetime-local"`; pass `format` and `parse` to show them otherwise.
   */
  readonly locale?: string | readonly string[];
  /**
   * The most digits a `number` or `decimal` text may have after the decimal separator; more is an `invalid-text`
   * error. A `decimal` allows at most 28 in any case.
   */
  readonly scale?: number;
  /**
   * Reads the text the user typed into a value, replacing the kind's parser. Throw to reject it: a thrown
   * {@link FieldError} is the field's `error`, any other error becomes an `invalid-text` `FieldError` with its message.
   * `previous` is the value the field shows.
   */
  readonly parse?: (text: string, previous: TValue) => TValue;
  /** Writes a value as the field's text, replacing the kind's formatter. Pair it with `parse`, which must read it back. */
  readonly format?: (value: TValue) => string;
}

/**
 * A framework-neutral binding of one View member to an input (`@runic-artifex/angular`, `react`, `vue` and `svelte`
 * wrap it as `useField` / `injectField`). It holds a draft while writes are in flight, so typing never loses
 * characters; sends nothing during IME composition; reads checked-write receipts; follows the member's validation;
 * and shows the View's value, including any normalization, once the last write settled.
 */
export interface FieldController<TState, TClient, TValue> {
  readonly current: FieldState<TValue>;
  /** The definition's `kind`, `nullable`, `integer` and `choices`, for bindings that wire an element. */
  readonly field: {
    readonly kind: BridgeFieldKind | undefined;
    readonly nullable: boolean;
    readonly integer: boolean;
    readonly choices: readonly TValue[] | undefined;
    /** A `datetime` member that requires an offset (`DateTimeOffset`). */
    readonly offset: boolean;
    /** The member's DataAnnotations, for an element's `required`, `maxlength`, `min`, `max` and `pattern`. */
    readonly constraints: BridgeFieldConstraints | undefined;
  };
  /** The text the field shows for `value` when no draft exists: the `format` option, or the kind's form. */
  format(value: TValue): string;
  /** Calls `listener` after each change of `current`, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /**
   * Sets the View the field reads and writes, or `undefined` while none is connected. Call it with each new state.
   * `setSource` does not notify subscribers; read `current` after calling it.
   */
  setSource(source: FieldSource<TState, TClient> | undefined): void;
  /** The user changed the value. */
  input(value: TValue): void;
  /**
   * The user changed the field's text, such as on an `input` event: parsed for the field's kind and written like
   * `input`, or kept as the draft with a `FieldError` of kind `invalid-text` when it does not parse.
   */
  inputText(text: string): void;
  /** An IME composition started: values are kept as the draft but not written until it ends. */
  compositionStart(): void;
  /** The IME composition ended with `text`, which is then written like `inputText`. */
  compositionEnd(text: string): void;
  /**
   * The user is done with the field, such as when it loses focus: with `commit: "pause"` the draft is written now.
   * The text typed is shown formatted again. Resolves with `current` once no write of the field is in flight or
   * waiting, so a command that depends on the field, such as Save, can run after it and skip a failed write:
   * `if ((await title.commit()).error === undefined) await editor.save()`. It never rejects; a failed write is the
   * state's `error`. A write that waits for `retry()` after a conflict, or for a client, does not delay it.
   */
  commit(): Promise<FieldState<TValue>>;
  /** Writes the draft again after a failed write; after a `conflict`, over the producer's value. */
  retry(): void;
  /** Drops the draft, the error and a conflict and shows the View's value. Writes in flight still complete. */
  reset(): void;
  /** Stops notifying. A value that waits to be written (coalescing, a pause) is still written. */
  dispose(): void;
}

const receiptKinds = new Set(["applied", "committed-with-error", "conflict", "rejected"]);

// A checked write resolves with its receipt; anything else a write resolves with is a success.
function receiptOf(result: unknown): FieldWriteReceipt<unknown> | undefined {
  if (result === null || typeof result !== "object" || !receiptKinds.has((result as { kind?: unknown }).kind as string)) return undefined;
  const receipt = result as Record<string, unknown>;
  const baseline = (value: unknown) => value !== null && typeof value === "object" && typeof (value as { version?: unknown }).version === "number";
  switch (receipt["kind"]) {
    case "applied": return baseline(receipt["snapshot"]) ? result as FieldWriteReceipt<unknown> : undefined;
    case "committed-with-error": return baseline(receipt["snapshot"]) && typeof receipt["message"] === "string" ? result as FieldWriteReceipt<unknown> : undefined;
    case "conflict": return baseline(receipt["incoming"]) && typeof receipt["message"] === "string" ? result as FieldWriteReceipt<unknown> : undefined;
    default: return typeof receipt["message"] === "string" ? result as FieldWriteReceipt<unknown> : undefined;
  }
}

// A generated client exposes its latest state as `snapshot`, which is current as soon as a write settled.
function latestState<TState>(client: unknown): { readonly state: TState } | undefined {
  if (client === null || typeof client !== "object" || typeof (client as { subscribe?: unknown }).subscribe !== "function"
    || !("snapshot" in client)) return undefined;
  return { state: (client as { readonly snapshot: TState }).snapshot };
}

function same(left: unknown, right: unknown): boolean {
  if (Object.is(left, right)) return true;
  if (left === null || right === null || typeof left !== "object" || typeof right !== "object") return false;
  try {
    const json = (value: unknown) => JSON.stringify(value, (_key, item: unknown) => typeof item === "bigint" ? `${item}n` : item);
    return json(left) === json(right);
  } catch { return false; }
}

const noMessages: readonly string[] = Object.freeze([]);

function sameMessages(left: readonly string[], right: readonly string[]): boolean {
  return left === right || left.length === right.length && left.every((message, index) => message === right[index]);
}

function invalid(message: string): FieldError { return new FieldError("invalid-text", message); }

// The page's fields, shared by every copy of this package, so a flush or a window close can write their drafts first.
// The page holds them weakly: a controller that is dropped without dispose() leaves the set once it is collected.
interface LiveField { readonly hasDraft: () => boolean; readonly commit: () => Promise<unknown> }
interface LiveFieldRef { deref(): LiveField | undefined }
const liveFieldsKey = Symbol.for("runic.views.fields/2");
function liveFieldRefs(): Set<LiveFieldRef> {
  const page = globalThis as unknown as Record<symbol, Set<LiveFieldRef> | undefined>;
  return page[liveFieldsKey] ??= new Set();
}
const weakRef = (globalThis as { readonly WeakRef?: new (target: LiveField) => LiveFieldRef }).WeakRef;
function addLiveField(field: LiveField): LiveFieldRef {
  const ref = weakRef === undefined ? { deref: () => field } : new weakRef(field);
  liveFieldRefs().add(ref);
  return ref;
}
function liveFields(): LiveField[] {
  const refs = liveFieldRefs();
  const fields: LiveField[] = [];
  for (const ref of refs) {
    const field = ref.deref();
    if (field === undefined) refs.delete(ref);
    else fields.push(field);
  }
  return fields;
}

/** The number of fields the page holds; for tests. */
export function liveFieldCount(): number {
  return liveFields().length;
}

/**
 * Commits every field of the page that holds a draft, such as a `commit: "pause"` draft not written yet, and resolves
 * once their writes settled. The runtime calls it when the application closes the window (`presented/1`), before it
 * acknowledges the close; call it before a departure that the page does not start itself, such as before a host
 * command that navigates. It never rejects.
 */
export function commitAllFields(): Promise<void> {
  return commitDrafts() ?? Promise.resolve();
}

/** Commits the page's fields that hold a draft, or returns `undefined` when none does. */
export function commitDrafts(): Promise<void> | undefined {
  const drafts = liveFields().filter(field => field.hasDraft());
  if (drafts.length === 0) return undefined;
  return Promise.all(drafts.map(field => field.commit().catch(() => undefined))).then(() => undefined);
}

/**
 * Creates a {@link FieldController} for a generated {@link BridgeField}, such as `EditorFields.title`, or any
 * definition with `read` and `write`. A `using` declaration disposes it where the engine has `Symbol.dispose`.
 *
 * ```ts
 * const title = createFieldController(EditorFields.title, { commit: "pause" });
 * editor.subscribe(state => title.setSource({ state, client: editor }));
 * input.addEventListener("input", () => title.inputText(input.value));
 * input.addEventListener("blur", () => title.commit());
 * ```
 */
export function createFieldController<TState, TClient, TValue>(
  definition: FieldDefinition<TClient, TState, TValue>,
  options: FieldOptions<TValue> = {},
): FieldController<TState, TClient, TValue> & Disposable {
  const checked = options.checked === true;
  if (checked && (typeof definition.checked !== "function" || typeof definition.baseline !== "function"))
    throw new TypeError("A checked field needs the member's checked write and baseline; use a generated field descriptor.");
  const pause = options.commit === "pause";
  if (options.commit !== undefined && options.commit !== "input" && !pause) throw new RangeError("commit must be \"input\" or \"pause\".");
  const delay = options.delay ?? 700;
  if (!Number.isFinite(delay) || delay < 0) throw new RangeError("delay must be a non-negative number of milliseconds.");
  const coalesce = checked || options.coalesce !== false;
  const keepDraftOnError = options.keepDraftOnError !== false;
  const kind = definition.kind;
  const nullable = definition.nullable === true;
  const scale = options.scale;
  if (scale !== undefined && (!Number.isSafeInteger(scale) || scale < 0)) throw new RangeError("scale must be a non-negative integer.");
  const traits = {
    kind, nullable, integer: definition.integer === true, choices: definition.choices, offset: definition.offset === true,
    scale, locale: options.locale,
  };
  const kindParse: FieldTextParser<TValue> = options.parse ?? kindParser<TValue>(traits);
  const parse = (text: string): TValue => {
    try { return kindParse(text, current.value); }
    catch (failure) { throw failure instanceof FieldError ? failure : invalid(failure instanceof Error ? failure.message : String(failure)); }
  };
  const format: (value: TValue) => string = options.format ?? kindFormatter<TValue>(traits);
  const field = Object.freeze({ kind, nullable, integer: traits.integer, choices: definition.choices, offset: traits.offset,
    constraints: definition.constraints });
  const listeners = new Set<() => void>();
  let source: FieldSource<TState, TClient> | undefined;
  let draft: { readonly value: TValue } | undefined;
  // The text last typed (inputText); shown while it does not parse or still means the value.
  let typed: string | undefined;
  let typedInvalid = false;
  let error: unknown;
  let conflict: FieldBaseline<TValue> | undefined;
  let receiptErrors: readonly string[] = noMessages;
  let composing = false;
  let inFlight = 0;
  let lastWrite: Promise<unknown> = Promise.resolve();
  // The value to write next: typed while a write was in flight (coalescing) or while no client was connected.
  let queued: { readonly value: TValue } | undefined;
  // commit: "pause": the draft has not been handed to a write yet.
  let unsent = false;
  let timer: unknown;
  // checked: the field's value and version that the next write is checked against.
  let baseline: FieldBaseline<TValue> | undefined;
  let disposed = false;
  // commit() calls waiting for the field's writes.
  let idleWaiters: ((state: FieldState<TValue>) => void)[] = [];
  const empty = (kind === "text" || kind === undefined ? "" : undefined) as TValue;
  const live: LiveField = {
    // A draft not sent yet: a pause that has not ended, or a value that waits for the write in flight. A write in
    // flight is on its way already, and a text that does not parse has nothing to write.
    hasDraft: () => !composing && (unsent || queued !== undefined),
    commit: () => controller.commit(),
  };
  const liveRef = addLiveField(live);
  let current: FieldState<TValue> = Object.freeze({
    value: empty, text: "", editing: false, error: undefined, errors: noMessages, conflict: undefined, validating: false,
  });

  const viewValue = (): TValue => source === undefined ? empty : definition.read(source.state);
  function textOf(value: TValue): string {
    if (typed === undefined) return format(value);
    if (typedInvalid) return typed;
    try { return same(parse(typed), value) ? typed : format(value); } catch { return format(value); }
  }
  function update(notify = true): void {
    const value = draft === undefined ? viewValue() : draft.value;
    const errors = definition.errors !== undefined
      ? source === undefined ? noMessages : definition.errors(source.state)
      : receiptErrors;
    const validating = definition.validating !== undefined && source !== undefined && definition.validating(source.state) === true;
    const next: FieldState<TValue> = {
      value, text: textOf(value), editing: draft !== undefined || typedInvalid, error,
      errors: sameMessages(errors, current.errors) ? current.errors : errors, conflict, validating,
    };
    if (Object.is(next.value, current.value) && next.text === current.text && next.editing === current.editing
      && Object.is(next.error, current.error) && next.errors === current.errors && next.conflict === current.conflict
      && next.validating === current.validating) return;
    current = Object.freeze(next);
    if (!notify || disposed) return;
    for (const listener of [...listeners]) {
      try { listener(); } catch (failure) { reportBridgeError(failure); }
    }
  }

  function captureBaseline(): void {
    if (!checked || baseline !== undefined || source === undefined) return;
    try { baseline = definition.baseline!(source.client); } catch { /* read again when the write is sent */ }
  }

  function call(client: TClient, value: TValue): Promise<unknown> {
    try {
      if (!checked) return Promise.resolve(definition.write(client, value));
      const base = baseline ?? definition.baseline!(client);
      baseline = base;
      return Promise.resolve(definition.checked!(client, value, { requestId: randomId(), baseline: base }));
    } catch (failure) { return Promise.reject(failure); }
  }

  function write(value: TValue): void {
    inFlight++;
    const pending = call(source!.client, value);
    lastWrite = pending.then(result => settled(value, result, undefined, false), failure => settled(value, undefined, failure, true));
  }

  function pump(): void {
    if (queued === undefined || source === undefined || composing || conflict !== undefined) return;
    if (coalesce && inFlight !== 0) return;
    const { value } = queued;
    queued = undefined;
    write(value);
  }

  function request(value: TValue): void {
    queued = { value };
    unsent = false;
    pump();
  }

  function settled(sent: TValue, result: unknown, failure: unknown, failed: boolean): void {
    inFlight--;
    let keep = false;
    const receipt = failed ? undefined : receiptOf(result);
    // The baseline follows each receipt, also after dispose, so a last write sent then is checked against it.
    if (receipt?.kind === "applied" || receipt?.kind === "committed-with-error") baseline = receipt.snapshot as FieldBaseline<TValue>;
    if (disposed) return;
    if (failed) {
      error = failure;
      keep = keepDraftOnError;
    } else if (receipt === undefined) error = undefined;
    else switch (receipt.kind) {
      case "applied":
        error = undefined;
        receiptErrors = receipt.validation === undefined ? noMessages
          : Object.freeze(receipt.validation.split("\n").filter(message => message.length !== 0));
        break;
      case "committed-with-error":
        error = new FieldError("committed-with-error", receipt.message, receipt.snapshot);
        break;
      case "rejected":
        error = new FieldError("rejected", receipt.message);
        keep = keepDraftOnError;
        break;
      case "conflict":
        if (same(receipt.incoming.value, sent)) {
          // An earlier attempt of this value was applied: the producer already has it.
          baseline = receipt.incoming as FieldBaseline<TValue>;
          error = undefined;
          break;
        }
        error = new FieldError("conflict", receipt.message, receipt.incoming);
        if (keepDraftOnError) {
          conflict = receipt.incoming as FieldBaseline<TValue>;
          keep = true;
        } else baseline = undefined;
        break;
    }
    // Values typed meanwhile are written next, also after a failure: they are newer than the failed one.
    if (queued !== undefined && source !== undefined && !composing && conflict === undefined) {
      pump();
      update();
      resolveIdle();
      return;
    }
    if (inFlight === 0 && !composing && queued === undefined && !unsent && timer === undefined && !typedInvalid
      && draft !== undefined && !keep) {
      // The last write settled: the View's state now includes it, with any normalization the producer applied.
      draft = undefined;
      baseline = undefined;
      const latest = source === undefined ? undefined : latestState<TState>(source.client);
      if (latest !== undefined && source !== undefined) source = { state: latest.state, client: source.client };
    }
    update();
    resolveIdle();
  }

  // Nothing is in flight, and nothing waits that will be written without the user (a conflict) or a client.
  function idle(): boolean {
    return inFlight === 0 && (queued === undefined || conflict !== undefined || source === undefined);
  }
  function resolveIdle(): void {
    if (idleWaiters.length === 0 || !disposed && !idle()) return;
    const waiters = idleWaiters;
    idleWaiters = [];
    for (const resolve of waiters) resolve(current);
  }

  function schedule(): void {
    if (timer !== undefined) clock.clear(timer);
    timer = clock.set(() => {
      timer = undefined;
      if (disposed || composing) return;
      if (unsent && draft !== undefined) request(draft.value);
      update();
    }, delay);
  }

  function cancelTimer(): void {
    if (timer === undefined) return;
    clock.clear(timer);
    timer = undefined;
  }

  function edit(value: TValue): void {
    if (draft === undefined) captureBaseline();
    draft = { value };
    typedInvalid = false;
    if (conflict === undefined) error = undefined;
    // During IME composition the value is only shown; compositionEnd writes it.
    if (composing) return;
    if (pause) { unsent = true; schedule(); }
    else request(value);
  }

  function input(value: TValue): void {
    if (disposed) return;
    typed = undefined;
    edit(value);
    update();
  }

  function inputText(text: string): void {
    if (disposed) return;
    let value: TValue;
    try { value = parse(text); }
    catch (failure) {
      if (draft === undefined) captureBaseline();
      typed = text;
      typedInvalid = true;
      // A pending value is older than this text: write nothing until the text parses.
      cancelTimer();
      unsent = false;
      if (conflict === undefined) error = failure instanceof FieldError ? failure : invalid(String(failure));
      update();
      return;
    }
    typed = text;
    edit(value);
    update();
  }

  const controller: FieldController<TState, TClient, TValue> & Disposable = disposable({
    get current() { return current; },
    field,
    format,
    subscribe(listener: () => void) {
      if (disposed) return () => {};
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    setSource(next: FieldSource<TState, TClient> | undefined) {
      source = next;
      if (disposed) return;
      // A value typed before a client was connected is written once one is.
      if (source !== undefined && queued !== undefined && inFlight === 0 && !composing) pump();
      update(false);
    },
    input,
    inputText,
    compositionStart() { composing = true; cancelTimer(); },
    compositionEnd(text: string) {
      composing = false;
      inputText(text);
    },
    commit() {
      if (disposed) return Promise.resolve(current);
      cancelTimer();
      if (!composing && unsent && draft !== undefined) request(draft.value);
      if (!typedInvalid) typed = undefined;
      update();
      if (idle()) return Promise.resolve(current);
      return new Promise<FieldState<TValue>>(resolve => { idleWaiters.push(resolve); });
    },
    retry() {
      if (disposed || draft === undefined || typedInvalid) return;
      if (conflict !== undefined) {
        baseline = conflict;
        conflict = undefined;
      }
      error = undefined;
      request(draft.value);
      update();
    },
    reset() {
      if (disposed) return;
      cancelTimer();
      draft = undefined;
      typed = undefined;
      typedInvalid = false;
      error = undefined;
      conflict = undefined;
      queued = undefined;
      unsent = false;
      baseline = undefined;
      update();
      resolveIdle();
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      liveFieldRefs().delete(liveRef);
      listeners.clear();
      cancelTimer();
      resolveIdle();
      // The last value the user typed is not lost with the field.
      const last = queued ?? (unsent && !typedInvalid ? draft : undefined);
      queued = undefined;
      if (last === undefined || source === undefined || conflict !== undefined) return;
      const client = source.client;
      const send = () => {
        inFlight++;
        void call(client, last.value).then(result => {
          const receipt = receiptOf(result);
          if (receipt?.kind === "rejected" || receipt?.kind === "conflict" || receipt?.kind === "committed-with-error")
            reportBridgeError(new FieldError(receipt.kind, receipt.message, receipt.kind === "rejected" ? undefined
              : receipt.kind === "conflict" ? receipt.incoming : receipt.snapshot));
        }, failure => reportBridgeError(failure));
      };
      // A checked write needs the receipt of the write in flight for its baseline.
      if (checked && inFlight !== 0) void lastWrite.then(send, send);
      else send();
    },
  });
  return controller;
}
