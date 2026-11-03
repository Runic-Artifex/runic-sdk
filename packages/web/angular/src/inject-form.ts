import { computed, signal, type Signal } from "@angular/core";
import {
  formFieldList,
  formStateOf,
  submitFields,
  type FormField,
  type FormFields,
  type FormState,
  type FormSubmitResult,
} from "@runic-artifex/views";

/** A field as {@link injectForm} reads it: an `injectField` handle. */
export interface FormFieldHandle extends FormField {
  readonly editing: Signal<boolean>;
  readonly validating: Signal<boolean>;
  readonly errors: Signal<readonly string[]>;
  readonly error: Signal<unknown>;
}

export interface InjectFormOptions {
  /** Whether the View reports unsaved changes, such as `computed(() => this.editor.state()?.isDirty === true)`. */
  readonly dirty?: Signal<boolean | undefined>;
}

/** A form bound by {@link injectForm}: its state as signals, and its actions. */
export interface FormHandle {
  /** The whole state. */
  readonly state: Signal<FormState>;
  readonly dirty: Signal<boolean>;
  readonly editing: Signal<boolean>;
  readonly validating: Signal<boolean>;
  readonly pending: Signal<boolean>;
  readonly valid: Signal<boolean>;
  readonly submitting: Signal<boolean>;
  /** Commits every field, waits for their writes and runs `command` unless a field's write or text failed. */
  submit<T>(command: () => T | PromiseLike<T>): Promise<FormSubmitResult<T>>;
  /** Commits every field and resolves once their writes settled. */
  commit(): Promise<FormState>;
  /** Resets every field. */
  reset(): void;
}

/**
 * One dirty, pending and valid state over `injectField` handles, and a `submit` that writes every field before its
 * command (`createFormController` in `@runic-artifex/views`). It needs no injection context.
 *
 * ```ts
 * readonly form = injectForm({ title: this.title, body: this.body }, { dirty: computed(() => this.editor.state()?.isDirty === true) });
 * // <button [disabled]="!form.valid() || form.submitting()" (click)="form.submit(() => editor.client()!.save())">Save</button>
 * ```
 */
export function injectForm(fields: FormFields<FormFieldHandle>, options: InjectFormOptions = {}): FormHandle {
  const list = formFieldList(fields);
  const submitting = signal(false);
  const state = computed(() => formStateOf(
    list.map(field => ({ editing: field.editing(), validating: field.validating(), errors: field.errors(), error: field.error() })),
    options.dirty?.() === true, submitting()));
  return {
    state,
    dirty: computed(() => state().dirty),
    editing: computed(() => state().editing),
    validating: computed(() => state().validating),
    pending: computed(() => state().pending),
    valid: computed(() => state().valid),
    submitting: computed(() => state().submitting),
    async submit<T>(command: () => T | PromiseLike<T>): Promise<FormSubmitResult<T>> {
      if (submitting()) return { status: "busy" };
      submitting.set(true);
      try { return await submitFields(list, command); }
      finally { submitting.set(false); }
    },
    async commit(): Promise<FormState> {
      await Promise.all(list.map(field => field.commit()));
      return state();
    },
    reset() { for (const field of list) field.reset(); },
  };
}
