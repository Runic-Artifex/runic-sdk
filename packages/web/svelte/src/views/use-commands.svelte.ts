import { untrack } from "svelte";
import { createCommandHandles, type CommandArguments, type CommandName, type CommandResult } from "@runic-artifex/views";
import type { CommandHandle } from "./use-command.svelte.js";

/** The View whose commands a component runs: a `useView` handle, or any client. */
export interface CommandsView<TClient extends object> {
  readonly client: TClient | undefined;
}

/** One {@link CommandHandle} per command of a generated client, such as `commands.refresh` and `commands.archive`. */
export type CommandHandles<TClient> = {
  readonly [TName in CommandName<TClient>]: CommandHandle<CommandArguments<TClient, TName>, CommandResult<TClient, TName>>;
};

/**
 * The commands of a View as {@link CommandHandle}s while the calling component is mounted, so it calls a command
 * without a `useCommand` per command. Call it during component initialization.
 *
 * ```svelte
 * const orders = useView(() => page);
 * const commands = useCommands(() => orders);
 * <button disabled={commands.refresh.pending} onclick={() => commands.refresh.run()}>Refresh</button>
 * <button onclick={() => commands.archive.run(row.id)}>Archive</button>
 * ```
 *
 * The names and arguments are typed from the generated client's commands (`{View}CommandName`); setters, `canX(arg)`,
 * `startX`, `recoverX`, `upload` and `dispose` are not offered. A handle is made when the component first reads its
 * name and kept until the component is destroyed. Each has its own `pending`, `error` and `failure`, reactive
 * properties as `useCommand`'s are. A run calls the client connected when it starts, and before the client connected
 * it resolves to `undefined`. `run()` never rejects: a failed run ends in `error`. Destroying the component stops the
 * handles' state, while a run still in flight resolves as usual.
 */
export function useCommands<TClient extends object>(view: () => CommandsView<TClient>): CommandHandles<TClient> {
  // One version for every handle, created here: a handle is often made while the markup first reads it, and Svelte does
  // not track state that the running effect itself created.
  let version = $state.raw(0);
  const commands = createCommandHandles({
    client: () => untrack(() => view().client),
    handle: controller => {
      const read = () => {
        void version;
        return controller.current;
      };
      return {
        run: controller.run,
        get pending() { return read().pending; },
        get error() { return read().error; },
        get failure() { return read().failure; },
        reset: controller.reset,
      };
    },
  });
  const unsubscribe = commands.subscribe(() => { version = commands.version; });
  // A pre effect runs when it is created, so the teardown exists even if the owner is destroyed before its first
  // flush (see useOperation). It reads no state, so it never re-runs.
  $effect.pre(() => () => { unsubscribe(); commands.dispose(); });
  return commands.handles as unknown as CommandHandles<TClient>;
}
