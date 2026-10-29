import { afterRenderEffect, computed, Directive, effect, ElementRef, forwardRef, inject, Injector, input, untracked } from "@angular/core";
import {
  NG_VALIDATORS,
  NG_VALUE_ACCESSOR,
  NgControl,
  type AbstractControl,
  type ControlValueAccessor,
  type ValidationErrors,
  type Validator,
} from "@angular/forms";
import type { FieldHandle } from "../inject-field.js";

type Wiring = "text" | "checkbox" | "radio" | "select";

// No control synced yet: the first `writeValue` of a control is its setup, not a change by the form.
const unsynced: unique symbol = Symbol("unsynced");

/**
 * Wires an `<input>`, `<textarea>` or `<select>` to an {@link injectField} handle: `<input [runicField]="title">`.
 *
 * Alone, it writes what the user types (`input`; `change` for checkboxes, radios and selects), holds writes during IME
 * composition, commits on blur, keeps the element's `value` (or `checked`) equal to the field without moving the
 * caret, and sets `aria-invalid` while the field has an `error` or `errors`.
 *
 * It is also the `ControlValueAccessor` and `Validator` of a `formControl` or `formControlName` on the same element:
 * the View's value flows into the control without marking it dirty or emitting `valueChanges`, the user's input
 * reaches it as usual, `patchValue` and `reset` write through to the View when they change the value, `disable()`
 * disables the element, and the control is invalid with `{ runic: errors }` while the View reports `errors`.
 */
@Directive({
  selector: "[runicField]",
  standalone: true,
  providers: [
    { provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => RunicFieldDirective), multi: true },
    { provide: NG_VALIDATORS, useExisting: forwardRef(() => RunicFieldDirective), multi: true },
  ],
  host: {
    "(input)": "onInput()",
    "(change)": "onChange()",
    "(compositionstart)": "onCompositionStart()",
    "(compositionend)": "onCompositionEnd()",
    "(blur)": "onBlur()",
    "[attr.aria-invalid]": "invalid() ? 'true' : null",
  },
})
export class RunicFieldDirective implements ControlValueAccessor, Validator {
  /** The field to wire, from {@link injectField}. */
  readonly runicField = input.required<FieldHandle<unknown>>();

  protected readonly invalid = computed(() => {
    const field = this.runicField();
    return field.error() !== undefined || field.errors().length !== 0;
  });

  private readonly element = inject<ElementRef<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>>(ElementRef).nativeElement;
  // NgControl injects this directive as its accessor, so it is looked up when needed rather than injected.
  private readonly injector = inject(Injector);
  private composing = false;
  private changed: ((value: unknown) => void) | undefined;
  private touched: (() => void) | undefined;
  private validatorChanged: (() => void) | undefined;
  private validated: readonly string[] | undefined;
  private synced: AbstractControl | undefined | typeof unsynced = unsynced;

  constructor() {
    afterRenderEffect({
      write: () => {
        const field = this.runicField();
        const value = field.value();
        const text = field.text();
        if (this.composing) return;
        const element = this.element;
        switch (this.wiring()) {
          case "checkbox": {
            const checkbox = element as HTMLInputElement;
            if (checkbox.checked !== (value === true)) checkbox.checked = value === true;
            checkbox.indeterminate = value === null || value === undefined;
            break;
          }
          case "radio": {
            const radio = element as HTMLInputElement;
            if (radio.checked !== (radio.value === text)) radio.checked = radio.value === text;
            break;
          }
          default:
            // Only a changed value is assigned, which keeps the caret where it is.
            if (element.value !== text) element.value = text;
        }
      },
    });
    // The View's value, and the user's while a draft exists, flows into the control without dirtying it.
    effect(() => {
      this.runicField().value();
      untracked(() => this.syncControl());
    });
    effect(() => {
      const errors = this.runicField().errors();
      untracked(() => {
        if (this.validated !== undefined && errors !== this.validated) this.validatorChanged?.();
      });
    });
  }

  private wiring(): Wiring {
    const element = this.element;
    if (element.tagName === "SELECT") return "select";
    if (element.tagName === "INPUT") {
      const type = (element as HTMLInputElement).type;
      if (type === "checkbox" || type === "radio") return type;
    }
    return "text";
  }

  private control(): AbstractControl | undefined {
    return this.injector.get(NgControl, null, { self: true, optional: true })?.control ?? undefined;
  }

  private syncControl(): void {
    const control = this.control();
    if (control === undefined) return;
    this.synced = control;
    const value = this.runicField().value();
    if (!Object.is(control.value, value)) control.setValue(value, { emitEvent: false, emitModelToViewChange: false });
  }

  private changedByUser(): void {
    this.changed?.(this.runicField().value());
  }

  protected onInput(): void {
    if (this.wiring() !== "text") return;
    this.runicField().inputText(this.element.value);
    if (!this.composing) this.changedByUser();
  }

  protected onChange(): void {
    const field = this.runicField();
    switch (this.wiring()) {
      case "checkbox": field.input((this.element as HTMLInputElement).checked); break;
      case "radio": if (!(this.element as HTMLInputElement).checked) return; field.inputText(this.element.value); break;
      case "select": field.inputText(this.element.value); break;
      default: return;
    }
    this.changedByUser();
  }

  protected onCompositionStart(): void {
    this.composing = true;
    this.runicField().compositionStart();
  }

  protected onCompositionEnd(): void {
    this.composing = false;
    this.runicField().compositionEnd(this.element.value);
    this.changedByUser();
  }

  protected onBlur(): void {
    void this.runicField().commit();
    this.touched?.();
  }

  /** A value set by the form (`setValue`, `patchValue`, `reset`): written to the View when it differs from the field's. */
  writeValue(value: unknown): void {
    const control = this.control();
    // Setting up a control writes its value first: the View's value is the one to keep.
    if (this.changed === undefined || control !== this.synced) {
      queueMicrotask(() => this.syncControl());
      return;
    }
    const field = this.runicField();
    if (Object.is(value, untracked(field.value))) return;
    if ((value === null || value === undefined) && !field.field.nullable) {
      // `reset()` of a nullable control: a non-nullable field keeps its value, and the control shows it again.
      queueMicrotask(() => this.syncControl());
      return;
    }
    field.input(value);
  }

  registerOnChange(fn: (value: unknown) => void): void { this.changed = fn; }

  registerOnTouched(fn: () => void): void { this.touched = fn; }

  setDisabledState(disabled: boolean): void { this.element.disabled = disabled; }

  /** `{ runic: errors }` while the View reports validation messages for the field. */
  validate(_control?: AbstractControl): ValidationErrors | null {
    const errors = untracked(this.runicField().errors);
    this.validated = errors;
    return errors.length === 0 ? null : { runic: errors };
  }

  registerOnValidatorChange(fn: () => void): void { this.validatorChanged = fn; }
}
