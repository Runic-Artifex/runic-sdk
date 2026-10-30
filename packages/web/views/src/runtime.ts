// The page-wide Views runtime (specs/application, "Shared globals"): one per page and `__runicBridge`, shared by
// every generated module and every copy of this package through `runic.views.generated-client-runtime`. It owns
// the connection (`hello` per connection, its features, and the requests sent with it), the only `onMessage`
// listener, which dispatches pushes by kind and route, the page's edit numbers and the reconnect of open routes.
import { randomId } from "./clock.js";
import { emitBridgeDiagnostic, suppressErrorDiagnostic } from "./diagnostics.js";
import { BridgeError } from "./errors.js";
import {
  invalidResponse, presentationEpochFeature, presentedFeature, readPush, readReply, replyError, type Message, type Push, type PushReading, type Reply,
} from "./protocol.js";
import { hostCallbacks, protocolMismatchError, reportBridgeError, reportProtocolMismatch, type RunicBridgeClient } from "./transport.js";
import { bridgeProtocol, clientName, runicProducerName, viewsVersion } from "./version.js";
import { textSpliceFeature } from "./text.js";
import { reportWindowClosed } from "./connection-state.js";
import { flushNotifications, nextFrame } from "./notifications.js";

/** The features this runtime implements and lists in `hello` (specs/application/README.md, "Features"). */
export const clientFeatures: readonly string[] = [textSpliceFeature, presentationEpochFeature, presentedFeature];

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
  /** Sends a request after the connection's `hello` and resolves with its reply; rejects with a BridgeError. */
  request(message: Message, route?: string): Promise<Reply>;
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
  /** Stops listening to the host bridge. */
  dispose(): void;
}

const sharedRuntimeKey = Symbol.for("runic.views.generated-client-runtime");

/** The page runtime, without creating one. */
export function currentRuntime(): SharedRuntime | undefined {
  return hostCallbacks()[sharedRuntimeKey] as SharedRuntime | undefined;
}

/** Returns the page runtime for a Bridge, replacing one of another Bridge or protocol. */
export function sharedRuntimeFor(bridge: RunicBridgeClient): SharedRuntime {
  const host = hostCallbacks();
  const existing = host[sharedRuntimeKey] as Partial<SharedRuntime> | undefined;
  if (existing?.protocol === bridgeProtocol && existing.bridge === bridge) return existing as SharedRuntime;
  if (existing?.protocol === bridgeProtocol) existing.dispose?.();
  const runtime = createRuntime(bridge);
  host[sharedRuntimeKey] = runtime;
  return runtime;
}

