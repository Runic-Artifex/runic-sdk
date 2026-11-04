import {
  formFieldList,
  formStateOf,
  submitFields,
  type FieldState,
  type FormField,
  type FormFields,
  type FormState,
  type FormSubmitOptions,
  type FormSubmitResult,
} from "@runic-artifex/views";

/** A field as {@link useForm} reads it: a `useField` handle. */
export type FormFieldHandle = FormField & Pick<FieldState<unknown>, "editing" | "validating" | "errors" | "error">;

export interface UseFormOptions {
  /** Whether the View reports unsaved changes, such as `() => editor.state?.isDirty === true`. */
  readonly dirty?: () => boolean | undefined;
}

/** A form bound by {@link useForm}: its state, read reactively, and its actions. */
export interface FormHandle extends FormState {
  /** Commits every field, waits for their writes and runs `command` unless a field's write or text failed. */
  submit<T>(command: () => T | PromiseLike<T>, options?: FormSubmitOptions): Promise<FormSubmitResult<T>>;
  /** Commits every field and resolves once their writes settled. */
  commit(): Promise<FormState>;
  /** Resets every field. */
  reset(): void;
}

/**
 * One dirty, pending and valid state over `useField` handles, and a `submit` that writes every field before its
 * command (`createFormController` in `@runic-artifex/views`):
 *
 * ```svelte
 * const form = useForm(() => ({ title, body }), { dirty: () => editor.state?.isDirty === true });
 * <button disabled={!form.valid || form.submitting} onclick={() => form.submit(() => editor.client!.save())}>Save</button>
 * ```
 */
export function useForm(fields: () => FormFields<FormFieldHandle>, options: UseFormOptions = {}): FormHandle {
  let submitting = $state(false);
  const state = $derived(formStateOf(formFieldList(fields()), options.dirty?.() === true, submitting));
  return {
    get dirty() { return state.dirty; },
    get editing() { return state.editing; },
    get validating() { return state.validating; },
    get pending() { return state.pending; },
    get valid() { return state.valid; },
    get submitting() { return state.submitting; },
    async submit<T>(command: () => T | PromiseLike<T>, submitOptions: FormSubmitOptions = {}): Promise<FormSubmitResult<T>> {
      if (submitting) return { status: "busy" };
      submitting = true;
      const list = formFieldList(fields());
      try { return await submitFields(list, command, submitOptions, index => list[index]!); }
      finally { submitting = false; }
    },
    async commit(): Promise<FormState> {
      await Promise.all(formFieldList(fields()).map(field => field.commit()));
      return state;
    },
    reset() { for (const field of formFieldList(fields())) field.reset(); },
  };
}
