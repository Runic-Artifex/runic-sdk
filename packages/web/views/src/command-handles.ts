/// <reference lib="esnext.disposable" preserve="true" />
// Command handles that a View creates on demand (#1317): one CommandController per command of a generated client,
// created when the page first reads `commands.<name>`, which Angular's `injectCommands` and `useCommands` in React,
// Vue and Svelte adapt to their lifetimes.
import { createCommandController, type CommandController } from "./controller.js";
import { disposable } from "./lifetime.js";

/**
 * The type-only key under which a generated `{Name}Client` lists its commands, as `{Name}CommandName`: the
 * ViewModel's published commands, such as `"refresh" | "archive"`, and not its setters, `canX(arg)`, `startX`,
 * `recoverX`, `upload` or `dispose`. `injectCommands` and `useCommands` read it through {@link CommandName}. It exists
 * only in the types: import it with `import type`; no client has the property and the package has no such value at
 * run time.
 */
declare const viewCommands: unique symbol;
export type { viewCommands };

// Names a command handle set never offers: a framework or `await` reads them from any object.
type ReservedName = "then" | "toJSON" | keyof object;

/** The names of a generated client's commands, such as `"refresh" | "archive"`; `never` for a client that has none. */
export type CommandName<TClient> = typeof viewCommands extends keyof TClient
  ? Exclude<Extract<Exclude<TClient[typeof viewCommands], undefined>, keyof TClient & string>, ReservedName>
  : never;

/** The arguments of command `TName` of a generated client: `[id: bigint]` for `archive(id: bigint)`. */
export type CommandArguments<TClient, TName extends keyof TClient> =
  TClient[TName] extends (...args: infer TArgs) => unknown ? TArgs : never;

/** What command `TName` of a generated client resolves to: its state, or a `BridgeOutcome` for a declared failure. */
export type CommandResult<TClient, TName extends keyof TClient> =
  TClient[TName] extends (...args: never) => infer TReturn ? Awaited<TReturn> : never;

/** The controller behind one command handle; its run calls the command of the current client. */
export type CommandHandleController = CommandController<readonly unknown[], unknown, unknown>;

export interface CommandHandlesOptions<THandle> {
  /** The View's current client, or `undefined` while it is not connected. Read when a run starts. */
  readonly client: () => object | null | undefined;
  /** Makes the binding's handle of a command, the first time the page reads its name. */
  readonly handle: (controller: CommandHandleController, name: string) => THandle;
  /**
   * Makes the handle again on the first read after its command's state changed, for a binding whose handles are
   * snapshots of their state, such as React's. Otherwise a name's handle is made once.
   */
  readonly snapshots?: boolean;
}

/** The command handles of one View, which a binding hands out as `commands`. */
export interface CommandHandles<THandle> {
  /**
   * One handle per command name, made when the page first reads the name and kept until `dispose()`. A name is a
   * string that starts with a letter; `then`, `toJSON` and the names of `Object.prototype` are not command names.
   */
  readonly handles: Readonly<Record<string, THandle>>;
  /** Counts the changes of the handles' state, for a binding that renders from a snapshot, such as React's. */
  readonly version: number;
  /** Calls `listener` after each change of any handle's state, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /** Stops publishing state; runs still in flight settle as usual. A handle made afterwards publishes nothing. */
  dispose(): void;
}

const reserved = new Set(["then", "toJSON", ...Object.getOwnPropertyNames(Object.prototype)]);
const commandName = /^\p{L}/u;

/** True when a handle set offers `name`: a string that starts with a letter, other than a reserved name. */
function isCommandName(name: string | symbol): name is string {
  return typeof name === "string" && commandName.test(name) && !reserved.has(name);
}

/**
 * Makes the command handles of a View for a binding: `handles.refresh` is a handle whose controller runs
 * `client()?.refresh(...)`. Before the client connected, a run resolves to `undefined` and sets no `error`, as
 * `createCommandController(() => client?.refresh())` does. A name that the connected client has no method for ends
 * the run in `error` with a `TypeError`. Each handle has its own `pending`, `error` and `failure`.
 * A `using` declaration disposes it where the engine has `Symbol.dispose`.
 */
export function createCommandHandles<THandle>(options: CommandHandlesOptions<THandle>): CommandHandles<THandle> & Disposable {
  interface Entry {
    readonly controller: CommandHandleController & Disposable;
    handle: THandle | undefined;
  }
  const entries = new Map<string, Entry>();
  const listeners = new Set<() => void>();
  let disposed = false;
  let version = 0;

  function entry(name: string): Entry {
    let found = entries.get(name);
    if (found) return found;
    const controller = createCommandController((...args: readonly unknown[]) => {
      const client = options.client();
      if (client === undefined || client === null) return undefined;
      const command = (client as Readonly<Record<string, unknown>>)[name];
      if (typeof command !== "function") throw new TypeError(`The client has no command named "${name}".`);
      return command.apply(client, args) as unknown;
    });
    found = { controller, handle: undefined };
    entries.set(name, found);
    if (disposed) controller.dispose();
    else {
      const created = found;
      controller.subscribe(() => {
        if (options.snapshots) created.handle = undefined;
        version++;
        for (const listener of [...listeners]) listener();
      });
    }
    return found;
  }

  // A null prototype, so Svelte's `$state` and similar deep proxies leave the set alone. Other keys, such as a
  // framework's marker, read and write the target.
  const handles = new Proxy(Object.create(null) as Record<string, THandle>, {
    get(target, key, receiver) {
      if (!isCommandName(key)) return Reflect.get(target, key, receiver);
      const found = entry(key);
      return found.handle ??= options.handle(found.controller, key);
    },
    set(target, key, value, receiver) { return !isCommandName(key) && Reflect.set(target, key, value, receiver); },
    defineProperty(target, key, descriptor) { return !isCommandName(key) && Reflect.defineProperty(target, key, descriptor); },
    deleteProperty(target, key) { return !isCommandName(key) && Reflect.deleteProperty(target, key); },
  });

  return disposable<CommandHandles<THandle>>({
    handles,
    get version() { return version; },
    subscribe(listener) {
      if (disposed) return () => {};
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      listeners.clear();
      for (const { controller } of entries.values()) controller.dispose();
    },
  });
}