function createRuntime(bridge: RunicBridgeClient): SharedRuntime {
  const pushHandlers = new Map<string, Set<PushHandler>>();
  const windowHandlers = new Map<string, Set<PushHandler>>();
  const invalidHandlers = new Map<string, Set<InvalidPushHandler>>();
  const connectionListeners = new Set<ConnectionListener>();
  let connected = false;
  let reported = false;
  let connection = 0;
  let disposed = false;
  // The hello of the current connection, and whether its reply arrived and succeeded.
  let hello: { readonly connection: number; readonly promise: Promise<void>; done: boolean } | undefined;
  // The features in use on the current connection.
  let features: ReadonlySet<string> = new Set<string>();
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
        try { handler(reading); } catch (error) { reportBridgeError(error, reading.route); }
      }
      return;
    }
    const push = reading.push;
    const handlers = push.route === undefined ? windowHandlers.get(push.kind) : pushHandlers.get(key(push.kind, push.route));
    if (!handlers) return;
    for (const handler of [...handlers]) {
      try { handler(push); } catch (error) { reportBridgeError(error, push.route); }
    }
  }

  /**
   * presented/1 (specs/application/README.md, "Window close"): the application closes the window. The page delivers what it has, lets one frame
   * render it, acknowledges with `closing-reply` and reports `closed`, which a following drop does not change.
   */
  async function closing(): Promise<void> {
    if (!features.has(presentedFeature)) return;
    const current = connection;
    flushNotifications();
    await nextFrame();
    // A new connection is a new window session: its producer did not close it.
    if (disposed || connection !== current) return;
    const reply = connected ? runtime.request({ kind: "closing-reply" }) : undefined;
    // Reported as the reply is sent: the host may end the connection before its answer arrives.
    reportWindowClosed(true);
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
    const promise = (async () => {
      const reply = await send({
        kind: "hello", protocol: bridgeProtocol, session: randomId(),
        client: { name: clientName, version: viewsVersion }, features: clientFeatures,
      }, undefined);
      if (reply["protocol"] !== bridgeProtocol && (reply["protocol"] !== undefined || reply.ok)) {
        refused = reply["protocol"] === undefined ? "does not state a Views protocol" : `uses Views protocol ${String(reply["protocol"])}`;
        const error = protocolMismatchError(refused);
        reportProtocolMismatch(error, bridge);
        throw error;
      }
      if (!reply.ok) throw replyError(reply.error, undefined, "The Bridge refused the connection.");
      // A feature is in use when both sides list it; a producer of 0.13 lists none.
      const listed = Array.isArray(reply["features"]) ? reply["features"] as readonly string[] : [];
      if (connection === current) features = new Set(clientFeatures.filter(feature => listed.includes(feature)));
      const implementation = reply["implementation"] as { readonly name: string; readonly version: string };
      if (implementation.name === runicProducerName && implementation.version !== viewsVersion && !versionReported) {
        versionReported = true;
        emitBridgeDiagnostic({
          kind: "error", code: "version",
          message: `The .NET host runs ${runicProducerName} ${implementation.version}, but this page uses @runic-artifex/views ${viewsVersion}. ` +
            "Use the .NET and npm packages of one release.",
        });
      }
    })();
    const entry = { connection: current, promise, done: false };
    hello = entry;
    // A failed hello is sent again by the next request, unless the producer was refused.
    promise.then(() => { entry.done = true; }, () => { if (hello?.promise === promise) hello = undefined; });
    return promise;
  }

  const runtime: SharedRuntime = {
    protocol: 4,
    bridge,
    get connected() { return connected; },
    get connection() { return connection; },
    routes: new Map(),
    operations: new Map(),
    edits: new Map(),
    get features() { return features; },
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
    onPush: (kind, route, handler) => add(pushHandlers, key(kind, route), handler),
    onInvalidPush: (route, handler) => add(invalidHandlers, route, handler),
    onWindowPush: (kind, handler) => add(windowHandlers, kind, handler),
    onConnection(listener) {
      connectionListeners.add(listener);
      return () => { connectionListeners.delete(listener); };
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      stopMessages();
      stopConnection();
      stopClosing();
      connected = false;
      reportWindowClosed(false);
      for (const listener of [...connectionListeners]) {
        try { listener(false); } catch (error) { reportBridgeError(error); }
      }
      connectionListeners.clear();
    },
  };

  const stopClosing = add(windowHandlers, "closing", () => { void closing(); });
  // A closed window of an earlier Bridge does not close this one.
  reportWindowClosed(false);
  const stopMessages = bridge.onMessage(message => dispatch(message));
  const stopConnection = bridge.onConnectionChange(next => {
    if (disposed || reported && next === connected) return;
    const first = !reported;
    reported = true;
    connected = next;
    if (next) connection++;
    // A new connection ends the `closed` state of a window the application closed.
    if (next && !first) reportWindowClosed(false);
    hello = undefined;
    features = new Set<string>();
    // Each connection starts with `hello`; a request that needs it waits for it, and a failed one is sent again.
    if (next) void greet().catch(() => undefined);
    // The first report states the connection the page starts on; routes open on it themselves.
    if (first && next) return;
    for (const listener of [...connectionListeners]) {
      try { listener(next); } catch (error) { reportBridgeError(error); }
    }
  });
  return runtime;
}

/** The message of a caught value, for an error that wraps it. */
export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
