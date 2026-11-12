// The Views runtime of one Runic instance (specs/application, "Shared globals" and "Runic instances"): one per instance
// and Bridge — on the page, per `__runicBridge` — shared by every generated module and every copy of this package
// through the instance's `runic.views.generated-client-runtime` slot. It owns
// the connection (`hello` per connection, its features, and the requests sent with it), the only `onMessage`
// listener, which dispatches pushes by kind and route, the page's edit numbers and the reconnect of open routes.
import { clock, randomId } from "./clock.js";
import { emitBridgeDiagnostic, suppressErrorDiagnostic } from "./diagnostics.js";
import { BridgeError } from "./errors.js";
import {
  clientErrorsFeature, fieldFlushFeature, fieldPathsFeature, invalidResponse, presentationEpochFeature, presentedFeature, queryCollectionsFeature, readPush,
  viewRequestsFeature, requestSessionFeature, readReply, replyError, type Message, type Push, type PushReading, type Reply,
} from "./protocol.js";
import { protocolMismatchError, reportBridgeError, reportProtocolMismatch, type RunicBridgeClient } from "./transport.js";
import { onInstanceDisposed, type Slots } from "./scope.js";
import { bridgeProtocol, clientName, runicProducerName, viewsVersion } from "./version.js";
import { textSpliceFeature } from "./text.js";
import { translationTextFeature, validationPendingFeature } from "./validation.js";
import { reportWindowLocale } from "./translated-text.js";
import { appearanceFeature, receiveAppearancePush } from "./appearance-push.js";
import { regionCollectionsFeature } from "./regions.js";
import { navigationPathFeature } from "./navigation-feature.js";
import { clientLanguages, clientLanguagesFeature } from "./client-languages.js";
import { hostedRegionsFeature } from "./hosted-region-wire.js";
import { reportWindowClosed } from "./connection-state.js";
import { flushNotifications, nextFrame } from "./notifications.js";
import { commitDrafts } from "./field-controller.js";
import { blobsFeature, readBlobGrant, type BlobGrant } from "./blob-wire.js";

/** The features this runtime implements and lists in `hello` (specs/application/README.md, "Features"). */
// How long a window close waits for the page's field drafts to be written, within the host's 5-second limit.
const closingFlushLimit = 2_000;
// How long a flush (field-flush/1) waits for the drafts' writes before it answers, within the host's 1-second limit.
const flushLimit = 800;

/**
 * Writes the page's field drafts when the user may be about to leave without the page knowing: a keyboard shortcut
 * (a key with Ctrl, Cmd or Alt that produces no character, seen before the page's own handlers) or the window losing
 * focus, as when a native menu opens. It helps hosts and pages without field-flush/1 too. Returns the function that removes the listeners.
 */
function flushOnDeparture(slots: Slots): () => void {
  const page = globalThis as { readonly addEventListener?: (type: string, listener: (event: unknown) => void, options?: boolean) => void;
    readonly removeEventListener?: (type: string, listener: (event: unknown) => void, options?: boolean) => void };
  if (typeof page.addEventListener !== "function" || typeof page.removeEventListener !== "function") return () => {};
  const keydown = (event: unknown) => {
    const key = event as {
      readonly ctrlKey?: boolean; readonly metaKey?: boolean; readonly altKey?: boolean; readonly key?: unknown;
      readonly getModifierState?: (modifier: string) => boolean;
    };
    if (key.ctrlKey !== true && key.metaKey !== true && key.altKey !== true) return;
    // Typing, not a shortcut: AltGr (which Windows reports as Ctrl and Alt) and macOS Option produce characters such
    // as @ € { [ \ | on many layouts, and a key that produces a character without Ctrl or Cmd is text too.
    if (typeof key.getModifierState === "function" && key.getModifierState("AltGraph")) return;
    const character = typeof key.key === "string" && [...key.key].length === 1;
    if (character && (key.metaKey !== true && key.ctrlKey !== true || key.ctrlKey === true && key.altKey === true && key.metaKey !== true)) return;
    void commitDrafts(slots);
  };
  const blur = (event: unknown) => {
    // The window's own blur, not one of an element inside it.
    if ((event as { readonly target?: unknown }).target === globalThis) void commitDrafts(slots);
  };
  page.addEventListener("keydown", keydown, true);
  page.addEventListener("blur", blur, true);
  return () => {
    page.removeEventListener!("keydown", keydown, true);
    page.removeEventListener!("blur", blur, true);
  };
}

