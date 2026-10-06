import { BridgeError } from "./errors.js";
import { reportBridgeError, type RunicBridgeClient } from "./transport.js";

export interface BridgeInteractionContext {
  /** Aborts when .NET cancels the request, the handler is replaced, or the View is disposed. */
  readonly signal: AbortSignal;
}

/** Generated codec and contract of one ReactiveUI interaction. */
export interface InteractionDefinition {
  readonly contract: string;
  readonly decodeInput: (value: unknown) => unknown;
  readonly encodeOutput: (value: unknown) => unknown;
}

export interface InteractionSurface {
  handle(handler: (input: never, context: BridgeInteractionContext) => unknown): () => void;
}

interface InteractionScope {
  readonly bridge: RunicBridgeClient;
  readonly route: string;
  readonly presentationId: string | undefined;
  /** The lease is not disposed and its route belongs to the current Bridge session. */
  readonly live: () => boolean;
}

type InteractionHandler = {
  readonly name: string;
  readonly contract: string;
  readonly generation: number;
  readonly controller: AbortController;
  readonly handle: (input: unknown, context: BridgeInteractionContext) => unknown;
};
type InteractionRequest = {
  readonly requestId: string; readonly route: string; readonly presentationId: string;
  readonly ownerEpoch: number; readonly name: string; readonly contract: string;
};

/**
 * Browser half of the interaction protocol for one mounted presentation. It
 * advertises only active handlers, so an unrendered control preserves the
 * normal .NET interaction fallback.
 */
export class InteractionRuntime {
  readonly surface: Readonly<Record<string, InteractionSurface>>;
  private readonly handlers = new Map<string, InteractionHandler>();
  private generation = 0;
  private capabilityGeneration = 0;
  private disposed = false;
  private unavailable = false;
  private loop: Promise<void> | undefined;
  private controlLoop: Promise<void> | undefined;
  private capabilitySync: Promise<void> = Promise.resolve();
  private retry: Promise<void> | undefined;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private retryResolve: (() => void) | undefined;
  private retryDelay = 25;
  private readonly activeRequests = new Map<string, AbortController>();
  private readonly cancelledRequests = new Map<string, ReturnType<typeof setTimeout>>();

  constructor(private readonly scope: InteractionScope, private readonly definitions: Readonly<Record<string, InteractionDefinition>>) {
    const surface: Record<string, InteractionSurface> = {};
    for (const name of Object.keys(definitions)) surface[name] = { handle: handler => this.register(name, handler as InteractionHandler["handle"]) };
    this.surface = surface;
  }

  /** Resolves when .NET acknowledged the registered handlers. */
  async ready(): Promise<void> {
    if (this.handlers.size === 0) return;
    try { await this.capabilitySync; }
    catch (cause) {
      throw new BridgeError(this.scope.bridge.isConnected() ? "failed" : "disconnected", "The interaction handler could not be registered.",
        { cause, route: "__runicInteractionControl" });
    }
    if (this.unavailable) throw new BridgeError("disconnected", "The interaction presentation is no longer available.");
  }

  dispose(): void {
    this.disposed = true;
    this.stopLoops();
    for (const handler of this.handlers.values()) handler.controller.abort();
    this.handlers.clear();
    this.syncCapabilities();
  }

  private running(): boolean {
    return !this.disposed && !this.unavailable && this.scope.live() && this.scope.bridge.isConnected() && this.handlers.size !== 0;
  }

  private contract(name: string): string {
    const definition = Object.hasOwn(this.definitions, name) ? this.definitions[name] : undefined;
    if (!definition) throw new BridgeError("failed", "Unknown interaction surface.");
    return definition.contract;
  }

  private register(name: string, handle: InteractionHandler["handle"]): () => void {
    if (this.disposed || this.unavailable || !this.scope.live() || !this.scope.bridge.isConnected())
      throw new BridgeError("disconnected", "The interaction view is disconnected.");
    const key = identity(name, this.contract(name));
    this.handlers.get(key)?.controller.abort();
    const registered: InteractionHandler = { name, contract: this.contract(name), generation: ++this.generation, controller: new AbortController(), handle };
    this.handlers.set(key, registered);
    this.syncCapabilities();
    this.ensureLoops();
    return () => {
      if (this.handlers.get(key) !== registered) return;
      this.handlers.delete(key);
      registered.controller.abort();
      this.syncCapabilities();
    };
  }

