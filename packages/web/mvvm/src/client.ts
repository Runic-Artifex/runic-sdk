import {
  CAPABILITIES,
  type CapabilityName,
  type ClientMessage,
  type FaultCode,
  type HostMessage,
  type HostPatchMessage,
  type JsonValue,
  type PatchChange,
  PROTOCOL_LIMITS,
  type ProtocolLimits,
  type Revision,
  type SnapshotState,
  type Uuid,
} from "./protocol.js";
import { ProtocolTransport, type TransportEvent } from "./transport.js";

export type MvvmClientPhase =
  | "idle"
  | "handshaking"
  | "opening"
  | "connected"
  | "recovering"
  | "disconnected"
  | "closing"
  | "closed"
  | "failed";

export interface CommandState {
  readonly canExecute: boolean;
  readonly isExecuting: boolean;
}

export interface ClientSnapshot {
  readonly phase: MvvmClientPhase;
  readonly synchronized: boolean;
  readonly revision: Revision | null;
  readonly properties: ReadonlyMap<number, JsonValue>;
  readonly collections: ReadonlyMap<number, readonly JsonValue[]>;
  readonly commands: ReadonlyMap<number, CommandState>;
  readonly validation: ReadonlyMap<number, readonly string[]>;
}

export type MvvmClientEvent =
  | { readonly type: "state"; readonly snapshot: ClientSnapshot }
  | { readonly type: "fault"; readonly error: MvvmFaultError }
  | { readonly type: "protocolError"; readonly error: ClientProtocolError };

export interface MvvmClientOptions {
  readonly requestIdFactory?: () => Uuid;
  readonly capabilities?: readonly CapabilityName[];
}

export interface MutationResult {
  readonly request: Uuid;
  readonly revision: Revision;
}

export interface CommandResult<T extends JsonValue = JsonValue> extends MutationResult {
  readonly valuePresent: boolean;
  readonly value?: T;
}

export interface CancelResult extends MutationResult {
  readonly targetRequest: Uuid;
  readonly accepted: boolean;
}

export interface CommandInvocation<T extends JsonValue = JsonValue> {
  readonly request: Uuid;
  readonly completion: Promise<CommandResult<T>>;
  cancel(): Promise<CancelResult>;
}

export class MvvmFaultError extends Error {
  public constructor(
    public readonly code: FaultCode,
    message: string,
    public readonly retryable: boolean,
    public readonly currentRevision?: Revision,
    public readonly snapshotRequired?: boolean,
  ) {
    super(message);
    this.name = "MvvmFaultError";
  }
}

export class ClientProtocolError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = "ClientProtocolError";
  }
}

export class MvvmDisconnectedError extends Error {
  public readonly outcomeUnknown = true;
  public constructor() {
    super("The MVVM transport disconnected; an in-flight mutation may have committed.");
    this.name = "MvvmDisconnectedError";
  }
}

interface Deferred<T> {
  readonly promise: Promise<T>;
  resolve(value: T): void;
  reject(reason: unknown): void;
}

interface PendingRequest {
  readonly operation: "setProperty" | "execute" | "cancel" | "ack" | "snapshot" | "close";
  readonly deferred: Deferred<unknown>;
  readonly baseRevision?: Revision;
  readonly targetRequest?: Uuid;
}

interface HandshakeFlow {
  readonly request: Uuid;
  readonly intent: "open" | "reconnect";
  readonly completion: Deferred<ClientSnapshot>;
}

