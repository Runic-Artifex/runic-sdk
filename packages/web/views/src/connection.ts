import { clock, delay, randomId } from "./clock.js";
import { emitBridgeDiagnostic, emitErrorDiagnostic } from "./diagnostics.js";
import { BridgeError } from "./errors.js";
import { bridgeFailure, bridgeSuccess, type BridgeOutcome } from "./outcome.js";
import { isObject, replyError, type Change, type Message, type Push, type Reply } from "./protocol.js";
import { currentRuntime, errorMessage, sharedRuntimeFor, type SharedRuntime } from "./runtime.js";
import { reportBridgeError, waitForBridge } from "./transport.js";
import { publishConnectionState } from "./connection-state.js";
import { abortable, abortReason, isAbort, throwIfAborted } from "./lifetime.js";
import { applySplice, minimumKept, singleSplice, textSpliceFeature } from "./text.js";
import { deferNotification, flushNotifications } from "./notifications.js";
// Interactions, operations and keyed collections are injected by the generated
// clients that use them, so this module imports only their types.
import type { BridgeCollections } from "./collections.js";
import type { BridgeInteractionContext, BridgeInteractions, InteractionSession, InteractionSurface } from "./interactions.js";
import type { BridgeOperationCodec, BridgeOperationRuntimeHandle, BridgeOperations, OperationScope } from "./operations.js";

/** The framework-neutral surface every generated client shares. */
export interface ViewClient<TState = unknown> {
  /**
   * The latest state, with the value of each setter call that is still on its way (specs/application, "Client
   * overlay"): a member shows the latest value the page set until the producer's state includes that edit, or the
   * edit failed. After `dispose()` or a Bridge session change it stays the last state the client exposed.
   *
   * It is the state subscribers last received. The client applies each frame when it arrives and delivers the
   * resulting state once per animation frame (a timer while the page is hidden). A setter call shows its value at
   * once, and a call resolves only after the state it waited for was delivered.
   */
  readonly snapshot: TState;
  /**
   * Delivers the current state, then each change of `snapshot`, until the returned
   * function is called: at most once per animation frame for the producer's
   * frames, and at once for a setter call or before a call resolves. After
   * `dispose()` it delivers the last state once and returns a no-op.
   */
  subscribe(listener: (state: TState) => void): () => void;
  /** Releases this connection. Further calls reject with a `released` BridgeError, or `disconnected` while the Bridge is disconnected. */
  dispose(): void;
}

export type FieldBaseline<T> = { readonly value: T; readonly version: number };
export type FieldWriteOptions<T> = { readonly requestId: string; readonly baseline: FieldBaseline<T> };
export type FieldWriteReceipt<T> =
  | { readonly kind: "applied"; readonly snapshot: FieldBaseline<T>; readonly validation?: string }
  | { readonly kind: "committed-with-error"; readonly snapshot: FieldBaseline<T>; readonly message: string }
  | { readonly kind: "rejected"; readonly message: string }
  | { readonly kind: "conflict"; readonly incoming: FieldBaseline<T>; readonly message: string };

/** Options of a generated `connect<Name>()` function. */
export interface BridgeConnectOptions {
  /**
   * Milliseconds to wait for the host Bridge to connect to .NET before the
   * client rejects with a `timeout` BridgeError. Defaults to 5000.
   */
  readonly timeout?: number | undefined;
  /**
   * Abandons the connection when it aborts before the client is returned: the
   * call rejects with `signal.reason`, an `AbortError` DOMException unless
   * `abort(reason)` gave another one, and releases what it connected. An already
   * aborted signal rejects at once. Aborting after the client was returned has no
   * effect; the call removes its abort listeners when it settles.
   */
  readonly signal?: AbortSignal | undefined;
}

/**
 * One decoder per state member, by wire name: it validates the member's wire value and returns the client value,
 * and throws for an invalid one. A `set` change in a frame decodes only its member.
 */
export type BridgeMemberDecoders<TState> = { readonly [K in keyof TState]-?: (wire: unknown) => TState[K] };

/** How a generated module connects one View route. Generated code is its only intended caller. */
export interface ViewConnectOptions<TState> extends BridgeConnectOptions {
  /** The View's operation contract, `{view}:{fingerprint}`. */
  readonly contract: string;
  /** The route: the root name, or `content{id}` for presented content. */
  readonly route: string;
  /** The state members' decoders, by wire name. Members of a state that are not listed are ignored. */
  readonly members: BridgeMemberDecoders<TState>;
  /** Wire names of checked fields (`write` and `fieldBaseline`). */
  readonly checkedFields?: readonly string[];
  /**
   * Wire names of text members: string members other than append-only text fields. With the feature
   * `text-splice/1`, frames change them with splices and setters send splices of long values.
   */
  readonly texts?: readonly string[];
  /** The client's interactions, from `defineInteractions`. */
  readonly interactions?: BridgeInteractions;
  /**
   * Interaction handlers by interaction name, registered before `open` and sent with it, so the producer can ask
   * them from the first request on. Handlers registered later through `interactions` are sent with `interactions`.
   * An `undefined` entry registers nothing, so a generated `connect{Name}` can pass its typed options through.
   */
  readonly handlers?: Readonly<Record<string, ((input: never, context: BridgeInteractionContext) => unknown) | undefined>>;
  /** The keyed collections and append-only text fields, from `defineCollections`. */
  readonly collections?: BridgeCollections;
  /** `bridgeOperations`, for a client with operations. */
  readonly operations?: BridgeOperations;
}

