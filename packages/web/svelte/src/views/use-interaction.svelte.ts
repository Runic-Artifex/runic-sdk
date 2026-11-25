import { untrack } from "svelte";
import {
  createInteractionController,
  interactionSurface,
  type InteractionInput,
  type InteractionName,
  type InteractionOutput,
  type InteractionRequest,
  type ViewClient,
} from "@runic-artifex/views";
import { getRunicInstance } from "./instance.js";

/** The View whose interaction is answered: a `useView` handle, or any client. */
export interface InteractionView<TClient extends ViewClient> {
  readonly client: TClient | undefined;
}

/** An interaction answered from the markup: its oldest open request. */
export interface InteractionHandle<TInput, TOutput> {
  /**
   * The oldest open request, `{ input, respond(answer) }`, or `undefined` while none is open: the same object while the
   * request is open, so it can key an `{#each}` or `{#key}`. Its `respond` answers that request only, and is ignored
   * once it has ended.
   */
  readonly request: InteractionRequest<TInput, TOutput> | undefined;
}

/**
 * Handles one interaction of a View while the calling component is mounted, through `createInteractionController`:
 * render the dialog from `request` and answer it with the request's `respond`. Pass the name of a generated interaction, such as
 * `"confirmDiscard"` for `Interaction<DiscardInput, bool> ConfirmDiscard`; the input and the answer are typed from it.
 * Call it during component initialization.
 *
 * ```svelte
 * const editor = useView(() => page);
 * const discard = useInteraction(() => editor, "confirmDiscard");
 * {#if discard.request}
 *   {@const request = discard.request}
 *   <Confirm text={request.input.title} onyes={() => request.respond(true)} onno={() => request.respond(false)} />
 * {/if}
 * ```
 *
 * The handler is registered while the View's client is connected, and removed when the client changes or the
 * component is destroyed. A request ends when its `respond` answers it, or when .NET stops waiting for it: the command
 * that asked was cancelled, the deadline passed, the window closed or the client changed. `request` then is the next
 * open request, oldest first, or `undefined`. A request's `respond` is ignored once it has ended, so a double click or a
 * late answer never answers another request.
 */
export function useInteraction<TClient extends ViewClient & { readonly interactions: object }, TName extends InteractionName<TClient>>(
  view: () => InteractionView<TClient>,
  name: TName,
): InteractionHandle<InteractionInput<TClient, TName>, InteractionOutput<TClient, TName>> {
  type TInput = InteractionInput<TClient, TName>;
  type TOutput = InteractionOutput<TClient, TName>;
  const controller = getRunicInstance().run(() => createInteractionController<TInput, TOutput>());
  let request = $state.raw<InteractionRequest<TInput, TOutput> | undefined>(undefined);
  const unsubscribe = controller.subscribe(() => { request = controller.request; });
  // A pre effect runs when it is created, so the teardown exists even if the owner is destroyed before its first
  // flush (see useOperation). It reads no state, so it never re-runs.
  $effect.pre(() => () => { unsubscribe(); controller.dispose(); });
  $effect.pre(() => {
    const { client } = view();
    untrack(() => controller.setSurface(interactionSurface<TInput, TOutput>(client, name)));
  });
  return { get request() { return request; } };
}
