import { useInsertionEffect, useMemo, useRef, useState } from "react";
import {
  formFieldList,
  formStateOf,
  submitFields,
  type FieldState,
  type FormField,
  type FormFields,
  type FormState,
  type FormSubmitResult,
} from "@runic-artifex/views";

/** A field as {@link useForm} reads it: a `useField` handle. */
export type FormFieldHandle = FormField & Pick<FieldState<unknown>, "editing" | "validating" | "errors" | "error">;

export interface UseFormOptions {
  /** Whether the View reports unsaved changes, such as `editor.state?.isDirty`. A field's draft makes the form dirty in any case. */
  readonly dirty?: boolean;
}

/** A form bound by {@link useForm}: its state in this render and its actions, which are the same on every render. */
export type FormHandle = FormState & {
  /**
   * Commits every field, waits for their writes and runs `command` unless a field's write or text failed:
   * `onClick={() => form.submit(() => editor.client!.save())}`. While it runs `submitting` is true and another `submit`
   * resolves `busy`.
   */
  submit<T>(command: () => T | PromiseLike<T>): Promise<FormSubmitResult<T>>;
  /** Commits every field and resolves once their writes settled, with the fields' state then. */
  commit(): Promise<FormState>;
  /** Resets every field. */
  reset(): void;
};

/**
 * One dirty, pending and valid state over the `useField` handles of a component, and a `submit` that writes every
 * field before its command (`createFormController` in `@runic-artifex/views`):
 *
 * ```tsx
 * const title = useField(editor, EditorFields.title, { commit: "pause" });
 * const body = useField(editor, EditorFields.body, { commit: "pause" });
 * const form = useForm({ title, body }, { dirty: editor.state?.isDirty });
 * <button disabled={!form.valid || form.submitting} onClick={() => form.submit(() => editor.client!.save())}>Save</button>
 * ```
 */
export function useForm(fields: FormFields<FormFieldHandle>, options: UseFormOptions = {}): FormHandle {
  const list = formFieldList(fields);
  const latest = useRef(list);
  useInsertionEffect(() => { latest.current = list; });
  const [submitting, setSubmitting] = useState(false);
  const busy = useRef(false);
  const dirty = useRef(options.dirty === true);
  dirty.current = options.dirty === true;
  const actions = useMemo(() => ({
    async submit<T>(command: () => T | PromiseLike<T>): Promise<FormSubmitResult<T>> {
      if (busy.current) return { status: "busy" };
      busy.current = true;
      setSubmitting(true);
      try { return await submitFields(latest.current, command); }
      finally {
        busy.current = false;
        setSubmitting(false);
      }
    },
    async commit(): Promise<FormState> {
      const states = await Promise.all(latest.current.map(field => field.commit()));
      return formStateOf(states, dirty.current, busy.current);
    },
    reset() { for (const field of latest.current) field.reset(); },
  }), []);
  return { ...formStateOf(list, options.dirty === true, submitting), ...actions };
}
