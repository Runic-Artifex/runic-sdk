import { assertInInjectionContext, DestroyRef, effect, inject, Injector, signal, untracked, type Signal } from "@angular/core";
import {
  createInteractionController,
  interactionSurface,
  type InteractionInput,
  type InteractionName,
  type InteractionOutput,
  type ViewClient,
} from "@runic-artifex/views";
import { instanceOf } from "./instance.js";

/** The View whose interaction is answered: an {@link injectView} handle, or any client signal. */
export interface InteractionView<TClient extends ViewClient> {
  readonly client: Signal<TClient | undefined>;
}

// The client of a view, whichever signal type holds it.
type ClientOf<TView extends InteractionView<ViewClient>> = NonNullable<ReturnType<TView["client"]>>;

export interface InjectInteractionOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/** An interaction answered from the template: its open request as a signal, and `respond`. */
export interface InteractionHandle<TInput, TOutput> {
  /** The input of the oldest open request, or `undefined` while none is open. */
  readonly request: Signal<TInput | undefined>;
  /** How many requests are open; for an interaction without an input (`Unit`), `pending() > 0` tells that one is. */
  readonly pending: Signal<number>;
  /** Answers the oldest open request, and shows the next one. Ignored when no request is open. */
  respond(answer: TOutput): void;
}

/**
 * Handles one interaction of a View for the lifetime of the injection context, as signals, through
 * `createInteractionController`: render the dialog from `request()` and answer it with `respond`. Pass the name of a
 * generated interaction, such as `"confirmDiscard"` for `Interaction<DiscardInput, bool> ConfirmDiscard`; the input and
 * the answer are typed from it.
 *
 * ```ts
 * readonly editor = injectView(this.page);
 * readonly discard = injectInteraction(this.editor, "confirmDiscard");
 * // @if (discard.request(); as input) {
 * //   <app-confirm [text]="input.title" (yes)="discard.respond(true)" (no)="discard.respond(false)" />
 * // }
 * ```
 *
 * The handler is registered while the View's client is connected, and removed when the client changes or the context
 * is destroyed. A request ends when `respond` answers it, or when .NET stops waiting for it: the command that asked was
 * cancelled, the deadline passed, the window closed or the client changed. `request()` then shows the next open
 * request, oldest first, or `undefined`; an answer with no open request is ignored.
 */
export function injectInteraction<TView extends InteractionView<ViewClient>, TName extends InteractionName<ClientOf<TView>>>(
  view: TView,
  name: TName,
  { injector: provided }: InjectInteractionOptions = {},
): InteractionHandle<InteractionInput<ClientOf<TView>, TName>, InteractionOutput<ClientOf<TView>, TName>> {
  type TInput = InteractionInput<ClientOf<TView>, TName>;
  type TOutput = InteractionOutput<ClientOf<TView>, TName>;
  if (!provided) assertInInjectionContext(injectInteraction);
  const injector = provided ?? inject(Injector);
  const controller = instanceOf(injector).run(() => createInteractionController<TInput, TOutput>());
  const request = signal<TInput | undefined>(undefined);
  const pending = signal(0);
  controller.subscribe(() => {
    request.set(controller.request);
    pending.set(controller.pending);
  });
  effect(() => {
    const client = view.client();
    untracked(() => controller.setSurface(interactionSurface<TInput, TOutput>(client, name)));
  }, { injector });
  injector.get(DestroyRef).onDestroy(() => controller.dispose());
  return {
    request: request.asReadonly(),
    pending: pending.asReadonly(),
    respond(answer) { controller.respond(answer); },
  };
}
