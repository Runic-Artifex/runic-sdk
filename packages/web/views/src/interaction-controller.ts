/// <reference lib="esnext.disposable" preserve="true" />
// An interaction as state (#1319): the open requests of one interaction handler, oldest first, for a page that
// renders its dialog from state instead of awaiting a dialog service in `handle`.
import type { BridgeInteractionContext } from "./interactions.js";
import { disposable } from "./lifetime.js";

/** One interaction of a generated client: `client.interactions.confirmDiscard`. */
export interface InteractionHandlerSurface<TInput, TOutput> {
  handle(handler: (input: TInput, context: BridgeInteractionContext) => TOutput | Promise<TOutput>): () => void;
}

/**
 * The names of a generated client's interactions, such as `"confirmDiscard"`. As a type parameter's constraint it keeps
 * a literal argument (`name: TName`), where a constraint computed by a conditional type would widen it to every name.
 */
export type InteractionName<TClient extends { readonly interactions: object }> = keyof TClient["interactions"] & string;

// The `handle` handler of an interaction of a generated client.
type InteractionHandlerOf<TClient, TName> = TClient extends { readonly interactions: infer TInteractions }
  ? TName extends keyof TInteractions
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    ? TInteractions[TName] extends { handle(handler: infer THandler): unknown } ? THandler extends (...args: any) => any ? THandler : never : never
    : never
  : never;

/** The input of an interaction of a generated client: the C# `Interaction<TInput, TOutput>`'s or `ViewQuestion`'s `TInput`. */
export type InteractionInput<TClient, TName extends string> = Parameters<InteractionHandlerOf<TClient, TName>>[0];

/** The answer of an interaction of a generated client: the C# `Interaction<TInput, TOutput>`'s or `ViewQuestion`'s `TOutput`. */
export type InteractionOutput<TClient, TName extends string> = Awaited<ReturnType<InteractionHandlerOf<TClient, TName>>>;

/**
 * One open request of an interaction: its input, and `respond`, which answers this request only. The same object
 * while the request is open, so a template can key on it.
 */
export interface InteractionRequest<TInput, TOutput> {
  /** The request's input, from the C# interaction's `TInput`. */
  readonly input: TInput;
  /**
   * Answers this request. Ignored once it has ended: answered already (a double click), or ended by .NET (the
   * command that asked was cancelled, the deadline passed, the window closed) or by a client change or disposal.
   */
  respond(answer: TOutput): void;
}

/**
 * A framework-neutral handler of one interaction whose requests are state (`@runic-artifex/angular`, `react`, `vue`
 * and `svelte` wrap it as `injectInteraction` / `useInteraction`). While it has a surface it handles the interaction:
 * each request waits, oldest first, until its `respond` answers it or .NET stops waiting for it.
 */
export interface InteractionController<TInput, TOutput> {
  /** The oldest open request, or `undefined` while none is open. */
  readonly request: InteractionRequest<TInput, TOutput> | undefined;
  /** Calls `listener` after each change of `request`, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /**
   * Handles the interaction of `surface`, such as `client.interactions.confirmDiscard`, or none for `undefined`. The
   * previous surface's handler is removed, and its open requests end: .NET receives them as cancelled.
   */
  setSurface(surface: InteractionHandlerSurface<TInput, TOutput> | undefined): void;
  /** Removes the handler; open requests end as cancelled. */
  dispose(): void;
}

interface OpenRequest<TInput, TOutput> {
  readonly request: InteractionRequest<TInput, TOutput>;
  readonly resolve: (answer: TOutput | undefined) => void;
  readonly signal: AbortSignal;
  readonly aborted: () => void;
}

/** The interaction surface of `client`, such as `client.interactions.confirmDiscard`, or `undefined` without a client. */
export function interactionSurface<TInput, TOutput>(client: unknown, name: string): InteractionHandlerSurface<TInput, TOutput> | undefined {
  if (client === undefined || client === null) return undefined;
  const interactions = (client as { readonly interactions?: Readonly<Record<string, unknown>> }).interactions;
  const surface = interactions?.[name] as InteractionHandlerSurface<TInput, TOutput> | undefined;
  if (surface === undefined) throw new TypeError(`The client has no interaction named "${name}".`);
  return surface;
}

/**
 * Creates an {@link InteractionController}. A `using` declaration disposes it where the engine has `Symbol.dispose`.
 *
 * ```ts
 * const discard = createInteractionController<DiscardNoteRequest, boolean>();
 * discard.setSurface(editor.interactions.confirmDiscard);
 * discard.subscribe(() => {
 *   const request = discard.request;
 *   dialog.hidden = request === undefined;
 *   yes.onclick = () => request?.respond(true);
 * });
 * ```
 *
 * A request ends when its `respond` answers it, or when .NET stops waiting for it: the command that asked was
 * cancelled, the deadline passed, the window closed, the client disconnected, or the surface changed. Its `respond`
 * is ignored after that, and never answers another request.
 */
export function createInteractionController<TInput, TOutput>(): InteractionController<TInput, TOutput> & Disposable {
  const listeners = new Set<() => void>();
  let open: OpenRequest<TInput, TOutput>[] = [];
  let surface: InteractionHandlerSurface<TInput, TOutput> | undefined;
  let remove: (() => void) | undefined;
  let disposed = false;
  let request: InteractionRequest<TInput, TOutput> | undefined;

  function notify(): void {
    const head = open[0]?.request;
    if (head === request) return;
    request = head;
    for (const listener of [...listeners]) listener();
  }

  // Ends `entry` once: an answer, an abort or the end of the handler, whichever comes first.
  function close(entry: OpenRequest<TInput, TOutput>, answer: TOutput | undefined): void {
    if (!open.includes(entry)) return;
    entry.signal.removeEventListener("abort", entry.aborted);
    open = open.filter(item => item !== entry);
    entry.resolve(answer);
  }

  function handler(input: TInput, { signal }: BridgeInteractionContext): Promise<TOutput> {
    // An aborted request's answer is not sent: .NET receives it as cancelled.
    if (signal.aborted) return Promise.resolve(undefined as TOutput);
    return new Promise<TOutput>(resolve => {
      const entry: OpenRequest<TInput, TOutput> = {
        request: Object.freeze({
          input,
          respond: (answer: TOutput) => {
            if (disposed) return;
            close(entry, answer);
            notify();
          },
        }),
        signal, resolve: resolve as (answer: TOutput | undefined) => void,
        aborted: () => { close(entry, undefined); notify(); },
      };
      signal.addEventListener("abort", entry.aborted, { once: true });
      open = [...open, entry];
      notify();
    });
  }

  function endAll(): void {
    for (const entry of open) close(entry, undefined);
  }

  const controller: InteractionController<TInput, TOutput> & Disposable = disposable({
    get request() { return request; },
    subscribe(listener: () => void) {
      if (disposed) return () => {};
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    setSurface(next: InteractionHandlerSurface<TInput, TOutput> | undefined) {
      if (disposed || next === surface) return;
      const previous = remove;
      surface = next;
      remove = undefined;
      // Removing the handler aborts its requests; any that remain are ended here, so the state clears at once.
      previous?.();
      endAll();
      notify();
      if (next === undefined) return;
      try { remove = next.handle(handler); } catch (failure) {
        // A released client handles nothing; the next surface is registered again.
        surface = undefined;
        throw failure;
      }
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      remove?.();
      remove = undefined;
      surface = undefined;
      endAll();
      notify();
      listeners.clear();
    },
  });
  return controller;
}
