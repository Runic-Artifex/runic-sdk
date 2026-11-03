// View request handlers of a presentation (specs/application/README.md, "View requests"): registered with
// `view-requests` on a connection that uses `view-requests/1`, and called for each `view-request` push once the page
// painted the request's revision. A generated client passes `defineRequests(…)` to `connectView` only when its View
// has view requests.
import { BridgeError } from "./errors.js";
import type { Message, Push, Reply } from "./protocol.js";
import { reportBridgeError } from "./transport.js";

/** What a view request handler receives besides its input. */
export interface BridgeViewRequestContext {
  /**
   * Aborts when the handler is replaced or unregistered, the View is disposed, or the connection is lost. A handler
   * that works asynchronously, such as an animated scroll, stops when it aborts.
   */
  readonly signal: AbortSignal;
}

/**
 * Handles one view request of a View: a one-way presentation action the ViewModel raised, such as focusing a field
 * or showing a toast. It runs once the page painted the state the request was raised with, so the elements of that
 * state are rendered. Its return value is ignored; a throw or rejection is reported with `reportError`.
 */
export type BridgeViewRequestHandler<TInput> = (input: TInput, context: BridgeViewRequestContext) => void | Promise<void>;

/** One view request of a generated client: `client.requests.<name>`. */
export interface ViewRequestSurface<TInput = never> {
  /**
   * Registers the handler of this view request for this client, replacing the one registered before (whose signal
   * aborts). Returns the function that unregisters it; it does nothing once another handler replaced this one.
   * A request is never replayed: one raised while no handler is registered is not delivered later.
   */
  handle(handler: BridgeViewRequestHandler<TInput>): () => void;
}

/** Generated decoder and contract of one view request. */
export interface ViewRequestDefinition {
  /** The contract of the request's input, `{fingerprint}:request:{Property}`. */
  readonly contract: string;
  /** Decodes the wire input; throws for an invalid one. */
  readonly decodeInput: (value: unknown) => unknown;
}

/** The presentation one set of view request handlers belongs to. Created by `connectView`. */
export interface ViewRequestScope {
  readonly route: string;
  /** The presentation id, which stays the same across reconnects. */
  readonly presentation: string;
  /** The presentation is not disposed and its route is open. */
  readonly live: () => boolean;
  /** Whether the current connection uses `view-requests/1`. */
  readonly enabled: () => boolean;
  /** The current connection's number, which changes with each new connection. */
  readonly connection: () => number;
  /** Sends a request of the route. */
  request(message: Message): Promise<Reply>;
  /** Registers a handler for `view-request` pushes of the route. */
  onPush(kind: "view-request", handler: (push: Push) => void): () => void;
  /**
   * Resolves once the page painted the route at `revision` or later: the route reached it, its state was delivered to
   * subscribers and one animation frame passed (README, "Painted states", steps 1 to 3).
   */
  painted(revision: number): Promise<void>;
}

/** The view request handlers of one presentation. */
export interface ViewRequestSession {
  readonly surface: Readonly<Record<string, ViewRequestSurface>>;
  /** The presentation was opened on the current connection. */
  opened(): void;
  /** The connection closed. */
  paused(): void;
  /** Resolves once the producer answered the latest registration; never rejects. */
  ready(): Promise<void>;
  dispose(): void;
}

/**
 * The view requests of one generated client, passed to `connectView` as `requests`. A View without view requests does
 * not bundle this code.
 */
export type BridgeViewRequests = (scope: ViewRequestScope) => ViewRequestSession;

/** Binds the view request decoders of a generated client to the view request protocol. */
export function defineRequests(definitions: Readonly<Record<string, ViewRequestDefinition>>): BridgeViewRequests {
  return scope => new ViewRequestRuntime(scope, definitions);
}

type Registered = {
  readonly name: string;
  readonly controller: AbortController;
  readonly handle: BridgeViewRequestHandler<unknown>;
};
type ViewRequest = Extract<Push, { kind: "view-request" }>;

class ViewRequestRuntime implements ViewRequestSession {
  readonly surface: Readonly<Record<string, ViewRequestSurface>>;
  private readonly registered = new Map<string, Registered>();
  /** The generation of the latest registration, increasing for the life of the client. */
  private generation = 0;
  /** The connection the presentation is open on, or undefined while it is not. */
  private openOn: number | undefined;
  private disposed = false;
  /** The requests the producer was last asked to register on this connection, as JSON. */
  private sent: string | undefined;
  private registration: Promise<void> = Promise.resolve();
  /** Requests wait for their paint in arrival order. */
  private delivery: Promise<void> = Promise.resolve();
  /** The signals of handlers that run. */
  private readonly active = new Set<AbortController>();
  private readonly stop: () => void;

  constructor(private readonly scope: ViewRequestScope, private readonly definitions: Readonly<Record<string, ViewRequestDefinition>>) {
    const surface: Record<string, ViewRequestSurface> = {};
    for (const name of Object.keys(definitions))
      surface[name] = { handle: handler => this.register(name, handler as BridgeViewRequestHandler<unknown>) };
    this.surface = surface;
    this.stop = scope.onPush("view-request", push => { if (push["presentation"] === scope.presentation) this.receive(push as ViewRequest); });
  }

  opened(): void {
    this.openOn = this.scope.connection();
    this.sent = undefined;
    this.sync();
  }

