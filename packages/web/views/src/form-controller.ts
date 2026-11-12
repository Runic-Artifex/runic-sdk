/// <reference lib="esnext.disposable" preserve="true" />
// Forms (C2): one dirty, pending and valid signal over a View's fields, and a submit that commits the fields first.
import { clock } from "./clock.js";
import { FieldError, type FieldState } from "./field-controller.js";
import { disposable } from "./lifetime.js";
import { reportBridgeError } from "./transport.js";
import { currentSlots } from "./scope.js";

/** The state of a form: what its fields show, combined. */
export interface FormState {
  /**
   * The form has changes that are not saved: the `dirty` option reports them (such as the View's `isDirty`, which a
   * .NET `RunicEditSession` computes), or a field has a draft that is not written to the View yet.
   */
  readonly dirty: boolean;
  /** A field has a draft: an input whose write is waiting, in flight, failed or does not parse. */
  readonly editing: boolean;
  /** A field's validation is in progress (`validating`, `validation-pending/1`). */
  readonly validating: boolean;
  /** `editing` or `validating`, or a `submit` runs: the form is not settled. */
  readonly pending: boolean;
  /** No field has validation messages (`errors`) or a failed write or text (`error`). */
  readonly valid: boolean;
  /** A `submit` runs. */
  readonly submitting: boolean;
}

/** What a form needs of a field: a field controller, or a framework binding's field handle. */
export interface FormField {
  commit(): Promise<FieldState<unknown>>;
  reset(): void;
  /** Writes a draft again after a failed write; `submit` calls it for a draft that a transport failure kept. */
  retry?(): void;
}

/** What `submit` reads of a field's state now. */
export type FormFieldState = Pick<FieldState<unknown>, "editing" | "error" | "validating">;

export interface FormSubmitOptions {
  /**
   * Waits until no field's validation is in progress (`validating`) before the command runs: `true` for up to 10
   * seconds, or a number of milliseconds. When it is still in progress then, the command does not run and `submit`
   * resolves `{ status: "validating" }`. By default the command runs at once; .NET validates in any case.
   */
  readonly waitForValidation?: boolean | number;
}

/** The fields of a form: a list, or a record such as `{ title, body }`. */
export type FormFields<TField> = readonly TField[] | Readonly<Record<string, TField>>;

/**
 * How a `submit` ended:
 *
 * - `submitted`: the fields' writes settled and the command ran; `result` is what it returned;
 * - `field-failed`: a field's value did not reach the View, so the command did not run; `error` is the first such
 *   field's `error`: a text that does not parse (`invalid-text`), a `rejected` or `conflict` checked write, or a
 *   transport failure of this submit's writes. A `committed-with-error` write reached the View and does not block;
 * - `validating`: with `waitForValidation`, a field's validation was still in progress when the wait ended;
 * - `busy`: another `submit` of the form was running, so neither the fields nor the command were touched.
 *
 * A command that throws rejects the `submit` promise with its error.
 */
export type FormSubmitResult<T> =
  | { readonly status: "submitted"; readonly result: T }
  | { readonly status: "field-failed"; readonly error: unknown }
  | { readonly status: "validating" }
  | { readonly status: "busy" };

/** Combines field states into a {@link FormState}. The framework bindings' `useForm` and `injectForm` use it. */
export function formStateOf(fields: readonly Pick<FieldState<unknown>, "editing" | "validating" | "errors" | "error">[],
  dirty = false, submitting = false): FormState {
  let editing = false;
  let validating = false;
  let valid = true;
  for (const field of fields) {
    editing ||= field.editing;
    validating ||= field.validating === true;
    if (field.errors.length > 0 || field.error !== undefined) valid = false;
  }
  return Object.freeze({ dirty: dirty || editing, editing, validating, pending: editing || validating || submitting, valid, submitting });
}

/** The fields of a list or record, in order. */
export function formFieldList<TField>(fields: FormFields<TField>): readonly TField[] {
  return Array.isArray(fields) ? fields as readonly TField[] : Object.values(fields as Readonly<Record<string, TField>>);
}

const blockingKinds = new Set(["invalid-text", "rejected", "conflict"]);
const validationWait = 10_000;
const validationPoll = 50;

/**
 * Commits every field, waits for their writes and, unless a value did not reach the View, runs `command`. A draft
 * that a transport failure kept is written again first (`retry`). `read(index)` is the state of a field now; the
 * framework bindings' forms use it, and `busy` is theirs to report.
 */