  private async reply(request: InteractionRequest, kind: "answered" | "cancelled" | "failed", output?: unknown): Promise<void> {
    const payload: Record<string, unknown> = { kind, requestId: request.requestId, route: request.route, presentationId: request.presentationId,
      ownerEpoch: request.ownerEpoch, name: request.name, contract: request.contract };
    if (kind === "answered") payload["output"] = output;
    try { await this.scope.bridge.call("__runicInteractionReply", jsonForInteraction(payload)); }
    catch (cause) {
      // The presentation lifecycle cancels the request on .NET; a closed transport is expected here.
      if (this.scope.bridge.isConnected())
        reportBridgeError(new BridgeError("failed", `The ${request.name} interaction reply could not be delivered.`,
          { cause, route: "__runicInteractionReply" }));
    }
  }

  private abortActive(): void {
    for (const controller of this.activeRequests.values()) controller.abort();
    this.activeRequests.clear();
    for (const timer of this.cancelledRequests.values()) clearTimeout(timer);
    this.cancelledRequests.clear();
  }

  private cancelRequest(requestId: string): void {
    const active = this.activeRequests.get(requestId);
    if (active) { active.abort(); return; }
    if (this.cancelledRequests.has(requestId)) return;
    if (this.cancelledRequests.size >= 32) {
      const oldest = this.cancelledRequests.keys().next().value;
      if (oldest !== undefined) {
        const timer = this.cancelledRequests.get(oldest);
        if (timer) clearTimeout(timer);
        this.cancelledRequests.delete(oldest);
      }
    }
    this.cancelledRequests.set(requestId, setTimeout(() => this.cancelledRequests.delete(requestId), 120_000));
  }

  private stopLoops(): void { this.unavailable = true; this.abortActive(); this.retryResolve?.(); }

  private waitForRetry(): Promise<void> {
    if (this.retry) return this.retry;
    const delay = this.retryDelay;
    this.retryDelay = Math.min(this.retryDelay * 2, 500);
    this.retry = new Promise<void>(resolve => {
      const finish = () => {
        if (this.retryTimer) clearTimeout(this.retryTimer);
        this.retryTimer = undefined; this.retryResolve = undefined; this.retry = undefined;
        resolve();
      };
      this.retryResolve = finish;
      this.retryTimer = setTimeout(finish, delay);
    });
    return this.retry;
  }

  private syncCapabilities(): void {
    const presentationId = this.scope.presentationId;
    if (!presentationId) return;
    const generation = ++this.capabilityGeneration;
    const handlers = [...this.handlers.values()].map(handler => ({ name: handler.name, contract: handler.contract }));
    this.capabilitySync = this.capabilitySync.catch(() => undefined).then(async () => {
      if (this.disposed || this.unavailable || !this.scope.live() || !this.scope.bridge.isConnected()) return;
      const reply = JSON.parse(await this.scope.bridge.call("__runicInteractionControl",
        jsonForInteraction({ route: this.scope.route, presentationId, generation, handlers }))) as { kind?: unknown };
      if (reply?.kind !== "ok") {
        this.unavailable = true;
        this.abortActive();
        throw new BridgeError("disconnected", "The interaction presentation is no longer available.");
      }
    });
    void this.capabilitySync.catch(() => undefined);
  }

  private async handleRequest(request: Record<string, unknown>): Promise<void> {
    if (typeof request["requestId"] !== "string" || typeof request["route"] !== "string" || typeof request["presentationId"] !== "string"
      || typeof request["ownerEpoch"] !== "number" || !Number.isSafeInteger(request["ownerEpoch"]) || typeof request["name"] !== "string"
      || typeof request["contract"] !== "string") return;
    const requestIdentity: InteractionRequest = { requestId: request["requestId"], route: request["route"], presentationId: request["presentationId"],
      ownerEpoch: request["ownerEpoch"], name: request["name"], contract: request["contract"] };
    const key = identity(request["name"], request["contract"]);
    const handler = this.handlers.get(key);
    if (!handler || request["route"] !== this.scope.route || request["presentationId"] !== this.scope.presentationId) {
      await this.reply(requestIdentity, "cancelled");
      return;
    }
    const controller = new AbortController();
    const expiresAt = typeof request["expiresAt"] === "string" ? Date.parse(request["expiresAt"]) : Number.NaN;
    const deadline = Number.isFinite(expiresAt) && expiresAt > Date.now()
      ? setTimeout(() => controller.abort(), Math.min(expiresAt - Date.now(), 600_000)) : undefined;
    const abortFromHandler = () => controller.abort();
    handler.controller.signal.addEventListener("abort", abortFromHandler, { once: true });
    this.activeRequests.set(requestIdentity.requestId, controller);
    if (Number.isFinite(expiresAt) && expiresAt <= Date.now()) controller.abort();
    const cancelledBeforeDelivery = this.cancelledRequests.get(requestIdentity.requestId);
    if (cancelledBeforeDelivery) {
      clearTimeout(cancelledBeforeDelivery);
      this.cancelledRequests.delete(requestIdentity.requestId);
      controller.abort();
    }
    try {
      const definition = this.definitions[request["name"]];
      if (!definition || !Object.hasOwn(this.definitions, request["name"])) throw new BridgeError("failed", "Unknown interaction input.");
      const input = definition.decodeInput(request["input"]);
      const output = await handler.handle(input, { signal: controller.signal });
      if (this.disposed || !this.scope.live() || controller.signal.aborted || this.handlers.get(key) !== handler)
        await this.reply(requestIdentity, "cancelled");
      else await this.reply(requestIdentity, "answered", definition.encodeOutput(output));
    } catch (error) {
      // .NET receives only "failed"; the handler's error stays local.
      if (!controller.signal.aborted) reportBridgeError(error, `${this.scope.route}:${request["name"]}`);
      await this.reply(requestIdentity, controller.signal.aborted ? "cancelled" : "failed");
    } finally {
      handler.controller.signal.removeEventListener("abort", abortFromHandler);
      if (deadline) clearTimeout(deadline);
      if (this.activeRequests.get(requestIdentity.requestId) === controller) this.activeRequests.delete(requestIdentity.requestId);
    }
  }