  paused(): void {
    this.openOn = undefined;
    this.sent = undefined;
    this.abortActive();
  }

  async ready(): Promise<void> {
    await this.registration.catch(() => undefined);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.stop();
    for (const handler of this.registered.values()) handler.controller.abort();
    this.registered.clear();
    this.abortActive();
    this.openOn = undefined;
  }

  private isOpen(): boolean {
    return !this.disposed && this.openOn !== undefined && this.openOn === this.scope.connection() && this.scope.live();
  }

  private register(name: string, handle: BridgeViewRequestHandler<unknown>): () => void {
    if (this.disposed || !this.scope.live()) throw new BridgeError("released", "The View of this view request was released.", { route: this.scope.route });
    if (typeof handle !== "function") throw new TypeError("A view request handler is a function.");
    this.registered.get(name)?.controller.abort();
    const handler: Registered = { name, controller: new AbortController(), handle };
    this.registered.set(name, handler);
    this.sync();
    return () => {
      if (this.registered.get(name) !== handler) return;
      this.registered.delete(name);
      handler.controller.abort();
      this.sync();
    };
  }

  private requests(): { readonly name: string; readonly contract: string }[] {
    return [...this.registered.keys()].map(name => ({ name, contract: this.definitions[name]!.contract }));
  }

  /**
   * Registers the current handlers with the next generation, after the registration before it, when the presentation
   * is open on a connection that uses the feature. Nothing is sent before the first handler, or when the requests did
   * not change.
   */
  private sync(): void {
    if (!this.isOpen() || !this.scope.enabled()) return;
    const requests = this.requests();
    const text = JSON.stringify(requests);
    if (this.sent === undefined ? requests.length === 0 : this.sent === text) return;
    this.sent = text;
    const generation = ++this.generation;
    const connection = this.openOn;
    this.registration = this.registration.then(async () => {
      // A newer registration is on its way, or the connection changed: the new one registers what is current.
      if (generation !== this.generation || !this.isOpen() || this.openOn !== connection) return;
      let reply: Reply;
      try {
        reply = await this.scope.request({ kind: "view-requests", route: this.scope.route, presentation: this.scope.presentation, generation, requests });
      } catch (cause) {
        if (cause instanceof BridgeError && (cause.kind === "disconnected" || cause.kind === "released") || !this.isOpen()) return;
        reportBridgeError(cause instanceof BridgeError ? cause
          : new BridgeError("failed", "The view request handlers could not be registered.", { cause, route: this.scope.route }), this.scope.route);
        return;
      }
      if (!reply.ok) {
        if (reply.error?.kind !== "released" && this.isOpen())
          reportBridgeError(new BridgeError("failed", reply.error?.message ?? "The view request handlers could not be registered.",
            { route: this.scope.route }), this.scope.route);
        return;
      }
      // `stale`: a newer registration is on its way; `ignored`: the presentation closed on this connection meanwhile.
      if (reply["registration"] === "unsupported")
        reportBridgeError(new BridgeError("failed",
          `The producer does not support the view requests ${requests.map(request => request.name).join(", ")} of ${this.scope.route}: ` +
          "the page and the .NET host were generated from different contracts.", { route: this.scope.route }), this.scope.route);
    });
  }

  private abortActive(): void {
    for (const controller of this.active) controller.abort();
    this.active.clear();
  }

  /** A `view-request` push: delivered after the page painted its revision, unless it no longer has a handler. */
  private receive(request: ViewRequest): void {
    const definition = Object.hasOwn(this.definitions, request.name) ? this.definitions[request.name] : undefined;
    if (!definition || definition.contract !== request.contract || !this.registered.has(request.name) || !this.isOpen()) return;
    const connection = this.openOn;
    const painted = this.delivery.then(() => this.scope.painted(request.revision));
    this.delivery = painted.catch(() => undefined);
    void painted.then(() => {
      // A request is lost with its connection, and ignored once its handler or presentation is gone.
      if (!this.isOpen() || this.openOn !== connection) return;
      const handler = this.registered.get(request.name);
      if (handler) this.run(handler, definition, request.input);
    }, () => undefined);
  }

  private run(handler: Registered, definition: ViewRequestDefinition, wire: unknown): void {
    let input: unknown;
    try { input = definition.decodeInput(wire); }
    catch (cause) {
      reportBridgeError(new BridgeError("failed", `The ${handler.name} view request has an invalid input: ${cause instanceof Error ? cause.message : String(cause)}`,
        { cause, route: this.scope.route }), this.scope.route);
      return;
    }
    const controller = new AbortController();
    const abort = () => controller.abort();
    handler.controller.signal.addEventListener("abort", abort, { once: true });
    this.active.add(controller);
    const done = () => {
      handler.controller.signal.removeEventListener("abort", abort);
      this.active.delete(controller);
    };
    let result: unknown;
    try { result = handler.handle(input, { signal: controller.signal }); }
    catch (error) { done(); reportBridgeError(error, this.scope.route); return; }
    if (result !== null && typeof result === "object" && typeof (result as PromiseLike<unknown>).then === "function")
      Promise.resolve(result).then(done, error => {
        // A handler that stops because its signal aborted is not a failure.
        if (!controller.signal.aborted) reportBridgeError(error, this.scope.route);
        done();
      });
    else done();
  }
}