export class MvvmClient {
  private readonly listeners = new Set<(event: MvvmClientEvent) => void>();
  private readonly requestIds = new Set<Uuid>();
  private readonly requestIdFactory: () => Uuid;
  private readonly offeredCapabilities: readonly CapabilityName[];
  private selectedCapabilities = new Set<CapabilityName>();
  private limits: ProtocolLimits = {
    maxFrameBytes: PROTOCOL_LIMITS.maxFrameBytes,
    maxJsonDepth: PROTOCOL_LIMITS.maxJsonDepth,
    maxSessions: PROTOCOL_LIMITS.maxSessions,
    maxPendingRequests: PROTOCOL_LIMITS.maxPendingRequests,
    maxSnapshotMembers: PROTOCOL_LIMITS.maxSnapshotMembers,
    maxPatchChanges: PROTOCOL_LIMITS.maxPatchChanges,
    maxCollectionItems: PROTOCOL_LIMITS.maxCollectionItems,
    commandTimeoutMilliseconds: PROTOCOL_LIMITS.defaultCommandTimeoutMilliseconds,
  };
  private phase: MvvmClientPhase = "idle";
  private contract: string | undefined;
  private session: Uuid | undefined;
  private view: Uuid | undefined;
  private capability: string | undefined;
  private revision: Revision | null = null;
  private properties = new Map<number, JsonValue>();
  private collections = new Map<number, readonly JsonValue[]>();
  private commands = new Map<number, CommandState>();
  private validation = new Map<number, readonly string[]>();
  private readonly pending = new Map<Uuid, PendingRequest>();
  private readonly completed = new Set<Uuid>();
  private readonly completedOrder: Uuid[] = [];
  private handshake: HandshakeFlow | undefined;
  private lastPatch: { from: Revision; to: Revision; raw: Uint8Array } | undefined;
  private mutationTail: Promise<void> = Promise.resolve();

  public constructor(
    private readonly transport: ProtocolTransport,
    options: MvvmClientOptions = {},
  ) {
    this.requestIdFactory = options.requestIdFactory ?? defaultRequestId;
    const capabilities = options.capabilities ?? CAPABILITIES;
    this.offeredCapabilities = Object.freeze([...new Set(capabilities)].sort());
    this.transport.subscribe((event) => this.onTransportEvent(event));
  }

  public get state(): ClientSnapshot {
    return this.createSnapshot();
  }

