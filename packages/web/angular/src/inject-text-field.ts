import { assertInInjectionContext, computed, DestroyRef, effect, inject, Injector, signal, untracked, type Signal } from "@angular/core";
import { createTextFieldController, type ViewClient } from "@runic-artifex/views";

/** A text input bound to one View member. */
export interface TextFieldHandle {
  /** What the input shows: the draft while one exists, otherwise the member's value in the View's state. */
  readonly value: Signal<string>;
  /**
   * True while a draft exists, from the first input until the last write settled and the state caught up, and after a
   * failed write. Keep the input enabled while it is true, rather than disabling it on the View's busy state, which
   * the field's own write causes.
   */
  readonly editing: Signal<boolean>;
  /** Why the latest write failed, until the next input or `reset()`. */
  readonly error: Signal<unknown>;
  /** The user changed the text: pass the `input` event, or the new text. */
  input(event: Event | string): void;
  /** An IME composition started: the text is kept but not written until it ends. Bind it to `compositionstart`. */
  compositionStart(): void;
  /** The IME composition ended: pass the `compositionend` event, or the text, which is then written. */
  compositionEnd(event: Event | string): void;
  /** Drops the draft and the error and shows the View's value. Writes in flight still complete. */
  reset(): void;
}

export interface InjectTextFieldOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
  /**
   * With `true` (the default) at most one write is in flight, and text typed meanwhile is written next, as one
   * value. With `false` every input is written.
   */
  readonly coalesce?: boolean;
  /** With `true` (the default) a failed write keeps the draft and sets `error`; with `false` the field shows the View's value again. */
  readonly keepDraftOnError?: boolean;
}

/** The View a text field binds to: an {@link injectView} handle, or any pair of state and client signals. */
export interface TextFieldView<TClient extends ViewClient> {
  readonly state: Signal<TClient["snapshot"] | undefined>;
  readonly client: Signal<TClient | undefined>;
}

function textOf(event: Event | string): string {
  if (typeof event === "string") return event;
  const target = event.target as { value?: unknown } | null;
  return typeof target?.value === "string" ? target.value : "";
}

/**
 * Binds a text input to one member of a View for the lifetime of the injection context, through
 * `createTextFieldController`. Typing key by key never loses characters: while its writes are in flight the field
 * shows what the user typed, at most one write is in flight (newer text is written next), nothing is written during
 * IME composition, and once the last write settled it shows the View's value, including any normalization.
 *
 * ```ts
 * readonly person = injectView(this.page);
 * readonly name = injectTextField(this.person, state => state.name, (client, value) => client.setName(value));
 * // <input [value]="name.value()" (input)="name.input($event)"
 * //        (compositionstart)="name.compositionStart()" (compositionend)="name.compositionEnd($event)">
 * ```
 *
 * `write` may be a generated setter, a command or a checked write. Its failure sets `error` and keeps the draft.
 */
export function injectTextField<TClient extends ViewClient>(
  view: TextFieldView<TClient>,
  read: (state: TClient["snapshot"]) => string,
  write: (client: TClient, value: string) => Promise<unknown>,
  { injector: provided, ...options }: InjectTextFieldOptions = {},
): TextFieldHandle {
  if (!provided) assertInInjectionContext(injectTextField);
  const injector = provided ?? inject(Injector);
  const controller = createTextFieldController<TClient["snapshot"], TClient>({ read, write, ...options });
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
    editing: computed(() => current().editing),
    error: computed(() => current().error),
    input(event) { controller.input(textOf(event)); },
    compositionStart() { controller.compositionStart(); },
    compositionEnd(event) { controller.compositionEnd(textOf(event)); },
    reset() { controller.reset(); },
  };
}