// Resolves when `promise` settles or after `milliseconds`, whichever is first.
function firstOf(promise: Promise<unknown>, milliseconds: number): Promise<void> {
  return new Promise(resolve => {
    const timer = clock.set(resolve, milliseconds);
    void promise.finally(() => { clock.clear(timer); resolve(); });
  });
}

export const clientFeatures: readonly string[] = [
  textSpliceFeature, presentationEpochFeature, presentedFeature, viewRequestsFeature, requestSessionFeature, regionCollectionsFeature,
  clientErrorsFeature, queryCollectionsFeature, blobsFeature, validationPendingFeature, fieldFlushFeature, fieldPathsFeature,
  translationTextFeature, navigationPathFeature, appearanceFeature, clientLanguagesFeature, hostedRegionsFeature,
];

/** A push handler, registered per push kind and route. */
export type PushHandler = (push: Push) => void;

/** The kinds of pushes without a route, which window-level handlers receive. */
export type WindowPushKind = Extract<Push, { readonly route?: undefined }>["kind"];

/** Called with `false` when the connection is lost and with `true` for each new connection after a loss. */
export type ConnectionListener = (connected: boolean) => void;

/** A handler for pushes that are not valid messages of the protocol, per route. */
export type InvalidPushHandler = (reading: Extract<PushReading, { result: "invalid" }>) => void;

// Field names and method shapes are a contract between copies of this package on one page: keep them stable.
export interface SharedRuntime {
  /** The protocol this runtime speaks; a runtime of another protocol at the shared key is replaced. */
  readonly protocol: 4;
  readonly bridge: RunicBridgeClient;
  /**
   * The slots of the instance the runtime belongs to (`globalThis` for the page instance). A runtime of an earlier
   * release does not have it; it is always the page's.
   */
  readonly slots?: Slots;
  /** Whether the host connection is open, as `onConnectionChange` last reported it. */
  readonly connected: boolean;
  /** Increments with each new connection. */
  readonly connection: number;
  /** Route entries of open routes, by route (connection.ts). */
  readonly routes: Map<string, unknown>;
  /** Watched operations (operations.ts). */
  readonly operations: Map<string, unknown>;
  /** The latest edit number per route and member (`{route}\u0000{member}`), for the life of the page. */
  readonly edits: Map<string, number>;
  /**
   * The features in use on the current connection: those both its `hello` and the producer's reply list. Empty until
   * the connection's `hello` succeeded. A runtime of an earlier release does not have it.
   */
  readonly features?: ReadonlySet<string>;
  /**
   * Resolves once the current connection's `hello` succeeded, so `features` describes it; sends the `hello` when none is
   * on its way. Rejects like `request` while disconnected or after a protocol mismatch. A runtime of an earlier release
   * does not have it.
   */
  whenGreeted?(): Promise<void>;
  /**
   * navigation-path/1: the window's route path that the current connection's `hello` reply carried, `null` for an empty
   * path region, or `undefined` when the window has no path region or the connection does not use the feature. A
   * runtime of an earlier release does not have it.
   */
  readonly navigationPath?: string | null | undefined;
  /** Sends a request after the connection's `hello` and resolves with its reply; rejects with a BridgeError. */
  request(message: Message, route?: string): Promise<Reply>;
  /**
   * Resolves once the current connection's `hello` succeeded, so `features` is final for it; rejects when there is
   * no connection or the hello fails. A runtime of an earlier release does not have it.
   */
  ready?(): Promise<void>;
  /** Registers a handler for pushes of `kind` on `route`. */
  onPush(kind: Push["kind"], route: string, handler: PushHandler): () => void;
  /** Registers a handler for invalid pushes that name `route`. */
  onInvalidPush(route: string, handler: InvalidPushHandler): () => void;
  /**
   * Registers a handler for pushes of `kind` that name no route, such as `closing`. The runtime answers `closing`
   * itself. A runtime of an earlier release does not have it.
   */
  onWindowPush?(kind: WindowPushKind, handler: PushHandler): () => void;
  onConnection(listener: ConnectionListener): () => void;
  /**
   * blobs/1: the current connection's upload grant, after its `hello` succeeded; undefined when the producer grants
   * none. Rejects as `request` does without a connection. A runtime of an earlier release does not have it.
   */
  blobGrant?(): Promise<BlobGrant | undefined>;
  /** Stops listening to the host bridge. */
  dispose(): void;
}

const sharedRuntimeKey = Symbol.for("runic.views.generated-client-runtime");

