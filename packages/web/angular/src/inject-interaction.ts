import { assertInInjectionContext, DestroyRef, effect, inject, Injector, signal, untracked, type Signal } from "@angular/core";
import {
  createInteractionController,
  interactionSurface,
  type InteractionInput,
  type InteractionName,
  type InteractionOutput,
  type InteractionRequest,
  type ViewClient,
} from "@runic-artifex/views";
import { instanceOf } from "./instance.js";

/** The View whose interaction is answered: an {@link injectView} handle, or any client signal. */
export interface InteractionView<TClient extends ViewClient> {
  readonly client: Signal<TClient | undefined>;
}

export interface InjectInteractionOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/** An interaction answered from the template: its oldest open request as a signal. */
export interface InteractionHandle<TInput, TOutput> {
  /**
   * The oldest open request, `{ input, respond(answer) }`, or `undefined` while none is open. Its `respond` answers
   * that request only, and is ignored once the request has ended.
   */
  readonly request: Signal<InteractionRequest<TInput, TOutput> | undefined>;
}

/**
 * Handles one interaction of a View for the lifetime of the injection context, as signals, through
 * `createInteractionController`: render the dialog from `request()` and answer it with the request's `respond`. Pass
 * the name of a generated interaction, such as `"confirmDiscard"` for `Interaction<DiscardInput, bool> ConfirmDiscard`;
 * the input and the answer are typed from it.
 *
 * ```ts
 * readonly editor = injectView(this.page);
 * readonly discard = injectInteraction(this.editor, "confirmDiscard");
 * // @if (discard.request(); as request) {
 * //   <app-confirm [text]="request.input.title" (yes)="request.respond(true)" (no)="request.respond(false)" />
 * // }
 * ```
 *
 * The handler is registered while the View's client is connected, and removed when the client changes or the context
 * is destroyed. A request ends when its `respond` answers it, or when .NET stops waiting for it: the command that asked
 * was cancelled, the deadline passed, the window closed or the client changed. `request()` then is the next open
 * request, oldest first, or `undefined`. A request's `respond` is ignored once it has ended, so a double click or a late
 * answer never answers another request.
 */
export function injectInteraction<TClient extends ViewClient & { readonly interactions: object }, TName extends InteractionName<TClient>>(
  view: InteractionView<TClient>,
  name: TName,
  { injector: provided }: InjectInteractionOptions = {},
): InteractionHandle<InteractionInput<TClient, TName>, InteractionOutput<TClient, TName>> {
  type TInput = InteractionInput<TClient, TName>;
  type TOutput = InteractionOutput<TClient, TName>;
  if (!provided) assertInInjectionContext(injectInteraction);
  const injector = provided ?? inject(Injector);
  const controller = instanceOf(injector).run(() => createInteractionController<TInput, TOutput>());
  const request = signal<InteractionRequest<TInput, TOutput> | undefined>(undefined);
  controller.subscribe(() => request.set(controller.request));
  effect(() => {
    const client = view.client();
    untracked(() => controller.setSurface(interactionSurface<TInput, TOutput>(client, name)));
  }, { injector });
  injector.get(DestroyRef).onDestroy(() => controller.dispose());
  return { request: request.asReadonly() };
}
