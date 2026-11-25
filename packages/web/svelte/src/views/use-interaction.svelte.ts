import { untrack } from "svelte";
import {
  createInteractionController,
  interactionSurface,
  type InteractionInput,
  type InteractionName,
  type InteractionOutput,
  type ViewClient,
} from "@runic-artifex/views";
import { getRunicInstance } from "./instance.js";

/** The View whose interaction is answered: a `useView` handle, or any client. */
export interface InteractionView<TClient extends ViewClient> {
  readonly client: TClient | undefined;
}

/** An interaction answered from the markup: its open request, and `respond`. */
export interface InteractionHandle<TInput, TOutput> {
  /** The input of the oldest open request, or `undefined` while none is open. */
  readonly request: TInput | undefined;
  /** How many requests are open; for an interaction without an input (`Unit`), `pending > 0` tells that one is. */
  readonly pending: number;
  /** Answers the oldest open request, and shows the next one. Ignored when no request is open. */
  respond(answer: TOutput): void;
}

/**
 * Handles one interaction of a View while the calling component is mounted, through `createInteractionController`:
 * render the dialog from `request` and answer it with `respond`. Pass the name of a generated interaction, such as
 * `"confirmDiscard"` for `Interaction<DiscardInput, bool> ConfirmDiscard`; the input and the answer are typed from it.
 * Call it during component initialization.
 *
 * ```svelte
 * const editor = useView(() => page);
 * const discard = useInteraction(() => editor, "confirmDiscard");
 * {#if discard.request}
 *   <Confirm text={discard.request.title} onyes={() => discard.respond(true)} onno={() => discard.respond(false)} />
 * {/if}
 * ```
 *
 * The handler is registered while the View's client is connected, and removed when the client changes or the
 * component is destroyed. A request ends when `respond` answers it, or when .NET stops waiting for it: the command that
 * asked was cancelled, the deadline passed, the window closed or the client changed. `request` then is the next open
 * request, oldest first, or `undefined`; an answer with no open request is ignored.
 */
export function useInteraction<TClient extends ViewClient, TName extends InteractionName<TClient>>(
  view: () => InteractionView<TClient>,
  name: TName,
): InteractionHandle<InteractionInput<TClient, TName>, InteractionOutput<TClient, TName>> {
  type TInput = InteractionInput<TClient, TName>;
  type TOutput = InteractionOutput<TClient, TName>;
  const controller = getRunicInstance().run(() => createInteractionController<TInput, TOutput>());
  let request = $state.raw<TInput | undefined>(undefined);
  let pending = $state(0);
  const unsubscribe = controller.subscribe(() => {
    request = controller.request;
    pending = controller.pending;
  });
  // A pre effect runs when it is created, so the teardown exists even if the owner is destroyed before its first
  // flush (see useOperation). It reads no state, so it never re-runs.
  $effect.pre(() => () => { unsubscribe(); controller.dispose(); });
  $effect.pre(() => {
    const { client } = view();
    untrack(() => controller.setSurface(interactionSurface<TInput, TOutput>(client, name)));
  });
  return {
    get request() { return request; },
    get pending() { return pending; },
    respond: (answer: TOutput) => controller.respond(answer),
  };
}