  private async runLoop(): Promise<void> {
    const presentationId = this.scope.presentationId;
    if (!presentationId) return;
    while (this.running()) {
      try { await this.capabilitySync; }
      catch {
        // ready() passes this failure to the command that awaits it; the loop retries.
        if (this.unavailable || !this.scope.bridge.isConnected()) { this.stopLoops(); return; }
        await this.waitForRetry();
        this.syncCapabilities();
        continue;
      }
      if (!this.running()) return;
      const handlers = [...this.handlers.values()].map(handler => ({ name: handler.name, contract: handler.contract }));
      let envelope: unknown;
      try {
        envelope = JSON.parse(await this.scope.bridge.call("__runicInteractionWait",
          jsonForInteraction({ route: this.scope.route, presentationId, generation: this.capabilityGeneration, handlers })));
      } catch {
        // A dropped long poll is retried with backoff; a closed transport ends the loop.
        if (!this.scope.bridge.isConnected()) { this.stopLoops(); return; }
        await this.waitForRetry();
        continue;
      }
      if (!this.running()) return;
      this.retryDelay = 25;
      if (envelope === null || typeof envelope !== "object") continue;
      const request = envelope as Record<string, unknown>;
      if (request["kind"] === "disconnected" || request["kind"] === "ignored" || request["kind"] === "unsupported"
        || request["kind"] === "invalid-request" || request["kind"] === "cancelled") { this.stopLoops(); return; }
      if (request["kind"] === "request") void this.handleRequest(request);
    }
  }

  private async runControlLoop(): Promise<void> {
    const presentationId = this.scope.presentationId;
    if (!presentationId) return;
    while (this.running()) {
      let envelope: unknown;
      try {
        envelope = JSON.parse(await this.scope.bridge.call("__runicInteractionControlWait",
          jsonForInteraction({ route: this.scope.route, presentationId })));
      } catch {
        // A dropped long poll is retried with backoff; a closed transport ends the loop.
        if (!this.scope.bridge.isConnected()) { this.stopLoops(); return; }
        await this.waitForRetry();
        continue;
      }
      if (!this.running()) return;
      this.retryDelay = 25;
      if (envelope === null || typeof envelope !== "object") continue;
      const control = envelope as Record<string, unknown>;
      if (control["kind"] === "disconnected" || control["kind"] === "ignored" || control["kind"] === "invalid-request"
        || (control["kind"] === "cancelled" && typeof control["requestId"] !== "string")) { this.stopLoops(); return; }
      if (control["kind"] === "cancelled" && typeof control["requestId"] === "string") this.cancelRequest(control["requestId"]);
    }
  }

  private ensureLoops(): void {
    if (this.disposed || this.unavailable || !this.scope.live() || this.handlers.size === 0) return;
    this.loop ??= this.runLoop().finally(() => { this.loop = undefined; if (this.running()) this.ensureLoops(); });
    this.controlLoop ??= this.runControlLoop().finally(() => { this.controlLoop = undefined; if (this.running()) this.ensureLoops(); });
  }
}

function identity(name: string, contract: string): string { return `${name.length}:${name}${contract.length}:${contract}`; }

function jsonForInteraction(value: unknown): string {
  const json = JSON.stringify(value, (_key, item) => typeof item === "bigint" ? item.toString() : item);
  if (json === undefined) throw new BridgeError("failed", "The interaction payload is not JSON serializable.");
  return json;
}
