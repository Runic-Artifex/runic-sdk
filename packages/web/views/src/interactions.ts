// Interaction handlers of a presentation (specs/application, "Interactions"): registered with `open` and
// `interactions`, asked through `interaction` pushes and answered with `interaction-reply`. A generated client
// passes `defineInteractions(…)` to `connectView` only when its View has interactions.
import { clock } from "./clock.js";
import { BridgeError } from "./errors.js";
import type { Message, Push, Reply } from "./protocol.js";
import { reportBridgeError } from "./transport.js";

export interface BridgeInteractionContext {
  /** Aborts when the producer cancels the request, the handler is replaced, or the View is disposed. */
  readonly signal: AbortSignal;
}

/** Generated codec and contract of one interaction. */
export interface InteractionDefinition {
  readonly contract: string;
  readonly decodeInput: (value: unknown) => unknown;
  readonly encodeOutput: (value: unknown) => unknown;
}

export interface InteractionSurface {
  handle(handler: (input: never, context: BridgeInteractionContext) => unknown): () => void;
}

/** The presentation one set of interaction handlers belongs to. Created by `connectView`. */
export interface InteractionScope {
  readonly route: string;
  /** The presentation id, which stays the same across reconnects. */
  readonly presentation: string;
  /** The presentation is not disposed and its route is open. */
  readonly live: () => boolean;
  /** Sends a request of the route. */
  request(message: Message): Promise<Reply>;
  /** Registers a handler for interaction pushes of the route. */
  onPush(kind: "interaction" | "interaction-cancelled", handler: (push: Push) => void): () => void;
}

/** The interaction handlers of one open presentation. */
export interface InteractionSession {
  readonly surface: Readonly<Record<string, InteractionSurface>>;
  /** The handlers to register with `open` (generation 0). */
  handlers(): readonly { readonly name: string; readonly contract: string }[];
  /** The presentation was opened on a connection; `registration` answers the handlers sent with `open`. */
  opened(registration: string | undefined): void;
  /** The connection closed: the producer ended the presentation's requests. */
  paused(): void;
  /** Resolves when the producer acknowledged the registered handlers. */
  ready(): Promise<void>;
  dispose(): void;
}

/**
 * The interactions of one generated client, passed to `connectView` as
 * `interactions`. A View without interactions does not bundle this code.
 */
export type BridgeInteractions = (scope: InteractionScope) => InteractionSession;

/** Binds the interaction codecs of a generated client to the interaction protocol. */
export function defineInteractions(definitions: Readonly<Record<string, InteractionDefinition>>): BridgeInteractions {
  return scope => new InteractionRuntime(scope, definitions);
}

type InteractionHandler = {
  readonly name: string;
  readonly contract: string;
  readonly controller: AbortController;
  readonly handle: (input: unknown, context: BridgeInteractionContext) => unknown;
};
type InteractionRequest = Extract<Push, { kind: "interaction" }>;

// An interaction expires after at most ten minutes (README, "Interactions").
const maximumExpiry = 600_000;

/**
 * The browser half of the interaction protocol for one presentation. It registers only active handlers, so an
 * unrendered control keeps the producer's fallback.
 */
class InteractionRuntime implements InteractionSession {
  readonly surface: Readonly<Record<string, InteractionSurface>>;
  private readonly registered = new Map<string, InteractionHandler>();
  private generation = 0;
  private open = false;
  private disposed = false;
  /** The latest registration; `ready()` waits for it. */
  private registration: Promise<void> = Promise.resolve();
  private readonly active = new Map<string, AbortController>();
  private readonly cancelled = new Map<string, unknown>();
  private readonly stops: (() => void)[];

  constructor(private readonly scope: InteractionScope, private readonly definitions: Readonly<Record<string, InteractionDefinition>>) {
    const surface: Record<string, InteractionSurface> = {};
    for (const name of Object.keys(definitions)) surface[name] = { handle: handler => this.register(name, handler as InteractionHandler["handle"]) };
    this.surface = surface;
    this.stops = [
      scope.onPush("interaction", push => { if (push["presentation"] === scope.presentation) void this.handleRequest(push as InteractionRequest); }),
      scope.onPush("interaction-cancelled", push => { if (push["presentation"] === scope.presentation) this.cancel(push["requestId"] as string); }),
    ];
  }

  handlers(): readonly { readonly name: string; readonly contract: string }[] {
    return [...this.registered.values()].map(handler => ({ name: handler.name, contract: handler.contract }));
  }

  opened(registration: string | undefined): void {
    this.open = true;
    const sent = this.handlers();
    if (sent.length !== 0 && registration !== undefined && registration !== "ok" && registration !== "stale")
      this.registration = Promise.reject(this.failure(registration));
    else this.registration = Promise.resolve();
    void this.registration.catch(() => undefined);
  }

  paused(): void {
    this.open = false;
    this.abortActive();
  }