export async function submitFields<T>(fields: readonly FormField[], command: () => T | PromiseLike<T>,
  options: FormSubmitOptions = {}, read?: (index: number) => FormFieldState): Promise<FormSubmitResult<T>> {
  const before = fields.map((_field, index) => read?.(index));
  const retried = fields.map((field, index) => {
    const state = before[index];
    // A draft kept after a failed write that was not the producer's answer (a transport failure) is written again.
    if (state === undefined || !state.editing || state.error === undefined || state.error instanceof FieldError || field.retry === undefined)
      return false;
    field.retry();
    return true;
  });
  const states = await Promise.all(fields.map(field => field.commit()));
  const failed = states.findIndex((state, index) => {
    const error = state.error;
    if (error === undefined) return false;
    if (error instanceof FieldError) return blockingKinds.has(error.kind);
    // A transport failure blocks when this submit's writes failed, not an earlier failure that left no draft.
    return read === undefined || retried[index] || !Object.is(error, before[index]?.error);
  });
  if (failed >= 0) return { status: "field-failed", error: states[failed]!.error };
  const wait = options.waitForValidation === true ? validationWait : options.waitForValidation === false ? 0 : options.waitForValidation ?? 0;
  if (wait > 0 && read !== undefined) {
    const validating = () => fields.some((_field, index) => read(index).validating === true);
    const deadline = clock.now() + wait;
    while (validating()) {
      if (clock.now() >= deadline) return { status: "validating" };
      await new Promise<void>(resolve => { clock.set(resolve, validationPoll); });
    }
  }
  return { status: "submitted", result: await command() };
}

export interface FormOptions {
  /**
   * Whether the View reports unsaved changes, read whenever the form's state is read, such as
   * `() => editor.snapshot.isDirty`. A field's draft makes the form dirty in any case.
   */
  readonly dirty?: () => boolean;
}

/**
 * One state over a View's fields (`createFieldController`): whether the form is dirty, settled and valid, and a
 * `submit` that writes every field before its command, so Save never runs before the last keystroke reached .NET.
 */
export interface FormController {
  /** The form's state now. Read it again after the View's state changed: fields learn of it without notifying. */
  readonly current: FormState;
  /** Calls `listener` after a field's state changed or a `submit` started or ended, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /** Commits every field (a `commit: "pause"` draft is written now) and resolves once their writes settled. */
  commit(): Promise<FormState>;
  /**
   * Commits every field, waits for their writes and runs `command` unless a field's write or text failed:
   * `await form.submit(() => editor.save())`. While it runs `submitting` is true and another `submit` is `busy`.
   */
  submit<T>(command: () => T | PromiseLike<T>, options?: FormSubmitOptions): Promise<FormSubmitResult<T>>;
  /** Resets every field: drops drafts, errors and conflicts and shows the View's values. */
  reset(): void;
  /** Stops notifying. The fields stay; their owner disposes them. */
  dispose(): void;
}

/** A field controller as a form reads it. */
export type FormFieldController = FormField & {
  readonly current: FieldState<unknown>;
  subscribe(listener: () => void): () => void;
  retry(): void;
};

/**
 * Creates a {@link FormController} over field controllers, such as `createFieldController` results:
 *
 * ```ts
 * const title = createFieldController(EditorFields.title, { commit: "pause" });
 * const body = createFieldController(EditorFields.body, { commit: "pause" });
 * const form = createFormController({ title, body }, { dirty: () => editor.snapshot.isDirty });
 * save.onclick = () => form.submit(() => editor.save());
 * ```
 *
 * Framework pages use `useForm` (React, Vue, Svelte) or `injectForm` (Angular) over their field handles instead.
 */
export function createFormController(fields: FormFields<FormFieldController>, options: FormOptions = {}): FormController & Disposable {
  const list = formFieldList(fields);
  const listeners = new Set<() => void>();
  const slots = currentSlots();
  let submitting = false;
  let disposed = false;
  let last: FormState | undefined;
  const read = (): FormState => {
    const next = formStateOf(list.map(field => field.current), options.dirty?.() === true, submitting);
    // The same object while nothing changed, so a framework can compare it by identity.
    if (last !== undefined && last.dirty === next.dirty && last.editing === next.editing && last.validating === next.validating
      && last.valid === next.valid && last.submitting === next.submitting) return last;
    return last = next;
  };
  const notify = () => {
    if (disposed) return;
    for (const listener of [...listeners]) {
      try { listener(); } catch (failure) { reportBridgeError(slots, failure); }
    }
  };
  const unsubscribe = list.map(field => field.subscribe(notify));
  return disposable<FormController>({
    get current() { return read(); },
    subscribe(listener) {
      if (disposed) return () => {};
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    async commit() {
      await Promise.all(list.map(field => field.commit()));
      return read();
    },
    async submit<T>(command: () => T | PromiseLike<T>, submitOptions: FormSubmitOptions = {}): Promise<FormSubmitResult<T>> {
      if (submitting) return { status: "busy" };
      submitting = true;
      notify();
      try { return await submitFields(list, command, submitOptions, index => list[index]!.current); }
      finally {
        submitting = false;
        notify();
      }
    },
    reset() { for (const field of list) field.reset(); },
    dispose() {
      if (disposed) return;
      disposed = true;
      listeners.clear();
      for (const stop of unsubscribe) stop();
    },
  });
}