/** The runtime of the instance with `slots`, without creating one. */
export function currentRuntime(slots: Slots): SharedRuntime | undefined {
  return slots[sharedRuntimeKey] as SharedRuntime | undefined;
}

/** The slots of the instance `runtime` belongs to. */
export function runtimeSlots(runtime: SharedRuntime): Slots {
  return runtime.slots ?? globalThis as unknown as Slots;
}

/** Returns the runtime of the instance with `slots` for a Bridge, replacing one of another Bridge or protocol. */
export function sharedRuntimeFor(slots: Slots, bridge: RunicBridgeClient): SharedRuntime {
  const existing = slots[sharedRuntimeKey] as Partial<SharedRuntime> | undefined;
  if (existing?.protocol === bridgeProtocol && existing.bridge === bridge) return existing as SharedRuntime;
  if (existing?.protocol === bridgeProtocol) existing.dispose?.();
  const runtime = createRuntime(slots, bridge);
  slots[sharedRuntimeKey] = runtime;
  return runtime;
}

function createRuntime(slots: Slots, bridge: RunicBridgeClient): SharedRuntime {
  const pushHandlers = new Map<string, Set<PushHandler>>();
  const windowHandlers = new Map<string, Set<PushHandler>>();
  const invalidHandlers = new Map<string, Set<InvalidPushHandler>>();
  const connectionListeners = new Set<ConnectionListener>();
  let connected = false;
  let reported = false;
  let connection = 0;
  let disposed = false;
  // The hello of the current connection, and whether its reply arrived and succeeded.
  let hello: { readonly connection: number; readonly session: string; readonly promise: Promise<void>; done: boolean } | undefined;
  // The features in use on the current connection.
  let features: ReadonlySet<string> = new Set<string>();
  // navigation-path/1: the hello reply's path of the current connection.
  let navigationPath: string | null | undefined;
  // The upload grant of the current connection (blobs/1).
  let grant: BlobGrant | undefined;
  // A producer of another protocol: every call of the page fails from then on.
  let refused: string | undefined;
  let versionReported = false;

  const key = (kind: string, route: string) => `${kind}\u0000${route}`;
  const add = <T>(map: Map<string, Set<T>>, name: string, value: T) => {
    let set = map.get(name);
    if (!set) map.set(name, set = new Set());
    set.add(value);
    return () => { set.delete(value); if (set.size === 0 && map.get(name) === set) map.delete(name); };
  };

  function dispatch(text: string): void {
    if (disposed) return;
    const reading = readPush(text);
    if (reading.result === "ignored") return;
    if (reading.result === "invalid") {
      const handlers = reading.route === undefined ? undefined : invalidHandlers.get(reading.route);
      if (!handlers) return;
      for (const handler of [...handlers]) {
        try { handler(reading); } catch (error) { reportBridgeError(slots, error, reading.route); }
      }
      return;
    }
    const push = reading.push;
    const handlers = push.route === undefined ? windowHandlers.get(push.kind) : pushHandlers.get(key(push.kind, push.route));
    if (!handlers) return;
    for (const handler of [...handlers]) {
      try { handler(push); } catch (error) { reportBridgeError(slots, error, push.route); }
    }
  }

  /**
   * presented/1 (specs/application/README.md, "Window close"): the application closes the window. The page delivers what it has, lets one frame
   * render it, acknowledges with `closing-reply` and reports `closed`, which a following drop does not change.
   */
  /**
   * field-flush/1 (specs/application/README.md, "Field flush"): the application is about to decide a departure. The
   * page writes its field drafts, waits for their writes up to `flushLimit`, and answers `flush-reply`.
   */
  async function flush(id: string): Promise<void> {
    if (!features.has(fieldFlushFeature)) return;
    const current = connection;
    const drafts = commitDrafts(slots);
    if (drafts !== undefined) await firstOf(drafts, flushLimit);
    if (disposed || connection !== current || !connected) return;
    await runtime.request({ kind: "flush-reply", id }).catch(() => undefined);
  }

  async function closing(): Promise<void> {
    if (!features.has(presentedFeature)) return;
    const current = connection;
    flushNotifications();
    // Drafts that wait, such as commit: "pause" ones, are written before the close is acknowledged (C2).
    const drafts = commitDrafts(slots);
    if (drafts !== undefined) {
      await firstOf(drafts, closingFlushLimit);
      flushNotifications();
    }
    await nextFrame();
    // A new connection is a new window session: its producer did not close it.
    if (disposed || connection !== current) return;
    const reply = connected ? runtime.request({ kind: "closing-reply" }) : undefined;
    // Reported as the reply is sent: the host may end the connection before its answer arrives.
    reportWindowClosed(slots, true);
    await reply?.catch(() => undefined);
  }

  function refusal(route: string | undefined): BridgeError {
    const error = protocolMismatchError(refused ?? "uses another Views protocol");
    const routed = route === undefined ? error : new BridgeError(error.kind, error.message, { route, reason: "protocol-mismatch" });
    // The one `protocol` diagnostic is the only report of these failures.
    suppressErrorDiagnostic(routed);
    return routed;
  }

  async function send(message: Message, route: string | undefined): Promise<Reply> {
    const kind = message["kind"] as string;
    let text: string;
    try { text = await bridge.request(JSON.stringify(message)); }
    catch (cause) {
      throw new BridgeError(connected ? "failed" : "disconnected", connected ? "The Bridge request could not complete." : "The Bridge is disconnected.",
        { cause, ...(route === undefined ? {} : { route }) });
    }
    const reply = readReply(kind, text);
    if (!reply) throw invalidResponse(route);
    return reply;
  }

  function greet(): Promise<void> {
    if (hello?.connection === connection) return hello.promise;
    const current = connection;
    // A new random session for each connection; with request-session/1 every request of the connection names it.
    const session = randomId();
    const promise = (async () => {
      const reply = await send({
        kind: "hello", protocol: bridgeProtocol, session,
        client: { name: clientName, version: viewsVersion }, features: clientFeatures,
        // client-languages/1: sent before the reply tells whether the producer uses it; a producer without it ignores it.
        languages: clientLanguages(slots),
      }, undefined);
      if (reply["protocol"] !== bridgeProtocol && (reply["protocol"] !== undefined || reply.ok)) {
        refused = reply["protocol"] === undefined ? "does not state a Views protocol" : `uses Views protocol ${String(reply["protocol"])}`;
        const error = protocolMismatchError(refused);
        reportProtocolMismatch(slots, error, bridge);
        throw error;
      }
      if (!reply.ok) throw replyError(reply.error, undefined, "The Bridge refused the connection.");
      // A feature is in use when both sides list it; a producer of 0.13 lists none.
      const listed = Array.isArray(reply["features"]) ? reply["features"] as readonly string[] : [];
      if (connection === current) {
        features = new Set(clientFeatures.filter(feature => listed.includes(feature)));
        const path = reply["navigationPath"];
        navigationPath = features.has(navigationPathFeature) && (path === null || typeof path === "string") ? path : undefined;
        grant = features.has(blobsFeature) ? readBlobGrant(reply["blobs"]) : undefined;
      }
      const implementation = reply["implementation"] as { readonly name: string; readonly version: string };
      if (implementation.name === runicProducerName && implementation.version !== viewsVersion && !versionReported) {
        versionReported = true;
        emitBridgeDiagnostic(slots, {
          kind: "error", code: "version",
          message: `The .NET host runs ${runicProducerName} ${implementation.version}, but this page uses @runic-artifex/views ${viewsVersion}. ` +
            "Use the .NET and npm packages of one release.",
        });
      }
    })();
    const entry = { connection: current, session, promise, done: false };
    hello = entry;
    // A failed hello is sent again by the next request, unless the producer was refused.
    promise.then(() => { entry.done = true; }, () => { if (hello?.promise === promise) hello = undefined; });
    return promise;
  }

  const runtime: SharedRuntime = {
    protocol: 4,
    bridge,
    slots,
    get connected() { return connected; },
    get connection() { return connection; },
    routes: new Map(),
    operations: new Map(),
    edits: new Map(),
    get features() { return features; },
    get navigationPath() { return navigationPath; },
    async whenGreeted() {
      if (refused !== undefined) throw refusal(undefined);
      if (!connected) throw new BridgeError("disconnected", "The Bridge is disconnected.");
      try { await greet(); }
      catch (error) {
        if (refused !== undefined) throw refusal(undefined);
        throw error;
      }
    },
    async request(message, route) {
      if (refused !== undefined) throw refusal(route);
      if (!connected) throw new BridgeError("disconnected", "The Bridge is disconnected.", route === undefined ? {} : { route });
      // The request goes out with the hello, without waiting for its reply: the producer holds it until the hello
      // succeeded (specs/application/README.md, "Handshake").
      const greeting = greet();
      const entry = hello;
      const settled = greeting.then(() => undefined, (error: unknown) => {
        if (refused !== undefined) throw refusal(route);
        throw error;
      });
      // Observed at once: it is awaited only after the request's reply, when the reply came before the hello's.
      settled.catch(() => undefined);
      const pipelined = entry?.promise === greeting && !entry.done;
      // request-session/1: the request names its connection's session, also before the hello's reply; a producer
      // without the feature ignores the member.
      if (entry !== undefined) message = { ...message, session: entry.session };
      let reply: Reply;
      try { reply = await send(message, route); }
      catch (error) {
        if (!entry?.done) await settled;
        throw error;
      }
      // Whatever the reply, the request's outcome follows the hello: a refused producer fails every call.
      if (!entry?.done) await settled;
      if (!reply.ok && reply.error?.reason === "no-hello") {
        // A producer that does not hold requests behind the hello (0.13), or one whose hold ended: nothing ran, so
        // the request is sent once more after the hello.
        if (pipelined && hello === entry) reply = await send(message, route);
        // A producer that lost this connection's hello, such as after a restart behind the same transport.
        if (!reply.ok && reply.error?.reason === "no-hello") hello = undefined;
      }
      return reply;
    },
    ready() {
      if (refused !== undefined) return Promise.reject(refusal(undefined));
      if (!connected) return Promise.reject(new BridgeError("disconnected", "The Bridge is disconnected."));
      return greet();
    },
    onPush: (kind, route, handler) => add(pushHandlers, key(kind, route), handler),
    onInvalidPush: (route, handler) => add(invalidHandlers, route, handler),
    onWindowPush: (kind, handler) => add(windowHandlers, kind, handler),
    onConnection(listener) {
      connectionListeners.add(listener);
      return () => { connectionListeners.delete(listener); };
    },
    async blobGrant() {
      if (refused !== undefined) throw refusal(undefined);
      if (!connected) throw new BridgeError("disconnected", "The Bridge is disconnected.");
      const current = connection;
      await greet();
      return connection === current ? grant : undefined;
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      stopMessages();
      stopConnection();
      stopClosing();
      stopFlush();
      stopFallback();
      stopLocale();
      stopAppearance();
      stopInstance();
      if (slots[sharedRuntimeKey] === runtime) slots[sharedRuntimeKey] = undefined;
      connected = false;
      reportWindowClosed(slots, false);
      for (const listener of [...connectionListeners]) {
        try { listener(false); } catch (error) { reportBridgeError(slots, error); }
      }
      connectionListeners.clear();
    },
  };

  const stopClosing = add(windowHandlers, "closing", () => { void closing(); });
  const stopFlush = add(windowHandlers, "flush", push => { void flush((push as { readonly id: string }).id); });
  const stopFallback = flushOnDeparture(slots);
  // translation-text/1: the producer sends `locale` only to connections that list the feature, also right after the
  // hello, before its reply may have arrived, so it is applied whatever the features read so far.
  const stopLocale = add(windowHandlers, "locale", push => {
    reportWindowLocale(slots, (push as Extract<Push, { kind: "locale" }>).locale);
  });
  // appearance/1: likewise the window's appearance, which the page keeps across a reconnect until the next push. Only
  // the raw push: the store reads it when the page reads the appearance.
  const stopAppearance = add(windowHandlers, "appearance", push => {
    receiveAppearancePush(slots, push as unknown as Readonly<Record<string, unknown>>);
  });
  // Disposing the instance ends its runtime.
  const stopInstance = onInstanceDisposed(slots, () => runtime.dispose());
  // A closed window of an earlier Bridge does not close this one.
  reportWindowClosed(slots, false);
  const stopMessages = bridge.onMessage(message => dispatch(message));
  const stopConnection = bridge.onConnectionChange(next => {
    if (disposed || reported && next === connected) return;
    const first = !reported;
    reported = true;
    connected = next;
    if (next) connection++;
    // A new connection ends the `closed` state of a window the application closed.
    if (next && !first) reportWindowClosed(slots, false);
    hello = undefined;
    features = new Set<string>();
    navigationPath = undefined;
    grant = undefined;
    // Each connection starts with `hello`; a request that needs it waits for it, and a failed one is sent again.
    if (next) void greet().catch(() => undefined);
    // The first report states the connection the page starts on; routes open on it themselves.
    if (first && next) return;
    for (const listener of [...connectionListeners]) {
      try { listener(next); } catch (error) { reportBridgeError(slots, error); }
    }
  });
  return runtime;
}

/** The message of a caught value, for an error that wraps it. */
export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