  public subscribe(listener: (event: MvvmClientEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  public start(contract: string, view: Uuid): Promise<ClientSnapshot> {
    if (this.phase !== "idle") throw new ClientProtocolError("The MVVM client has already been started.");
    this.contract = contract;
    this.view = view;
    return this.startHandshake("open");
  }

  /** Re-handshakes an already rebound transport, then recovers the existing session by snapshot. */
  public reconnect(): Promise<ClientSnapshot> {
    if (this.phase !== "disconnected" || !this.session || !this.view || !this.capability) {
      throw new ClientProtocolError("Only a disconnected open session can reconnect.");
    }
    return this.startHandshake("reconnect");
  }

  public setProperty(member: number, value: JsonValue): Promise<MutationResult> {
    const request = this.nextRequestId();
    return this.enqueueMutation(request, async () => {
      const deferred = createDeferred<MutationResult>();
      const baseRevision = this.requireRevision();
      this.addPending(request, { operation: "setProperty", deferred: deferred as Deferred<unknown>, baseRevision });
      await this.sendSessionMessage({
        v: 1,
        kind: "setProperty",
        ...this.mutationIdentity(request, baseRevision),
        payload: { member, value },
      });
      return deferred.promise;
    });
  }

  public execute<T extends JsonValue = JsonValue>(
    member: number,
    options: Readonly<{ argument?: JsonValue }> = {},
  ): CommandInvocation<T> {
    const request = this.nextRequestId();
    const dispatched = createDeferred<void>();
    void dispatched.promise.catch(() => undefined);
    let cancellation: Promise<CancelResult> | undefined;
    const completion = this.enqueueMutation(request, async () => {
      const deferred = createDeferred<CommandResult<T>>();
      const baseRevision = this.requireRevision();
      this.addPending(request, { operation: "execute", deferred: deferred as Deferred<unknown>, baseRevision });
      const payload: { member: number; argument?: JsonValue } = { member };
      if (Object.prototype.hasOwnProperty.call(options, "argument")) payload.argument = options.argument!;
      await this.sendSessionMessage({
        v: 1,
        kind: "execute",
        ...this.mutationIdentity(request, baseRevision),
        payload,
      });
      dispatched.resolve(undefined);
      return deferred.promise;
    }).catch((error) => {
      dispatched.reject(error);
      throw error;
    });
    return { request, completion, cancel: () => cancellation ??= this.cancelAfter(request, dispatched.promise) };
  }

  public cancel(targetRequest: Uuid): Promise<CancelResult> {
    return this.cancelAfter(targetRequest, Promise.resolve());
  }

  private async cancelAfter(targetRequest: Uuid, dispatched: Promise<void>): Promise<CancelResult> {
    this.requireConnected();
    if (!this.selectedCapabilities.has("cancellation")) {
      throw new ClientProtocolError("Cancellation was not negotiated for this connection.");
    }
    await dispatched;
    this.requireConnected();
    const request = this.nextRequestId();
    const deferred = createDeferred<CancelResult>();
    this.addPending(request, { operation: "cancel", deferred: deferred as Deferred<unknown>, targetRequest });
    void this.sendSessionMessage({
      v: 1,
      kind: "cancel",
      ...this.sessionIdentity(request),
      payload: { targetRequest },
    }).catch((error) => this.rejectPending(request, error));
    return deferred.promise;
  }

  public acknowledge(revision: Revision = this.requireRevision()): Promise<MutationResult> {
    this.requireConnected();
    if (revision > this.requireRevision()) throw new ClientProtocolError("Cannot acknowledge beyond the local revision.");
    const request = this.nextRequestId();
    const deferred = createDeferred<MutationResult>();
    this.addPending(request, { operation: "ack", deferred: deferred as Deferred<unknown> });
    void this.sendSessionMessage({
      v: 1,
      kind: "ack",
      ...this.sessionIdentity(request),
      payload: { revision },
    }).catch((error) => this.rejectPending(request, error));
    return deferred.promise;
  }

  public requestSnapshot(): Promise<ClientSnapshot> {
    this.requireConnected();
    return this.sendSnapshotRequest();
  }

  private sendSnapshotRequest(): Promise<ClientSnapshot> {
    const request = this.nextRequestId();
    const deferred = createDeferred<ClientSnapshot>();
    this.addPending(request, { operation: "snapshot", deferred: deferred as Deferred<unknown> });
    void this.sendSessionMessage({
      v: 1,
      kind: "requestSnapshot",
      ...this.sessionIdentity(request),
      payload: {},
    }).catch((error) => this.rejectPending(request, error));
    return deferred.promise;
  }

  public close(reason?: string): Promise<void> {
    if (this.phase === "closed") return Promise.resolve();
    this.requireConnected();
    this.setPhase("closing");
    const request = this.nextRequestId();
    const deferred = createDeferred<void>();
    this.addPending(request, { operation: "close", deferred: deferred as Deferred<unknown> });
    const payload: { reason?: string } = {};
    if (reason !== undefined) payload.reason = reason;
    void this.sendSessionMessage({
      v: 1,
      kind: "close",
      ...this.sessionIdentity(request),
      payload,
    }).catch((error) => this.rejectPending(request, error));
    return deferred.promise;
  }

  private startHandshake(intent: "open" | "reconnect"): Promise<ClientSnapshot> {
    if (this.transport.state !== "connected") throw new ClientProtocolError("The MVVM transport is not connected.");
    this.transport.resetLimits();
    const request = this.nextRequestId();
    const completion = createDeferred<ClientSnapshot>();
    this.handshake = { request, intent, completion };
    this.setPhase(intent === "open" ? "handshaking" : "recovering");
    void this.transport
      .send({
        v: 1,
        kind: "handshake",
        request,
        payload: { supportedVersions: [1], capabilities: this.offeredCapabilities },
      })
      .catch((error) => {
        this.handshake = undefined;
        completion.reject(error);
        this.setPhase("disconnected");
      });
    return completion.promise;
  }

  private onTransportEvent(event: TransportEvent): void {
    if (event.type === "message") {
      void this.onMessage(event.message, event.rawFrame).catch((error) => this.fail(error));
      return;
    }
    if (event.type === "state" && event.current === "disconnected") this.onDisconnected();
    if (event.type === "protocolError" && this.transport.state === "faulted") this.fail(event.error);
  }

  private async onMessage(message: HostMessage, rawFrame: Uint8Array): Promise<void> {
    if (message.kind === "handshakeResult") {
      await this.onHandshakeResult(message);
      return;
    }
    if (message.kind === "fault" && !("session" in message)) {
      if (!this.handshake || message.request !== this.handshake.request) throw this.protocol("Unexpected pre-session fault.");
      const error = faultError(message.payload);
      this.handshake.completion.reject(error);
      this.handshake = undefined;
      this.setPhase("failed");
      return;
    }
    if (message.kind === "opened") {
      this.verifyOpeningMessage(message);
      this.session = message.session;
      this.capability = message.capability;
      this.applySnapshot(message.payload.snapshot);
      this.setPhase("connected");
      const flow = this.handshake!;
      this.handshake = undefined;
      flow.completion.resolve(this.createSnapshot());
      return;
    }
    this.verifySessionIdentity(message);
    switch (message.kind) {
      case "patch":
        this.onPatch(message, rawFrame);
        return;
      case "snapshot": {
        const pending = this.requirePending(message.request, "snapshot");
        const reconnect = this.handshake?.intent === "reconnect";
        this.applySnapshot(message.payload);
        this.finishPending(message.request);
        this.setPhase("connected");
        pending.deferred.resolve(this.createSnapshot());
        const flow = this.handshake;
        if (flow?.intent === "reconnect") {
          this.handshake = undefined;
          flow.completion.resolve(this.createSnapshot());
        }
        return;
      }
      case "result":
        this.onResult(message);
        return;
      case "fault":
        this.onFault(message.request, message.payload);
        return;
      case "closed": {
        const pending = this.takePending(message.request, "close");
        if (this.revision !== null && message.payload.revision < this.revision)
          throw this.protocol("A close response regressed the revision.");
        this.revision = message.payload.revision;
        this.setPhase("closed");
        pending.deferred.resolve(undefined);
        this.rejectAllPending(new MvvmFaultError("session.closed", "The MVVM session is closed.", false));
        return;
      }
    }
  }

  private async onHandshakeResult(message: Extract<HostMessage, { kind: "handshakeResult" }>): Promise<void> {
    const flow = this.handshake;
    if (!flow || message.request !== flow.request) throw this.protocol("Unexpected handshake result.");
    const selected = message.payload.capabilities;
    if ([...selected].sort().join("\0") !== selected.join("\0") || selected.some((x) => !this.offeredCapabilities.includes(x))) {
      throw this.protocol("The host selected an invalid capability set.");
    }
    this.selectedCapabilities = new Set(selected);
    this.limits = message.payload.limits;
    this.transport.configureLimits(this.limits);
    if (flow.intent === "open") {
      this.setPhase("opening");
      const request = this.nextRequestId();
      this.handshake = { ...flow, request };
      await this.transport.send({
        v: 1,
        kind: "open",
        contract: this.contract!,
        view: this.view!,
        request,
        payload: {},
      });
    } else {
      const request = this.nextRequestId();
      this.addPending(request, { operation: "snapshot", deferred: flow.completion as Deferred<unknown> });
      await this.sendSessionMessage({
        v: 1,
        kind: "requestSnapshot",
        ...this.sessionIdentity(request),
        payload: {},
      });
    }
  }

  private onPatch(message: HostPatchMessage, rawFrame: Uint8Array): void {
    if (this.phase !== "connected" && this.phase !== "closing") return;
    if (!this.selectedCapabilities.has("patches")) throw this.protocol("A patch was received without the patches capability.");
    if (message.payload.changes.length > this.limits.maxPatchChanges) throw this.protocol("A patch exceeds the negotiated change limit.");
    const revision = this.requireRevision();
    const { fromRevision: from, toRevision: to } = message.payload;
    if (to !== from + 1n) {
      this.beginRecovery();
      return;
    }
    if (from < revision) {
      if (this.lastPatch && this.lastPatch.from === from && this.lastPatch.to === to && bytesEqual(this.lastPatch.raw, rawFrame)) return;
      this.beginRecovery();
      return;
    }
    if (from !== revision) {
      this.beginRecovery();
      return;
    }
    this.applyChanges(message.payload.changes);
    this.revision = to;
    this.lastPatch = { from, to, raw: rawFrame.slice() };
    this.notifyState();
  }

  private onResult(message: Extract<HostMessage, { kind: "result" }>): void {
    if (this.completed.has(message.request)) return;
    const operation = message.payload.operation;
    const pending = this.requirePending(message.request, operation === "ack" ? "ack" : operation);
    const revision = message.payload.revision;
    const executeValuePresent = operation === "execute" && Object.prototype.hasOwnProperty.call(message.payload, "value");
    if (executeValuePresent && !this.selectedCapabilities.has("commandResults")) {
      throw this.protocol("A command value was received without the commandResults capability.");
    }
    if (operation === "setProperty" || operation === "execute") this.acceptMutationRevision(revision, pending.baseRevision!);
    else if (revision !== this.requireRevision()) throw this.protocol("A control result has an unexpected revision.");

    if (operation === "execute") {
      const valuePresent = executeValuePresent;
      pending.deferred.resolve({ request: message.request, revision, valuePresent, ...(valuePresent ? { value: message.payload.value } : {}) });
    } else if (operation === "cancel") {
      if (message.payload.targetRequest !== pending.targetRequest) throw this.protocol("A cancel result targeted a different request.");
      pending.deferred.resolve({
        request: message.request,
        revision,
        targetRequest: message.payload.targetRequest,
        accepted: message.payload.accepted,
      });
    } else if (operation === "ack") pending.deferred.resolve({ request: message.request, revision });
    else pending.deferred.resolve({ request: message.request, revision });
    this.finishPending(message.request);
  }

  private onFault(request: Uuid, payload: Extract<HostMessage, { kind: "fault" }>["payload"]): void {
    if (this.completed.has(request)) return;
    const pending = this.pending.get(request);
    if (!pending) throw this.protocol("A fault did not correlate to a pending request.");
    this.pending.delete(request);
    this.rememberCompleted(request);
    const error = faultError(payload);
    pending.deferred.reject(error);
    this.emit({ type: "fault", error });
    if (pending.operation === "snapshot" && this.handshake?.intent === "reconnect") {
      this.handshake.completion.reject(error);
      this.handshake = undefined;
      this.setPhase("disconnected");
      return;
    }
    if (payload.code === "revision.stale" && payload.snapshotRequired) this.beginRecovery();
    if (payload.code === "session.closed") {
      this.setPhase("closed");
      this.rejectAllPending(error);
    }
  }

  private applySnapshot(snapshot: SnapshotState): void {
    if (this.revision !== null && snapshot.revision < this.revision) {
      throw this.protocol("A snapshot regressed the revision.");
    }
    if (snapshot.members.length > this.limits.maxSnapshotMembers) throw this.protocol("A snapshot exceeds the negotiated member limit.");
    const properties = new Map<number, JsonValue>();
    const collections = new Map<number, readonly JsonValue[]>();
    const commands = new Map<number, CommandState>();
    const validation = new Map<number, readonly string[]>();
    const keys = new Set<string>();
    for (const item of snapshot.members) {
      const key = `${item.type}:${item.member}`;
      if (keys.has(key)) throw this.protocol("A snapshot contains a duplicate member entry.");
      keys.add(key);
      switch (item.type) {
        case "property": properties.set(item.member, cloneJson(item.value)); break;
        case "collection":
          this.requireCapability("collections", "A collection snapshot was received without the collections capability.");
          if (item.items.length > this.limits.maxCollectionItems) throw this.protocol("A collection exceeds the negotiated item limit.");
          collections.set(item.member, item.items.map(cloneJson));
          break;
        case "command": commands.set(item.member, { canExecute: item.canExecute, isExecuting: item.isExecuting }); break;
        case "validation":
          this.requireCapability("validation", "Validation state was received without the validation capability.");
          validation.set(item.member, [...item.errors]);
          break;
      }
    }
    this.properties = properties;
    this.collections = collections;
    this.commands = commands;
    this.validation = validation;
    this.revision = snapshot.revision;
    this.lastPatch = undefined;
    this.notifyState();
  }

  private applyChanges(changes: readonly PatchChange[]): void {
    const properties = new Map(this.properties);
    const collections = new Map(this.collections);
    const commands = new Map(this.commands);
    const validation = new Map(this.validation);
    for (const change of changes) {
      switch (change.type) {
        case "property": properties.set(change.member, cloneJson(change.value)); break;
        case "command": commands.set(change.member, { canExecute: change.canExecute, isExecuting: change.isExecuting }); break;
        case "validation":
          this.requireCapability("validation", "Validation state was received without the validation capability.");
          validation.set(change.member, [...change.errors]);
          break;
        case "collection":
          this.requireCapability("collections", "A collection patch was received without the collections capability.");
          this.applyCollectionChange(collections, change);
          break;
        case "collectionMove": {
          this.requireCapability("collections", "A collection patch was received without the collections capability.");
          const items = [...this.requireCollection(collections, change.member)];
          if (change.from + change.count > items.length) throw this.protocol("A collection move is out of range.");
          const moved = items.splice(change.from, change.count);
          if (change.to > items.length) throw this.protocol("A collection move target is out of range.");
          items.splice(change.to, 0, ...moved);
          collections.set(change.member, items);
          break;
        }
      }
    }
    this.properties = properties;
    this.collections = collections;
    this.commands = commands;
    this.validation = validation;
  }

  private applyCollectionChange(
    collections: Map<number, readonly JsonValue[]>,
    change: Extract<PatchChange, { type: "collection" }>,
  ): void {
    if (change.operation === "reset") {
      if (change.index !== 0) throw this.protocol("A collection reset must use index zero.");
      if (change.items.length > this.limits.maxCollectionItems) throw this.protocol("A collection reset exceeds the negotiated item limit.");
      collections.set(change.member, change.items.map(cloneJson));
      return;
    }
    const items = [...this.requireCollection(collections, change.member)];
    const count = change.items.length;
    if (count > this.limits.maxCollectionItems) throw this.protocol("A collection edit exceeds the negotiated item limit.");
    if (change.operation === "insert") {
      if (change.index > items.length) throw this.protocol("A collection insertion is out of range.");
      items.splice(change.index, 0, ...change.items.map(cloneJson));
    } else {
      if (change.index + count > items.length) throw this.protocol("A collection edit is out of range.");
      items.splice(change.index, count, ...(change.operation === "replace" ? change.items.map(cloneJson) : []));
    }
    if (items.length > this.limits.maxCollectionItems) throw this.protocol("A collection patch exceeds the negotiated collection limit.");
    collections.set(change.member, items);
  }

  private requireCollection(collections: Map<number, readonly JsonValue[]>, member: number): readonly JsonValue[] {
    const collection = collections.get(member);
    if (!collection) throw this.protocol("A patch targeted an unknown collection member.");
    return collection;
  }

  private acceptMutationRevision(revision: Revision, baseRevision: Revision): void {
    const local = this.requireRevision();
    const expected = baseRevision + 1n;
    if (revision !== expected || (local !== baseRevision && local !== expected)) {
      this.beginRecovery();
      throw this.protocol("A mutation result has an unexpected revision.");
    }
    if (local === baseRevision) {
      this.revision = revision;
      this.notifyState();
    }
  }

  private beginRecovery(): void {
    if (this.phase === "recovering" || this.phase === "disconnected" || this.phase === "closed") return;
    this.setPhase("recovering");
    void this.sendSnapshotRequest().catch((error) => this.fail(error));
  }

  private verifyOpeningMessage(message: Extract<HostMessage, { kind: "opened" }>): void {
    const flow = this.handshake;
    if (!flow || flow.intent !== "open" || message.request !== flow.request || message.contract !== this.contract || message.view !== this.view) {
      throw this.protocol("The opened response does not match the open request.");
    }
  }

  private verifySessionIdentity(message: Exclude<HostMessage, { kind: "handshakeResult" | "opened" }>): void {
    if (!("session" in message) || message.session !== this.session || message.view !== this.view) {
      throw this.protocol("A host message has an unexpected session identity.");
    }
  }

  private mutationIdentity(request: Uuid, baseRevision: Revision) {
    return { ...this.sessionIdentity(request), baseRevision } as const;
  }

  private sessionIdentity(request: Uuid) {
    this.requireSession();
    return { session: this.session!, view: this.view!, request, capability: this.capability! } as const;
  }

  private async sendSessionMessage(message: ClientMessage): Promise<void> {
    await this.transport.send(message);
  }

  private enqueueMutation<T>(request: Uuid, operation: () => Promise<T>): Promise<T> {
    this.requireConnected();
    const queued = this.mutationTail.then(() => {
      this.requireConnected();
      return operation();
    });
    const result = queued.catch((error) => {
      this.rejectPending(request, error);
      throw error;
    });
    this.mutationTail = result.then(() => undefined, () => undefined);
    return result;
  }

  private takePending(request: Uuid, operation: PendingRequest["operation"]): PendingRequest {
    const pending = this.requirePending(request, operation);
    this.finishPending(request);
    return pending;
  }

  private requirePending(request: Uuid, operation: PendingRequest["operation"]): PendingRequest {
    const pending = this.pending.get(request);
    if (!pending || pending.operation !== operation) throw this.protocol("A terminal response did not match a pending request.");
    return pending;
  }

  private addPending(request: Uuid, pending: PendingRequest): void {
    if (this.pending.size >= this.limits.maxPendingRequests) throw this.protocol("The negotiated pending request limit was exceeded.");
    this.pending.set(request, pending);
  }

  private finishPending(request: Uuid): void {
    this.pending.delete(request);
    this.rememberCompleted(request);
  }

  private rememberCompleted(request: Uuid): void {
    this.completed.add(request);
    this.completedOrder.push(request);
    const maximum = Math.max(1, this.limits.maxPendingRequests * 2);
    while (this.completedOrder.length > maximum) this.completed.delete(this.completedOrder.shift()!);
  }

  private rejectPending(request: Uuid, error: unknown): void {
    const pending = this.pending.get(request);
    if (!pending) return;
    this.pending.delete(request);
    this.rememberCompleted(request);
    pending.deferred.reject(error);
  }

  private rejectAllPending(error: unknown): void {
    for (const [request, { deferred }] of this.pending) {
      deferred.reject(error);
      this.rememberCompleted(request);
    }
    this.pending.clear();
  }

  private onDisconnected(): void {
    if (this.phase === "closed") return;
    const error = new MvvmDisconnectedError();
    this.rejectAllPending(error);
    this.handshake?.completion.reject(error);
    this.handshake = undefined;
    this.setPhase("disconnected");
  }

  private fail(reason: unknown): void {
    const error = reason instanceof ClientProtocolError ? reason : this.protocol("The host violated the MVVM session contract.");
    this.rejectAllPending(error);
    this.handshake?.completion.reject(error);
    this.handshake = undefined;
    this.setPhase("failed");
    this.emit({ type: "protocolError", error });
  }

  private protocol(message: string): ClientProtocolError {
    return new ClientProtocolError(message);
  }

  private requireCapability(capability: CapabilityName, message: string): void {
    if (!this.selectedCapabilities.has(capability)) throw this.protocol(message);
  }

  private requireConnected(): void {
    if (this.phase !== "connected") throw new ClientProtocolError("The MVVM client is not synchronized.");
  }

  private requireSession(): void {
    if (!this.session || !this.view || !this.capability) throw new ClientProtocolError("No MVVM session is open.");
  }

  private requireRevision(): Revision {
    if (this.revision === null) throw new ClientProtocolError("No authoritative revision is available.");
    return this.revision;
  }

  private nextRequestId(): Uuid {
    const request = this.requestIdFactory();
    if (this.requestIds.has(request)) throw new ClientProtocolError("The request ID factory returned a duplicate UUID.");
    this.requestIds.add(request);
    return request;
  }

  private setPhase(phase: MvvmClientPhase): void {
    if (this.phase === phase) return;
    this.phase = phase;
    this.notifyState();
  }

  private createSnapshot(): ClientSnapshot {
    return {
      phase: this.phase,
      synchronized: this.phase === "connected",
      revision: this.revision,
      properties: new Map([...this.properties].map(([key, value]) => [key, cloneJson(value)])),
      collections: new Map([...this.collections].map(([key, value]) => [key, value.map(cloneJson)])),
      commands: new Map([...this.commands].map(([key, value]) => [key, { ...value }])),
      validation: new Map([...this.validation].map(([key, value]) => [key, [...value]])),
    };
  }

  private notifyState(): void {
    this.emit({ type: "state", snapshot: this.createSnapshot() });
  }

  private emit(event: MvvmClientEvent): void {
    for (const listener of [...this.listeners]) {
      try { listener(event); } catch { /* Consumer callbacks cannot corrupt protocol dispatch. */ }
    }
  }
}

function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function faultError(payload: { code: FaultCode; message: string; retryable: boolean; currentRevision?: Revision; snapshotRequired?: boolean }) {
  return new MvvmFaultError(payload.code, payload.message, payload.retryable, payload.currentRevision, payload.snapshotRequired);
}

function bytesEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.byteLength !== right.byteLength) return false;
  let different = 0;
  for (let index = 0; index < left.byteLength; index++) different |= left[index]! ^ right[index]!;
  return different === 0;
}

function cloneJson(value: JsonValue): JsonValue {
  if (value === null || typeof value !== "object") return value;
  if (Array.isArray(value)) return value.map(cloneJson);
  const clone: Record<string, JsonValue> = Object.create(null) as Record<string, JsonValue>;
  for (const key of Object.keys(value)) clone[key] = cloneJson((value as Readonly<Record<string, JsonValue>>)[key]!);
  return clone;
}

function defaultRequestId(): Uuid {
  const runtimeCrypto = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
  if (!runtimeCrypto?.randomUUID) {
    throw new ClientProtocolError("Provide requestIdFactory when the runtime has no cryptographic randomUUID().");
  }
  return runtimeCrypto.randomUUID().toLowerCase();
}
