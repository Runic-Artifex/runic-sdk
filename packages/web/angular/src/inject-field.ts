import { assertInInjectionContext, computed, DestroyRef, effect, inject, Injector, signal, untracked, type Signal } from "@angular/core";
import {
  createFieldController,
  type BridgeField,
  type FieldBaseline,
  type FieldController,
  type FieldDefinition,
  type FieldOptions,
  type FieldState,
  type ViewClient,
} from "@runic-artifex/views";

/** The View a field binds to: an {@link injectView} handle, or any pair of state and client signals. */
export interface FieldView<TClient extends ViewClient> {
  readonly state: Signal<TClient["snapshot"] | undefined>;
  readonly client: Signal<TClient | undefined>;
}

export interface InjectFieldOptions extends FieldOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/** One View member bound to an input, as signals. See `FieldState` and `FieldController` in `@runic-artifex/views`. */
export interface FieldHandle<TValue> {
  /** The draft while one exists, otherwise the member's value in the View's state. */
  readonly value: Signal<TValue>;
  /** What a text input shows: the text typed while it does not parse or still means `value`, otherwise `value` formatted. */
  readonly text: Signal<string>;
  /**
   * True while a draft exists, from the first input until the last write settled and the state caught up, and after a
   * failed write. Keep the input enabled while it is true, rather than disabling it on the View's busy state, which
   * the field's own write causes.
   */
  readonly editing: Signal<boolean>;
  /** Why the latest write or text failed: a `BridgeError` of the write, or a `FieldError`. `undefined` otherwise. */
  readonly error: Signal<unknown>;
  /** The member's validation messages; `[]` when valid. */
  readonly errors: Signal<readonly string[]>;
  /** The producer's value and version while a checked write's `conflict` keeps the draft. */
  readonly conflict: Signal<FieldBaseline<TValue> | undefined>;
  /** The definition's `kind`, `nullable`, `integer` and `choices`. */
  readonly field: FieldController<unknown, unknown, TValue>["field"];
  /** The user changed the value, such as a checkbox's `checked`. */
  input(value: TValue): void;
  /** The user changed the text: pass the `input` event, or the text. It is parsed for the field's kind. */
  inputText(event: Event | string): void;
  /** An IME composition started: the text is kept but not written until it ends. Bind it to `compositionstart`. */
  compositionStart(): void;
  /** The IME composition ended: pass the `compositionend` event, or the text, which is then written. */
  compositionEnd(event: Event | string): void;
  /**
   * The user is done with the field (bind it to `blur`): with `commit: "pause"` the draft is written now. Resolves
   * with the field's state once its writes settled, so a command that depends on the field can follow it.
   */
  commit(): Promise<FieldState<TValue>>;
  /** Writes the draft again after a failed write; after a `conflict`, over the producer's value. */
  retry(): void;
  /** Drops the draft, the error and a conflict and shows the View's value. Writes in flight still complete. */
  reset(): void;
}

function textOf(event: Event | string): string {
  if (typeof event === "string") return event;
  const target = event.target as { value?: unknown } | null;
  return typeof target?.value === "string" ? target.value : "";
}

/**
 * Binds one member of a View to an input for the lifetime of the injection context, through
 * `createFieldController`: pass a generated descriptor such as `EditorFields.title`. Typing never loses characters,
 * nothing is written during IME composition, checked-write receipts and the member's validation show as `error`,
 * `errors` and `conflict`, and once the last write settled the field shows the View's value.
 *
 * ```ts
 * readonly editor = injectView(this.page);
 * readonly title = injectField(this.editor, EditorFields.title);
 * // <input [runicField]="title"> with RunicFieldDirective from @runic-artifex/angular/forms, or by hand:
 * // <input [value]="title.text()" (input)="title.inputText($event)" (blur)="title.commit()"
 * //        (compositionstart)="title.compositionStart()" (compositionend)="title.compositionEnd($event)">
 * ```
 */
export function injectField<TClient extends ViewClient, TValue>(
  view: FieldView<TClient>,
  field: BridgeField<TClient, TClient["snapshot"], TValue> | FieldDefinition<TClient, TClient["snapshot"], TValue>,
  { injector: provided, ...options }: InjectFieldOptions = {},
): FieldHandle<TValue> {
  if (!provided) assertInInjectionContext(injectField);
  const injector = provided ?? inject(Injector);
  const controller = createFieldController<TClient["snapshot"], TClient, TValue>(field, options);
  const current = signal(controller.current);
  controller.subscribe(() => current.set(controller.current));
  effect(() => {
    const state = view.state();
    const client = view.client();
    untracked(() => {
      controller.setSource(state === undefined || client === undefined ? undefined : { state, client });
      current.set(controller.current);
    });
  }, { injector });
  injector.get(DestroyRef).onDestroy(() => controller.dispose());
  return {
    value: computed(() => current().value),
    text: computed(() => current().text),
    editing: computed(() => current().editing),
    error: computed(() => current().error),
    errors: computed(() => current().errors),
    conflict: computed(() => current().conflict),
    field: controller.field,
    input(value) { controller.input(value); },
    inputText(event) { controller.inputText(textOf(event)); },
    compositionStart() { controller.compositionStart(); },
    compositionEnd(event) { controller.compositionEnd(textOf(event)); },
    commit() { return controller.commit(); },
    retry() { controller.retry(); },
    reset() { controller.reset(); },
  };
}