/** A checked write's request members, with the values already encoded as the field's wire type. */
export interface BridgeFieldWriteRequest {
  readonly requestId: string;
  readonly expectedVersion: number;
  readonly expectedValue: unknown;
  readonly value: unknown;
}

/**
 * The runtime half of a generated client: one presentation of a route, registered with `open`. Generated code is
 * its only intended caller. Values and inputs are encoded as their wire types; `undefined` as an input means the
 * command takes none. Calls that change state resolve once the route's state includes their effects (the reply's
 * revision), with the exposed state at that moment.
 */
export interface ViewConnection<TState> extends ViewClient<TState> {
  /** The route of this presentation. */
  readonly route: string;
  readonly interactions: Readonly<Record<string, InteractionSurface>>;
  /**
   * A generated setter: sends `set` with the member's next edit number and exposes `value` (decoded with the
   * member's decoder) in `snapshot` until the edit's revision arrives or the edit fails. A superseded edit resolves
   * like an applied one.
   */
  set(member: string, value: unknown): Promise<TState>;
  /** Runs a command after the presentation's interaction handlers are registered. */
  command(member: string, input?: unknown): Promise<TState>;
  /**
   * Runs a command that declares a failure type, like `command`. Its declared failure resolves
   * `bridgeFailure(decodeFailure(failure))`; every other failure still rejects with `BridgeError`.
   */
  commandOutcome<TFailure>(member: string, decodeFailure: (value: unknown) => TFailure, input?: unknown): Promise<BridgeOutcome<TState, TFailure>>;
  /** Asks whether a command is available for `input` without running it. */
  can(member: string, input?: unknown): Promise<boolean>;
  /** A checked write of `member`; `decode` decodes the field values of the receipt. */
  write<T>(member: string, request: BridgeFieldWriteRequest, decode: (value: unknown) => T): Promise<FieldWriteReceipt<T>>;
  /** The value and version of a checked field in the producer's state, which a `write` call is based on. */
  fieldBaseline(field: string): FieldBaseline<unknown>;
  /** Starts `member` as an operation with the id `operation`. */
  start<TResult, TFailure = never>(member: string, operation: string, input: unknown,
    codec: BridgeOperationCodec<TResult, TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
  /** Returns the operation of `member` with the id `operation`, for example after the page reloaded. */
  recover<TResult, TFailure = never>(member: string, operation: string,
    codec: BridgeOperationCodec<TResult, TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
}

/** The scope of an operation: its route's request channel and revision waits. Internal to the runtime. */
export interface RouteOperationScope extends OperationScope {
  readonly runtime: SharedRuntime;
  /** Throws a `disconnected` BridgeError when the Bridge is disconnected, or `released` when the View was released. */
  assertConnected(): void;
  /** Resolves when the producer acknowledged the presentation's interaction handlers. */
  ready(): Promise<void>;
  /** Resolves once the route reached `revision`, at once when the route is not open. */
  waitForRevision(revision: number): Promise<void>;
}

type State = Record<string, unknown>;

// Sets a member as an own property: a member named "__proto__" must not replace the state's prototype.
function setMember(state: State, member: string, value: unknown): void {
  if (member === "__proto__") Object.defineProperty(state, member, { value, enumerable: true, writable: true, configurable: true });
  else state[member] = value;
}

interface Lease {
  readonly id: string;
  disposed: boolean;
  /** The producer registered this presentation on the current connection. */
  registered: boolean;
  current: unknown;
  readonly listeners: Set<(state: unknown) => void>;
  interactions: InteractionSession | undefined;
}

interface Overlay {
  readonly edit: number;
  /** The connection the edit was sent on; only that connection's producer holds its value as a splice base. */
  readonly connection: number;
  /** The decoded value, or absent when it did not decode (the producer rejects it). */
  readonly value?: unknown;
  /** The reply revision of the edit, once it arrived. */
  revision?: number;
}

interface Waiter { readonly revision: number; readonly resolve: () => void; timer: unknown }

// Recovery reads a snapshot up to four times, waiting 250, 500 and 1,000 ms between failed reads. Frames that
// arrive meanwhile are kept, up to 64, and applied after the read (state-changes.md, "Recovery").
const recoveryAttempts = 4;
const recoveryDelay = 250;
const maximumBufferedFrames = 64;
// A reply's revision that the route has not reached after this long is a producer bug (README, "Client resolution").
const revisionTimeout = 5_000;
// A reopened presentation that a closed connection still owns answers `ignored` for a while.
const reopenAttempts = 20;
const reopenDelay = 250;

/** The shared state of one open route: its producer state, revision, overlay and presentations. */
class RouteEntry {
  readonly presentations = new Set<Lease>();
  active = true;
  /** The producer's state, decoded. */
  state: State | undefined;
  revision: number | undefined;
  /** The connection the state was read on; the first state of a new connection is its baseline. */
  stateConnection = -1;
  readonly fields = new Map<string, number>();
  /** What subscribers see: the state with the overlay. */
  exposed: State | undefined;
  readonly overlays = new Map<string, Overlay>();
  private readonly waiters = new Set<Waiter>();
  private reading = 0;
  private buffered: Push[] = [];
  private missed: number | undefined;
  private recovery: Promise<boolean> | undefined;
  /** A frame kept during the recovery that is ending needs another one. */
  private recoverAgain = false;
  private backoff: { cancel(): void } | undefined;
  private readonly stops: (() => void)[];
  /** Delivers the exposed state to the presentations; queued once per animation frame by `publish`. */
  private readonly deliver = () => this.notify();

  constructor(readonly runtime: SharedRuntime, readonly route: string, readonly contract: string,
    readonly members: Readonly<Record<string, (wire: unknown) => unknown>>, readonly checkedFields: readonly string[],
    readonly collections: BridgeCollections | undefined, readonly texts: ReadonlySet<string> = new Set()) {
    this.stops = [
      runtime.onPush("state", route, push => this.statePush(push as Extract<Push, { kind: "state" }>)),
      runtime.onPush("frame", route, push => this.frame(push)),
      runtime.onPush("failure", route, push => {
        const failure = push as Extract<Push, { kind: "failure" }>;
        // The producer withholds the route until its keys are valid; the client keeps its last state.
        reportBridgeError(replyError(failure.error, route, `The Bridge cannot publish the state of ${route}.`), route);
      }),
      runtime.onInvalidPush(route, reading => {
        if (reading.kind !== "state" && reading.kind !== "frame") return;
        reportBridgeError(new BridgeError("failed", `The Bridge pushed an invalid ${reading.kind} for ${route}: ${reading.reason}`, { route }), route);
        this.recover();
      }),
      runtime.onConnection(connected => { if (connected) this.reopen(); else this.connectionLost(); }),
    ];
  }

  live(): boolean { return this.active && this.runtime.routes.get(this.route) === this; }

  close(): void {
    if (!this.active) return;
    this.active = false;
    if (this.runtime.routes.get(this.route) === this) this.runtime.routes.delete(this.route);
    for (const stop of this.stops) stop();
    this.backoff?.cancel();
    // A call whose route closed resolves at once.
    for (const waiter of this.waiters) { clock.clear(waiter.timer); waiter.resolve(); }
    this.waiters.clear();
    this.buffered = [];
  }

  /* ------------------------------------------------------------ states */

  /** Decodes a full state; throws for an invalid one. */
  private decode(wire: Message, fields: unknown): { state: State; versions: Map<string, number> } {
    const state: State = {};
    for (const [member, decode] of Object.entries(this.members)) {
      try { setMember(state, member, decode(Object.getOwnPropertyDescriptor(wire, member)?.value)); }
      catch (cause) { throw new Error(`The member ${member} is invalid: ${errorMessage(cause)}`); }
      this.collections?.validate(member, state[member]);
    }
    const versions = new Map<string, number>();
    for (const field of this.checkedFields) {
      const version = isObject(fields) && isObject(fields[field]) ? fields[field]["version"] : undefined;
      if (typeof version === "number") versions.set(field, version);
    }
    return { state, versions };
  }

  /**
   * Accepts a full state when its revision is greater than the current one, or when it is the first state of a new
   * connection. Throws for an invalid state, which leaves the current one.
   */
  accept(revision: number, wire: Message, fields: unknown): boolean {
    const baseline = this.stateConnection !== this.runtime.connection;
    if (!baseline && this.revision !== undefined && revision <= this.revision) return false;
    const { state, versions } = this.decode(wire, fields);
    const reconnected = baseline && this.state !== undefined;
    this.state = state;
    this.revision = revision;
    this.stateConnection = this.runtime.connection;
    this.fields.clear();
    for (const [field, version] of versions) this.fields.set(field, version);
    if (reconnected) {
      // The first state of a new connection includes every edit and call the producer answered before.
      for (const [member, overlay] of this.overlays) if (overlay.revision !== undefined) this.overlays.delete(member);
      this.settle(true);
    } else this.settle();
    return true;
  }

  private statePush(push: Extract<Push, { kind: "state" }>): void {
    if (!this.live()) return;
    try { this.accept(push.revision, push.state, push.fields); }
    catch (cause) {
      reportBridgeError(new BridgeError("failed", `The Bridge pushed an invalid state for ${this.route}: ${errorMessage(cause)}`,
        { cause, route: this.route }), this.route);
    }
  }

  /** Applies a frame (state-changes.md, "Applying frames"). */
  frame(push: Push): void {
    if (!this.live() || push.kind !== "frame") return;
    if (this.reading > 0) {
      // Frames that arrive during a read are applied after it; a repeated frame is kept once.
      if (this.buffered.some(frame => frame["revision"] === push.revision)) return;
      if (this.buffered.length < maximumBufferedFrames) this.buffered.push(push);
      else this.missed = Math.max(this.missed ?? push.revision, push.revision);
      return;
    }
    if (this.revision !== undefined && push.revision <= this.revision) return;
    if (this.state === undefined || push.baseRevision !== this.revision || push.revision <= push.baseRevision) { this.recover(); return; }
    let next: { state: State; versions: Map<string, number> };
    try { next = this.applyChanges(this.state, push.changes); }
    catch (cause) {
      reportBridgeError(new BridgeError("failed", `A frame for ${this.route} could not be applied: ${errorMessage(cause)}`,
        { cause, route: this.route }), this.route);
      this.recover();
      return;
    }
    this.state = next.state;
    this.revision = push.revision;
    for (const [field, version] of next.versions) this.fields.set(field, version);
    this.settle();
  }

  private applyChanges(state: State, changes: readonly Change[]): { state: State; versions: Map<string, number> } {
    const next: State = { ...state };
    const versions = new Map<string, number>();
    const owned = new Set<string>();
    const edited = new Set<string>();
    for (const change of changes) {
      const field = change.field;
      if (!Object.hasOwn(this.members, field)) throw new Error(`${field} is not a member of ${this.route}.`);
      if (change.kind === "set") {
        setMember(next, field, this.members[field]!(change["value"]));
        const checked = this.checkedFields.includes(field);
        if (checked !== (change["version"] !== undefined)) throw new Error(`The set of ${field} has ${checked ? "no" : "a"} version.`);
        if (checked) versions.set(field, change["version"] as number);
        edited.add(field);
        continue;
      }
      if (change.kind === "splice") {
        // text-splice/1: a splice of a text member, applied to the value the previous change of the frame left.
        if (!this.texts.has(field)) throw new Error(`${field} is not a text member.`);
        setMember(next, field, applySplice(next[field], change));
        const checked = this.checkedFields.includes(field);
        if (checked !== (change["version"] !== undefined)) throw new Error(`The splice of ${field} has ${checked ? "no" : "a"} version.`);
        if (checked) versions.set(field, change["version"] as number);
        edited.add(field);
        continue;
      }
      if (!this.collections?.kind(field)) throw new Error(`${field} is not a keyed collection or text field.`);
      setMember(next, field, this.collections.apply(field, next[field], change, owned.has(field)));
      owned.add(field);
      edited.add(field);
    }
    for (const field of edited) this.collections?.validate(field, next[field]);
    return { state: next, versions };
  }

  /* ------------------------------------------------------------ reads */

  /** Runs a read of the full state; frames that arrive meanwhile are applied after it. */
  private async read(run: () => Promise<boolean>): Promise<boolean> {
    this.reading++;
    let accepted = false;
    try { accepted = await run(); return accepted; }
    finally {
      this.reading--;
      if (this.reading === 0) this.drain(accepted);
    }
  }

  private draining = false;

  private drain(accepted: boolean): void {
    const frames = this.buffered;
    const missed = this.missed;
    this.buffered = [];
    this.missed = undefined;
    // After a read that failed, the client keeps its last state and discards the frames it kept.
    if (accepted && this.live()) {
      this.draining = true;
      try {
        for (const frame of frames) this.frame(frame);
        if (missed !== undefined && (this.revision === undefined || missed > this.revision)) this.recover();
      } finally { this.draining = false; }
    }
    this.settle();
  }

  /** Reads the state with `snapshot`, retrying a failed read with backoff. Resolves with whether a state was accepted. */
  recover(): Promise<boolean> {
    if (!this.live()) return Promise.resolve(false);
    if (this.recovery && this.draining) { this.recoverAgain = true; return this.recovery; }
    return this.recovery ??= this.read(() => this.readSnapshot()).finally(() => {
      this.recovery = undefined;
      if (!this.recoverAgain) return;
      this.recoverAgain = false;
      void this.recover();
    });
  }

  private async readSnapshot(): Promise<boolean> {
    for (let attempt = 1; ; attempt++) {
      try {
        const reply = await this.runtime.request({ kind: "snapshot", route: this.route }, this.route);
        if (!this.live()) return false;
        if (!reply.ok) throw replyError(reply.error, this.route, "The state could not be read.");
        try { this.accept(reply["revision"] as number, reply["state"] as Message, reply["fields"]); }
        catch (cause) { throw new BridgeError("failed", `The Bridge returned an invalid state: ${errorMessage(cause)}`, { cause, route: this.route }); }
        return true;
      } catch (error) {
        // A closed route and a released View stop silently; a disconnect stops and is reported, because the new
        // connection opens every route again.
        if (!this.live() || error instanceof BridgeError && error.kind === "released") return false;
        if (!this.runtime.connected || error instanceof BridgeError && error.kind === "disconnected" || attempt === recoveryAttempts) {
          reportBridgeError(error, this.route);
          return false;
        }
        emitErrorDiagnostic(error, this.route);
        const wait = delay(recoveryDelay * 2 ** (attempt - 1));
        this.backoff = wait;
        await wait.promise;
        this.backoff = undefined;
        if (!this.live()) return false;
      }
    }
  }

  /**
   * Sends `open` for a presentation and accepts the state of its reply. Retries while the producer answers
   * `ignored`, as after a reconnect while the closed connection still owns the id. Throws for a failed open.
   */
  async open(lease: Lease, signal?: AbortSignal): Promise<void> {
    for (let attempt = 1; ; attempt++) {
      let reply: Reply | undefined;
      const handlers = lease.interactions?.handlers() ?? [];
      await abortable(this.read(async () => {
        reply = await this.runtime.request({
          kind: "open", route: this.route, presentation: lease.id, ...(handlers.length === 0 ? {} : { handlers }),
        }, this.route);
        if (lease.disposed) {
          // Disposed while the open was on its way, as by an aborted connect: the producer registered it, so close it.
          if (reply.ok && reply["presentation"] === "ok" && this.runtime.connected)
            void this.runtime.request({ kind: "close", route: this.route, presentation: lease.id }, this.route).catch(() => undefined);
          return false;
        }
        if (!reply.ok || !this.live()) return false;
        try { this.accept(reply["revision"] as number, reply["state"] as Message, reply["fields"]); }
        catch (cause) { throw new BridgeError("failed", `The Bridge returned an invalid state: ${errorMessage(cause)}`, { cause, route: this.route }); }
        return true;
      }), signal);
      if (!reply!.ok) throw replyError(reply!.error, this.route, "The View could not be opened.");
      if (lease.disposed) return;
      if (reply!["presentation"] !== "ignored") {
        lease.registered = true;
        lease.interactions?.opened(typeof reply!["registration"] === "string" ? reply!["registration"] : undefined);
        return;
      }
      if (attempt === reopenAttempts || lease.disposed || !this.live()) return;
      const wait = delay(reopenDelay);
      await abortable(wait.promise, signal);
    }
  }

  /** A new connection: open every presentation again with its id, and resume its interaction handlers. */
  private reopen(): void {
    if (!this.live()) return;
    for (const lease of this.presentations) {
      if (lease.disposed) continue;
      void this.open(lease).catch(error => {
        if (!lease.disposed && this.live()) reportBridgeError(error instanceof BridgeError ? error
          : new BridgeError("failed", "The View could not be opened again after a reconnect.", { cause: error, route: this.route }), this.route);
      });
    }
  }

  private connectionLost(): void {
    for (const lease of this.presentations) {
      lease.registered = false;
      lease.interactions?.paused();
    }
    // A recovery stops at once; the new connection opens the route again.
    this.backoff?.cancel();
  }

  /* ------------------------------------------------------------ revisions and the overlay */

  /** Resolves once the route reached `revision` and no read is in flight; at once when the route is not open. */
  waitForRevision(revision: number): Promise<void> {
    if (!this.live() || this.reading === 0 && this.revision !== undefined && this.revision >= revision) {
      flushNotifications();
      return Promise.resolve();
    }
    return new Promise<void>(resolve => {
      const waiter: Waiter = { revision, resolve, timer: undefined };
      waiter.timer = clock.set(() => {
        if (!this.waiters.has(waiter)) return;
        emitBridgeDiagnostic({
          kind: "error", code: "revision-timeout", route: this.route,
          message: `The route ${this.route} did not reach revision ${revision} within ${revisionTimeout} ms of the reply that named it; ` +
            "the client reads its state again. A producer that publishes every revision it replies with never causes this.",
        });
        void this.recover().finally(() => {
          if (!this.waiters.delete(waiter)) return;
          flushNotifications();
          resolve();
        });
      }, revisionTimeout);
      this.waiters.add(waiter);
    });
  }

  /**
   * Ends overlays and resolves waiters whose revision the route reached, then publishes the exposed state. A resolved
   * waiter, or `immediate`, delivers it at once: a caller that awaited it reads the state it waited for.
   */
  settle(all = false, immediate = false): void {
    let resolved = false;
    if (this.reading === 0 && this.revision !== undefined) {
      for (const [member, overlay] of this.overlays)
        if (overlay.revision !== undefined && this.revision >= overlay.revision) this.overlays.delete(member);
      for (const waiter of this.waiters)
        if (all || waiter.revision <= this.revision) {
          this.waiters.delete(waiter);
          clock.clear(waiter.timer);
          waiter.resolve();
          resolved = true;
        }
    }
    // The waiters' continuations run after this returns, so they see the delivered state.
    this.publish(immediate || resolved);
  }

  /**
   * Recomputes the exposed state and, when a member changed, queues its delivery to the presentations for the next
   * animation frame. `immediate` delivers every pending state of the page now.
   */
  publish(immediate = false): void {
    if (this.state !== undefined) {
      let next: State = this.state;
      if (this.overlays.size !== 0) {
        next = { ...this.state };
        for (const [member, overlay] of this.overlays) if (Object.hasOwn(overlay, "value")) setMember(next, member, overlay.value);
      }
      const previous = this.exposed;
      if (previous === undefined || !Object.keys(this.members).every(member => Object.is(previous[member], next[member]))) {
        this.exposed = next;
        deferNotification(this.deliver);
      }
    }
    if (immediate) flushNotifications();
  }

  /** Delivers the exposed state to each presentation that has not received it. */
  private notify(): void {
    const exposed = this.exposed;
    if (exposed === undefined) return;
    for (const lease of this.presentations) {
      if (lease.disposed || lease.current === exposed) continue;
      lease.current = exposed;
      for (const listener of [...lease.listeners]) {
        try { listener(exposed); } catch (error) { reportBridgeError(error, this.route); }
      }
    }
  }

  /** Starts an edit of `member`: its number, and the overlay of its value. */
  beginEdit(member: string, value: unknown): number {
    const key = `${this.route}\u0000${member}`;
    const edit = (this.runtime.edits.get(key) ?? 0) + 1;
    this.runtime.edits.set(key, edit);
    const connection = this.runtime.connection;
    let overlay: Overlay = { edit, connection };
    try { overlay = { edit, connection, value: this.members[member]!(value) }; } catch { /* the producer rejects it */ }
    this.overlays.set(member, overlay);
    // The page shows its own edit at once, such as the value of a controlled input.
    this.publish(true);
    return edit;
  }

  /** The member's latest edit number on this page. */
  latestEdit(member: string): number { return this.runtime.edits.get(`${this.route}\u0000${member}`) ?? 0; }

  /**
   * The splice a setter sends instead of `value` (text-splice/1, README "Edits"): against the latest edit while its
   * overlay is shown, otherwise against the route's state at its revision. Undefined, a full value, when the feature
   * is not in use, the base is not known to this connection's producer, or the splice keeps fewer than 64 code units.
   */
  spliceFor(member: string, value: unknown): Readonly<Record<string, unknown>> | undefined {
    if (typeof value !== "string" || !this.texts.has(member) || this.runtime.features?.has(textSpliceFeature) !== true) return undefined;
    const overlay = this.overlays.get(member);
    let base: unknown;
    let reference: { readonly baseEdit: number } | { readonly baseRevision: number };
    if (overlay !== undefined) {
      if (overlay.connection !== this.runtime.connection || !Object.hasOwn(overlay, "value")) return undefined;
      base = overlay.value;
      reference = { baseEdit: overlay.edit };
    } else {
      if (this.state === undefined || this.revision === undefined || this.stateConnection !== this.runtime.connection) return undefined;
      base = this.state[member];
      reference = { baseRevision: this.revision };
    }
    if (typeof base !== "string") return undefined;
    const splice = singleSplice(base, value);
    if (base.length - splice.delete < minimumKept) return undefined;
    return { ...reference, length: base.length, index: splice.index, delete: splice.delete, text: splice.text };
  }

  /** The edit's reply arrived: a latest edit that failed ends the overlay, and an applied one ends at its revision. */
  endEdit(member: string, edit: number, revision: number | undefined): void {
    const overlay = this.overlays.get(member);
    if (overlay?.edit !== edit) return;
    if (revision === undefined) this.overlays.delete(member);
    else overlay.revision = revision;
    this.settle(false, true);
  }
}

/**
 * Connects one generated client presentation to its route: sends `open` with a new presentation id and returns the
 * connection once the state arrived. Presentations of one route share its state, revision and recovery; each has
 * its own subscribers and interaction handlers.
 */
export async function connectView<TState>(options: ViewConnectOptions<TState>): Promise<ViewConnection<TState>> {
  try { return await connectRoute(options); }
  catch (error) {
    // The caller aborted the connection; that is not a Bridge failure.
    if (!isAbort(error, options.signal)) emitErrorDiagnostic(error, options.route);
    throw error;
  }
}

// Reports a failure that leaves a client method once, with its route, then rethrows it.
async function observed<T>(route: string, run: () => Promise<T>): Promise<T> {
  try { return await run(); }
  catch (error) { emitErrorDiagnostic(error, route); throw error; }
}

const receiptKinds = new Set(["applied", "committed-with-error", "conflict", "rejected"]);

async function connectRoute<TState>(options: ViewConnectOptions<TState>): Promise<ViewConnection<TState>> {
  const { route, signal } = options;
  throwIfAborted(signal);
  publishConnectionState();
  const bridge = await waitForBridge({ ...(options.timeout === undefined ? {} : { timeout: options.timeout }), signal });
  const runtime = sharedRuntimeFor(bridge);
  const existing = runtime.routes.get(route) as RouteEntry | undefined;
  if (existing && existing.contract !== options.contract)
    throw new BridgeError("failed", "This route already has an incompatible Bridge contract.", { route });
  const entry = existing ?? new RouteEntry(runtime, route, options.contract,
    options.members as Readonly<Record<string, (wire: unknown) => unknown>>, options.checkedFields ?? [], options.collections,
    new Set(options.texts ?? []));
  if (!existing) runtime.routes.set(route, entry);
  const lease: Lease = {
    id: randomId(), disposed: false, registered: false, current: undefined, listeners: new Set(), interactions: undefined,
  };
  entry.presentations.add(lease);

  const live = () => !lease.disposed && entry.live() && currentRuntime() === runtime;
  function assertConnected(): void {
    if (!runtime.connected) throw new BridgeError("disconnected", "The Bridge is disconnected.", { route });
    if (!live()) throw new BridgeError("released", "This View was released. Connect its current reference for the current state.", { route });
  }
  const request = async (message: Message): Promise<Reply> => {
    assertConnected();
    return runtime.request(message, route);
  };
  // The interaction handlers are created before `open`, so a reconnect can send them with it.
  if (options.interactions) lease.interactions = options.interactions({
    route, presentation: lease.id, live, request, onPush: (kind, handler) => runtime.onPush(kind, route, handler),
  });
  for (const [name, handler] of Object.entries(options.handlers ?? {})) {
    if (handler === undefined) continue;
    const surface = lease.interactions?.surface[name];
    if (!surface) { dispose(); throw new BridgeError("failed", `The View has no interaction ${name}.`, { route }); }
    surface.handle(handler);
  }
  function dispose(): void {
    if (lease.disposed) return;
    lease.disposed = true;
    lease.interactions?.dispose();
    lease.listeners.clear();
    entry.presentations.delete(lease);
    if (lease.registered && runtime.connected) {
      // The producer also closes the presentation with the connection, so a closed transport is expected here.
      void runtime.request({ kind: "close", route, presentation: lease.id }, route).catch(cause => {
        if (runtime.connected) reportBridgeError(new BridgeError("failed", "The View could not be closed.", { cause, route }), route);
      });
    }
    lease.registered = false;
    if (entry.presentations.size === 0) entry.close();
  }

  try {
    assertConnected();
    await entry.open(lease, signal);
  } catch (error) {
    dispose();
    throw error;
  }
  if (!live()) { dispose(); throw new BridgeError(runtime.connected ? "released" : "disconnected", "The View was released during connection.", { route }); }
  if (signal?.aborted) { dispose(); throw abortReason(signal); }
  // The other presentations of the route receive the state this one starts with.
  flushNotifications();
  lease.current = entry.exposed;

  const ready = async () => { await lease.interactions?.ready(); };
  const current = () => lease.current as TState;
  // A call that ran resolves once the route reached its revision; a failed one rejects after that.
  const settled = async (reply: Reply): Promise<void> => {
    if (reply.revision !== undefined) await entry.waitForRevision(reply.revision);
    // The caller reads the state of every frame that arrived before the reply.
    flushNotifications();
  };
  const failure = async (reply: Reply, fallback: string): Promise<never> => {
    await settled(reply);
    throw replyError(reply.error, route, fallback);
  };
  const operationScope: RouteOperationScope = {
    route, contract: options.contract, runtime, assertConnected, ready, waitForRevision: revision => entry.waitForRevision(revision),
  };
  const operations = () => {
    if (!options.operations) throw new BridgeError("failed", "This View was connected without operation support.", { route });
    return options.operations;
  };
  async function command(member: string, input: unknown): Promise<Reply> {
    assertConnected();
    await ready();
    return request({ kind: "command", route, member, ...(input === undefined ? {} : { input }) });
  }

  return {
    route,
    get snapshot() { return current(); },
    subscribe(listener) {
      const typed = listener as (state: unknown) => void;
      if (!live()) { typed(lease.current); return () => {}; }
      lease.listeners.add(typed);
      // The caller sees a failing initial delivery and gets no unsubscribe, so do not retain it.
      try { typed(lease.current); }
      catch (error) { lease.listeners.delete(typed); throw error; }
      return () => { lease.listeners.delete(typed); };
    },
    dispose,
    interactions: lease.interactions?.surface ?? {},
    set: (member, value) => observed(route, async () => {
      assertConnected();
      // The splice is computed against what the page shows before this edit.
      const splice = entry.spliceFor(member, value);
      let edit = entry.beginEdit(member, value);
      let reply: Reply;
      try {
        reply = await request(splice === undefined ? { kind: "set", route, member, value, edit } : { kind: "set", route, member, edit, splice });
        if (!reply.ok && reply.error?.reason === "splice-base") {
          // The producer no longer has the splice's base, and admitted nothing. A later edit carries a newer value;
          // otherwise this value is sent in full as a new edit.
          if (entry.latestEdit(member) !== edit) { flushNotifications(); return current(); }
          edit = entry.beginEdit(member, value);
          reply = await request({ kind: "set", route, member, value, edit });
        }
      }
      catch (error) { entry.endEdit(member, edit, undefined); throw error; }
      if (!reply.ok) {
        entry.endEdit(member, edit, undefined);
        return failure(reply, "The value could not be set.");
      }
      if (reply.released === true) { entry.endEdit(member, edit, undefined); return current(); }
      // A superseded edit leaves the overlay to the newer edit, which is still on its way.
      if (reply["superseded"] !== true) entry.endEdit(member, edit, reply.revision);
      await settled(reply);
      return current();
    }),
    command: (member, input) => observed(route, async () => {
      const reply = await command(member, input);
      if (!reply.ok) return failure(reply, "The command failed.");
      await settled(reply);
      return current();
    }),
    commandOutcome<TFailure>(member: string, decodeFailure: (value: unknown) => TFailure, input?: unknown) {
      return observed(route, async (): Promise<BridgeOutcome<TState, TFailure>> => {
        const reply = await command(member, input);
        if (!reply.ok && reply.error?.kind === "domain-failed") {
          await settled(reply);
          let declared: TFailure;
          try { declared = decodeFailure(reply.error.failure); }
          catch (cause) { throw new BridgeError("failed", `The Bridge returned an invalid failure: ${errorMessage(cause)}`, { cause, route }); }
          // The command failed as declared, but its effects could not be published: report it, so the key
          // problem is not lost behind a resolved outcome.
          if (reply.error.reason === "state-withheld") reportBridgeError(replyError(reply.error, route), route);
          return bridgeFailure(declared);
        }
        if (!reply.ok) return failure(reply, "The command failed.");
        await settled(reply);
        return bridgeSuccess(current());
      });
    },
    can: (member, input) => observed(route, async () => {
      const reply = await request({ kind: "can", route, member, ...(input === undefined ? {} : { input }) });
      if (!reply.ok) throw replyError(reply.error, route, "The command availability could not be read.");
      return reply["value"] as boolean;
    }),
    write: <T>(member: string, write: BridgeFieldWriteRequest, decode: (value: unknown) => T) => observed(route, async (): Promise<FieldWriteReceipt<T>> => {
      const identity = { kind: "write", route, member, requestId: write.requestId, expectedVersion: write.expectedVersion } as const;
      const full = { ...identity, expectedValue: write.expectedValue, value: write.value };
      // text-splice/1 (README "Text splices in write"): a checked text member's write sends a splice of expectedValue
      // when it keeps at least 64 code units of it.
      const { expectedValue, value } = write;
      let splice: Readonly<Record<string, unknown>> | undefined;
      if (typeof expectedValue === "string" && typeof value === "string" && runtime.features?.has(textSpliceFeature) === true
        && entry.texts.has(member) && entry.checkedFields.includes(member)) {
        const single = singleSplice(expectedValue, value);
        if (expectedValue.length - single.delete >= minimumKept) splice = { length: expectedValue.length, ...single };
      }
      let reply = await request(splice === undefined ? full : { ...identity, splice });
      // The producer's value at expectedVersion is not the splice's base, and it retained nothing: send the write in full.
      if (splice !== undefined && !reply.ok && reply.error?.reason === "splice-base") reply = await request(full);
      if (!reply.ok) return failure(reply, "The checked write failed.");
      await settled(reply);
      try {
        const receipt = reply["receipt"] as Message;
        const kind = receipt["kind"];
        if (typeof kind !== "string" || !receiptKinds.has(kind)) throw new TypeError("Unknown checked write receipt kind.");
        const text = (value: unknown) => { if (typeof value !== "string") throw new TypeError("Expected a message."); return value; };
        const baseline = (value: unknown): FieldBaseline<T> => {
          if (!isObject(value) || typeof value["version"] !== "number" || !Number.isSafeInteger(value["version"]) || value["version"] < 0)
            throw new TypeError("Expected a field value and version.");
          return { value: decode(value["value"]), version: value["version"] };
        };
        switch (kind) {
          case "applied": return { kind, snapshot: baseline(receipt["snapshot"]),
            ...(receipt["validation"] === undefined ? {} : { validation: text(receipt["validation"]) }) };
          case "committed-with-error": return { kind, snapshot: baseline(receipt["snapshot"]), message: text(receipt["message"]) };
          case "conflict": return { kind, incoming: baseline(receipt["incoming"]), message: text(receipt["message"]) };
          default: return { kind: "rejected", message: text(receipt["message"]) };
        }
      } catch (cause) { throw new BridgeError("failed", `The Bridge returned an invalid checked write receipt: ${errorMessage(cause)}`, { cause, route }); }
    }),
    fieldBaseline(field) {
      if (!live() || entry.state === undefined)
        throw runtime.connected ? new BridgeError("released", "This View was released.", { route }) : new BridgeError("disconnected", "The Bridge is disconnected.", { route });
      const version = entry.fields.get(field);
      if (!entry.checkedFields.includes(field)) throw new BridgeError("failed", "The requested checked field is unavailable.", { route });
      if (version === undefined) throw new BridgeError("failed", "The checked field baseline is unavailable for this connection.", { route });
      return { value: entry.state[field], version };
    },
    start: (member, operation, input, codec) => observed(route, () => {
      if (operation.length === 0) throw new RangeError("An operation id is required.");
      return operations().start(operationScope, member, operation, input, codec);
    }),
    recover: (member, operation, codec) => observed(route, () => operations().recover(operationScope, member, operation, codec)),
  };
}

/**
 * Returns a per-module cache of content references. A reference stays the same
 * object while it is reachable, so frameworks can key Views by identity.
 */
export function viewReferences<R extends object>(create: (id: string) => R): (id: string) => R {
  const references = new Map<string, WeakRef<R>>();
  const finalizer = new FinalizationRegistry<{ readonly id: string; readonly reference: WeakRef<R> }>(entry => {
    if (references.get(entry.id) === entry.reference) references.delete(entry.id);
  });
  return id => {
    let reference = references.get(id)?.deref();
    if (reference) return reference;
    reference = create(id);
    const weak = new WeakRef(reference);
    references.set(id, weak);
    finalizer.register(reference, { id, reference: weak });
    return reference;
  };
}
