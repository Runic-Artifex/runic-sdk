// The page-wide Views runtime (specs/application, "Shared globals"): one per page and `__runicBridge`, shared by
// every generated module and every copy of this package through `runic.views.generated-client-runtime`. It owns
// the connection (`hello` per connection), the only `onMessage` listener, which dispatches pushes by kind and
// route, the page's edit numbers and the reconnect of open routes.
import { randomId } from "./clock.js";
import { emitBridgeDiagnostic, suppressErrorDiagnostic } from "./diagnostics.js";
import { BridgeError } from "./errors.js";
import { invalidResponse, readPush, readReply, replyError, type Message, type Push, type PushReading, type Reply } from "./protocol.js";
import { hostCallbacks, protocolMismatchError, reportBridgeError, reportProtocolMismatch, type RunicBridgeClient } from "./transport.js";
import { bridgeProtocol, clientName, runicProducerName, viewsVersion } from "./version.js";

/** A push handler, registered per push kind and route. */
export type PushHandler = (push: Push) => void;

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
  /** Sends a request after the connection's `hello` and resolves with its reply; rejects with a BridgeError. */
  request(message: Message, route?: string): Promise<Reply>;
  /** Registers a handler for pushes of `kind` on `route`. */
  onPush(kind: Push["kind"], route: string, handler: PushHandler): () => void;
  /** Registers a handler for invalid pushes that name `route`. */
  onInvalidPush(route: string, handler: InvalidPushHandler): () => void;
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
  const invalidHandlers = new Map<string, Set<InvalidPushHandler>>();
  const connectionListeners = new Set<ConnectionListener>();
  let connected = false;
  let reported = false;
  let connection = 0;
  let disposed = false;
  // The hello of the current connection.
  let hello: { readonly connection: number; readonly promise: Promise<void> } | undefined;
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
    const handlers = pushHandlers.get(key(reading.push.kind, reading.push.route));
    if (!handlers) return;
    for (const handler of [...handlers]) {
      try { handler(reading.push); } catch (error) { reportBridgeError(error, reading.push.route); }
    }
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
        client: { name: clientName, version: viewsVersion },
      }, undefined);
      if (reply["protocol"] !== bridgeProtocol && (reply["protocol"] !== undefined || reply.ok)) {
        refused = reply["protocol"] === undefined ? "does not state a Views protocol" : `uses Views protocol ${String(reply["protocol"])}`;
        const error = protocolMismatchError(refused);
        reportProtocolMismatch(error, bridge);
        throw error;
      }
      if (!reply.ok) throw replyError(reply.error, undefined, "The Bridge refused the connection.");
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
    hello = { connection: current, promise };
    // A failed hello is sent again by the next request, unless the producer was refused.
    promise.catch(() => { if (hello?.promise === promise) hello = undefined; });
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
    async request(message, route) {
      if (refused !== undefined) throw refusal(route);
      if (!connected) throw new BridgeError("disconnected", "The Bridge is disconnected.", route === undefined ? {} : { route });
      try { await greet(); }
      catch (error) {
        if (refused !== undefined) throw refusal(route);
        throw error;
      }
      const reply = await send(message, route);
      // A producer that lost this connection's hello, such as after a restart behind the same transport.
      if (!reply.ok && reply.error?.reason === "no-hello") hello = undefined;
      return reply;
    },
    onPush: (kind, route, handler) => add(pushHandlers, key(kind, route), handler),
    onInvalidPush: (route, handler) => add(invalidHandlers, route, handler),
    onConnection(listener) {
      connectionListeners.add(listener);
      return () => { connectionListeners.delete(listener); };
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      stopMessages();
      stopConnection();
      connected = false;
      for (const listener of [...connectionListeners]) {
        try { listener(false); } catch (error) { reportBridgeError(error); }
      }
      connectionListeners.clear();
    },
  };

  const stopMessages = bridge.onMessage(message => dispatch(message));
  const stopConnection = bridge.onConnectionChange(next => {
    if (disposed || reported && next === connected) return;
    const first = !reported;
    reported = true;
    connected = next;
    if (next) connection++;
    hello = undefined;
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
