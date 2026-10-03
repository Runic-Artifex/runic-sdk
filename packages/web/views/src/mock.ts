import type { RunicBridgeClient } from "./transport.js";

/** A wire state as .NET serializes it, without `revision`. */
export type MockState = Record<string, unknown>;

/**
 * Answers one route suffix of a mock View. A returned object is merged into the
 * state, a boolean answers a `Can{Command}` query, and a string is the raw reply.
 * A thrown error becomes a failed reply; an error with a `kind` keeps that kind.
 */
export type MockRoute = (state: MockState, ...args: unknown[]) =>
  MockState | boolean | string | void | Promise<MockState | boolean | string | void>;

export interface MockViewDefinition {
  readonly state: MockState;
  /** Handlers by route suffix, such as `Increment`, `SetStep` or `CanSave`. */
  readonly routes?: Readonly<Record<string, MockRoute>>;
}

export interface MockView {
  /** The current wire state. */
  readonly state: MockState;
  /** Merges a patch, advances the revision and pushes the state like a .NET publication. */
  update(patch: MockState | ((state: MockState) => MockState)): void;
}

export interface MockCall {
  readonly name: string;
  readonly args: readonly unknown[];
}

/** An in-memory `__runicBridge` that answers generated-client routes without .NET. */
export interface MockBridge extends Required<RunicBridgeClient> {
  /** Every call in order, including rejected ones. */
  readonly calls: readonly MockCall[];
  /** Serves `{route}Snapshot`, `Mount`, `Unmount`, `Set{Property}`, `Can{Command}` and the given routes. */
  view(route: string, definition: MockViewDefinition): MockView;
  /** Serves a whole route name, such as `__runicOperationStatus`. */
  route(name: string, handler: (...args: unknown[]) => string | Promise<string>): void;
  /** Rejects further calls until `reconnect()`. */
  disconnect(): void;
  /** Restores calls and notifies reconnect listeners, as a host transport does after a reconnect. */
  reconnect(): void;
}

interface RegisteredView {
  readonly route: string;
  readonly routes: Readonly<Record<string, MockRoute>>;
  state: MockState;
  revision: number;
}

export function createMockBridge(): MockBridge {
  const views = new Map<string, RegisteredView>();
  const routes = new Map<string, (...args: unknown[]) => string | Promise<string>>();
  const reconnectListeners = new Set<() => void>();
  const calls: MockCall[] = [];
  let connected = true;
  let revision = 0;

  const envelope = (view: RegisteredView, error?: { kind: string; message: string }) => JSON.stringify({
    ok: error === undefined, state: { ...view.state, revision: view.revision }, error: error ?? null, protocol: 1,
  });
  const commit = (view: RegisteredView, next: MockState) => { view.state = next; view.revision = ++revision; };
  const lowerFirst = (value: string) => value.charAt(0).toLowerCase() + value.slice(1);

  async function answer(view: RegisteredView, suffix: string, args: unknown[]): Promise<string | undefined> {
    const handler = Object.hasOwn(view.routes, suffix) ? view.routes[suffix] : undefined;
    if (handler) {
      try {
        const result = await handler(view.state, ...args);
        if (typeof result === "boolean") return String(result);
        if (typeof result === "string") return result;
        if (result) commit(view, { ...view.state, ...result });
        return envelope(view);
      } catch (error) {
        const kind = typeof (error as { kind?: unknown })?.kind === "string" ? (error as { kind: string }).kind : "failed";
        return envelope(view, { kind, message: error instanceof Error ? error.message : String(error) });
      }
    }
    if (suffix === "Snapshot") return envelope(view);
    if (suffix === "Mount" || suffix === "Unmount") return "ok";
    if (/^Set[A-Z]/.test(suffix)) { commit(view, { ...view.state, [lowerFirst(suffix.slice(3))]: args[0] }); return envelope(view); }
    if (/^Can[A-Z]/.test(suffix)) return "true";
    return undefined;
  }

  return {
    calls,
    isConnected: () => connected,
    async call(name, ...args) {
      calls.push({ name, args });
      if (!connected) throw new Error("The mock Bridge is disconnected.");
      const raw = routes.get(name);
      if (raw) return await raw(...args);
      // The longest route prefix wins, so `counter` does not shadow `counterCompact`.
      for (const view of [...views.values()].sort((left, right) => right.route.length - left.route.length)) {
        if (!name.startsWith(view.route)) continue;
        const reply = await answer(view, name.slice(view.route.length), args);
        if (reply !== undefined) return reply;
        return envelope(view, { kind: "failed", message: `The mock View ${view.route} has no ${name.slice(view.route.length)} route.` });
      }
      throw new Error(`The mock Bridge has no route ${name}.`);
    },
    onReconnect(listener) {
      reconnectListeners.add(listener);
      return () => { reconnectListeners.delete(listener); };
    },
    view(route, definition) {
      const view: RegisteredView = { route, routes: definition.routes ?? {}, state: { ...definition.state }, revision: ++revision };
      views.set(route, view);
      return {
        get state() { return view.state; },
        update(patch) {
          commit(view, typeof patch === "function" ? patch(view.state) : { ...view.state, ...patch });
          const callback = (globalThis as unknown as Record<string, unknown>)[`__${route}Changed`];
          if (typeof callback === "function") callback({ ...view.state, revision: view.revision });
        },
      };
    },
    route(name, handler) { routes.set(name, handler); },
    disconnect() { connected = false; },
    reconnect() {
      connected = true;
      for (const listener of [...reconnectListeners]) listener();
    },
  };
}

/**
 * Installs a mock as `window.__runicBridge`, so generated clients connect to it
 * instead of waiting for a .NET host. Call it before the first `connect*()`.
 */
export function installMockBridge(bridge: MockBridge = createMockBridge()): MockBridge {
  (globalThis as unknown as { __runicBridge?: RunicBridgeClient }).__runicBridge = bridge;
  return bridge;
}