  async ready(): Promise<void> {
    if (this.registered.size === 0) return;
    await this.registration;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const stop of this.stops) stop();
    for (const handler of this.registered.values()) handler.controller.abort();
    this.registered.clear();
    this.abortActive();
  }

  private failure(registration: string): BridgeError {
    return registration === "ignored"
      ? new BridgeError("released", "The interaction presentation is no longer open.", { route: this.scope.route })
      : new BridgeError("failed", `The interaction handlers were not registered: ${registration}.`, { route: this.scope.route });
  }

  private register(name: string, handle: InteractionHandler["handle"]): () => void {
    if (this.disposed || !this.scope.live()) throw new BridgeError("released", "The interaction View was released.", { route: this.scope.route });
    const definition = this.definitions[name]!;
    const key = identity(name, definition.contract);
    this.registered.get(key)?.controller.abort();
    const handler: InteractionHandler = { name, contract: definition.contract, controller: new AbortController(), handle };
    this.registered.set(key, handler);
    this.sync();
    return () => {
      if (this.registered.get(key) !== handler) return;
      this.registered.delete(key);
      handler.controller.abort();
      this.sync();
    };
  }

  /** Sends the current handlers with the next generation, after the registration before it. */
  private sync(): void {
    if (this.disposed || !this.open) return;
    const generation = ++this.generation;
    const handlers = this.handlers();
    const previous = this.registration.catch(() => undefined);
    this.registration = previous.then(async () => {
      if (this.disposed || !this.open || generation !== this.generation) return;
      let reply: Reply;
      try {
        reply = await this.scope.request({ kind: "interactions", route: this.scope.route, presentation: this.scope.presentation, generation, handlers });
      } catch (cause) {
        throw cause instanceof BridgeError ? cause
          : new BridgeError("failed", "The interaction handlers could not be registered.", { cause, route: this.scope.route });
      }
      if (!reply.ok) throw new BridgeError(reply.error?.kind === "released" ? "released" : "failed",
        reply.error?.message ?? "The interaction handlers could not be registered.", { route: this.scope.route });
      // `stale`: a newer registration is on its way.
      const registration = reply["registration"] as string;
      if (registration !== "ok" && registration !== "stale") throw this.failure(registration);
    });
    void this.registration.catch(() => undefined);
  }

  private abortActive(): void {
    for (const controller of this.active.values()) controller.abort();
    this.active.clear();
  }

  private cancel(requestId: string): void {
    const active = this.active.get(requestId);
    if (active) { active.abort(); return; }
    // A cancellation that arrives before its request; a window holds at most 32 pending requests.
    if (this.cancelled.has(requestId)) return;
    if (this.cancelled.size >= 32) {
      const [oldest, timer] = this.cancelled.entries().next().value as [string, unknown];
      clock.clear(timer);
      this.cancelled.delete(oldest);
    }
    this.cancelled.set(requestId, clock.set(() => this.cancelled.delete(requestId), 120_000));
  }

  private async reply(request: InteractionRequest, outcome: "answered" | "cancelled" | "failed", output?: unknown): Promise<void> {
    try {
      const reply = await this.scope.request({
        kind: "interaction-reply", route: request.route, presentation: request.presentation, requestId: request.requestId,
        ownerEpoch: request.ownerEpoch, name: request.name, contract: request.contract, outcome,
        ...(outcome === "answered" ? { output } : {}),
      });
      if (reply.ok && reply["result"] === "invalid-output")
        reportBridgeError(new BridgeError("failed", `The ${request.name} interaction answered with an output the producer could not read.`,
          { route: this.scope.route }), this.scope.route);
    } catch (cause) {
      // The presentation's connection closing cancels the request on the producer; a closed transport is expected.
      if (!(cause instanceof BridgeError && (cause.kind === "disconnected" || cause.kind === "released")))
        reportBridgeError(new BridgeError("failed", `The ${request.name} interaction reply could not be delivered.`,
          { cause, route: this.scope.route }), this.scope.route);
    }
  }

  private async handleRequest(request: InteractionRequest): Promise<void> {
    const handler = this.registered.get(identity(request.name, request.contract));
    if (this.disposed || !handler || !this.scope.live()) { await this.reply(request, "cancelled"); return; }
    const controller = new AbortController();
    const expiresAt = Date.parse(request.expiresAt);
    const remaining = Number.isFinite(expiresAt) ? expiresAt - clock.now() : maximumExpiry;
    const deadline = remaining > 0 ? clock.set(() => controller.abort(), Math.min(remaining, maximumExpiry)) : undefined;
    if (remaining <= 0) controller.abort();
    const abortFromHandler = () => controller.abort();
    handler.controller.signal.addEventListener("abort", abortFromHandler, { once: true });
    this.active.set(request.requestId, controller);
    const early = this.cancelled.get(request.requestId);
    if (early !== undefined) {
      clock.clear(early);
      this.cancelled.delete(request.requestId);
      controller.abort();
    }
    try {
      const definition = this.definitions[request.name]!;
      const input = definition.decodeInput(request.input);
      const output = await handler.handle(input, { signal: controller.signal });
      if (this.disposed || !this.scope.live() || controller.signal.aborted || this.registered.get(identity(request.name, request.contract)) !== handler)
        await this.reply(request, "cancelled");
      else await this.reply(request, "answered", definition.encodeOutput(output));
    } catch (error) {
      // The producer receives only "failed"; the handler's error stays local.
      if (!controller.signal.aborted) reportBridgeError(error, this.scope.route);
      await this.reply(request, controller.signal.aborted ? "cancelled" : "failed");
    } finally {
      handler.controller.signal.removeEventListener("abort", abortFromHandler);
      if (deadline !== undefined) clock.clear(deadline);
      if (this.active.get(request.requestId) === controller) this.active.delete(request.requestId);
    }
  }
}

function identity(name: string, contract: string): string { return `${name.length}:${name}${contract.length}:${contract}`; }
