import { linkedSignal, untracked, type WritableSignal } from "@angular/core";
import { validate, type PathKind, type SchemaPath, type SchemaPathRules, type ValidationError } from "@angular/forms/signals";
import type { FieldHandle } from "../inject-field.js";

/**
 * A `WritableSignal` over an {@link injectField} handle, for a Signal Forms `form()`: it reads the field's `value`,
 * and `set` and `update` write through the field (`input`), so the form's `[formField]` edits the View.
 *
 * ```ts
 * readonly title = injectField(this.editor, EditorFields.title);
 * readonly titleForm = form(runicFieldModel(this.title), path => validateRunicField(path, this.title));
 * // <input [formField]="titleForm" (blur)="title.commit()">
 * ```
 */
export function runicFieldModel<TValue>(handle: FieldHandle<TValue>): WritableSignal<TValue> {
  const model = linkedSignal(() => handle.value());
  model.set = value => handle.input(value);
  model.update = update => handle.input(update(untracked(handle.value)));
  return model;
}

/**
 * Adds the field's validation messages (`errors()`) to a Signal Forms schema path as errors of kind `runic`, each
 * with its message.
 */
export function validateRunicField<TValue, TPathKind extends PathKind = PathKind.Root>(
  path: SchemaPath<TValue, SchemaPathRules.Supported, TPathKind>,
  handle: Pick<FieldHandle<unknown>, "errors">,
): void {
  validate(path, () => {
    const errors = handle.errors();
    return errors.length === 0 ? undefined : errors.map((message): ValidationError.WithoutFieldTree => ({ kind: "runic", message }));
  });
}
