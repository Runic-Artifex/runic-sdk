import { computed, reactive, shallowRef, toValue, type MaybeRefOrGetter } from "vue";
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
  /** Whether the View reports unsaved changes, such as `() => editor.state.value?.isDirty === true`. */
  readonly dirty?: MaybeRefOrGetter<boolean | undefined>;
}

/** A form bound by {@link useForm}: a `reactive` state and its actions. */
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
 * ```vue
 * const form = useForm({ title, body }, { dirty: () => editor.state.value?.isDirty === true });
 * <button :disabled="!form.valid || form.submitting" @click="form.submit(() => editor.client.value!.save())">Save</button>
 * ```
 */
export function useForm(fields: FormFields<FormFieldHandle>, options: UseFormOptions = {}): FormHandle {
  const list = formFieldList(fields);
  const submitting = shallowRef(false);
  const state = computed(() => formStateOf(list, toValue(options.dirty) === true, submitting.value));
  return reactive({
    dirty: computed(() => state.value.dirty),
    editing: computed(() => state.value.editing),
    validating: computed(() => state.value.validating),
    pending: computed(() => state.value.pending),
    valid: computed(() => state.value.valid),
    submitting: computed(() => state.value.submitting),
    async submit<T>(command: () => T | PromiseLike<T>, submitOptions: FormSubmitOptions = {}): Promise<FormSubmitResult<T>> {
      if (submitting.value) return { status: "busy" };
      submitting.value = true;
      try { return await submitFields(list, command, submitOptions, index => list[index]!); }
      finally { submitting.value = false; }
    },
    async commit(): Promise<FormState> {
      await Promise.all(list.map(field => field.commit()));
      return state.value;
    },
    reset() { for (const field of list) field.reset(); },
  }) as FormHandle;
}
